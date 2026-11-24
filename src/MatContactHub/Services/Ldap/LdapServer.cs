using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// The listeners of the LDAP server (a <see cref="TcpListener"/> each, independent of Kestrel): the plain port (LDAP and StartTLS) and the LDAPS port, started and
/// stopped by <see cref="Apply"/>. Every accepted socket becomes an <see cref="LdapSession"/>, unless the server has <see cref="LdapServerOptions.MaxConnections"/>
/// connections, or the address <see cref="LdapServerOptions.MaxConnectionsPerAddress"/>: then it gets a notice of disconnection (busy). Stopping a listener ends
/// its connections. A port that cannot be opened (in use) is reported in <see cref="PlainError"/> / <see cref="SecureError"/> and tried again with the next Apply.
/// </summary>
public sealed class LdapServer(ILdapServerContext context) : IAsyncDisposable
{
    private readonly ILdapServerContext _ctx = context;
    private readonly ConcurrentDictionary<LdapSession, bool> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _perAddress = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private Listener? _plain;
    private Listener? _secure;

    /// <summary>The port the plain listener listens on (the real one when the configuration says 0), or null.</summary>
    public int? PlainPort => _plain?.Port;

    public int? SecurePort => _secure?.Port;

    public string? PlainError { get; private set; }

    public string? SecureError { get; private set; }

    public int ConnectionCount => _sessions.Count;

    public IReadOnlyList<LdapConnectionInfo> Connections => [.. _sessions.Keys.Select(s => s.Info).OrderBy(i => i.SinceUtc)];

    /// <summary>Starts or stops the two listeners so that they are as asked.</summary>
    public void Apply(bool plain, bool secure)
    {
        lock (_lock)
        {
            _plain = Switch(_plain, plain, _ctx.Options.Port, false, e => PlainError = e);
            _secure = Switch(_secure, secure, _ctx.Options.SecurePort, true, e => SecureError = e);
        }
    }

    public async Task StopAsync()
    {
        Listener? plain;
        Listener? secure;
        lock (_lock)
        {
            (plain, secure) = (_plain, _secure);
            (_plain, _secure) = (null, null);
        }

        plain?.Stop();
        secure?.Stop();
        foreach (var s in _sessions.Keys)
        {
            s.Close();
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_sessions.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private Listener? Switch(Listener? current, bool wanted, int port, bool secure, Action<string?> error)
    {
        if (wanted && current is not null)
        {
            return current;
        }

        if (!wanted)
        {
            if (current is not null)
            {
                current.Stop();
                CloseSessions(secure);
            }

            error(null);
            return null;
        }

        try
        {
            var listener = new Listener(this, port, secure);
            error(null);
            _ctx.Logger.LogInformation("The LDAP server listens on port {Port} ({Kind}).", listener.Port, secure ? "LDAPS" : "LDAP, StartTLS");
            return listener;
        }
        catch (SocketException ex)
        {
            _ctx.Logger.LogError("The LDAP server cannot listen on port {Port}: {Reason}", port, ex.Message);
            error(ex.Message);
            return null;
        }
    }

    /// <summary>Ends the connections of one listener (LDAPS ones, or the plain ones: StartTLS too).</summary>
    private void CloseSessions(bool ldaps)
    {
        foreach (var s in _sessions.Keys.Where(s => s.Ldaps == ldaps))
        {
            s.Close();
        }
    }

    private async Task AcceptAsync(TcpListener listener, bool secure, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _ctx.Logger.LogWarning("LDAP: accepting a connection failed: {Reason}", ex.Message);
                continue;
            }

            _ = Task.Run(() => ServeAsync(socket, secure, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(Socket socket, bool secure, CancellationToken ct)
    {
        var address = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        var client = address is null ? "?" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        try
        {
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (SocketException)
        {
        }

        var count = _perAddress.AddOrUpdate(client, 1, (_, n) => n + 1);
        try
        {
            if (_sessions.Count >= _ctx.Options.MaxConnections || count > _ctx.Options.MaxConnectionsPerAddress)
            {
                _ctx.Logger.LogWarning("LDAP: {Client} is refused, too many connections.", client);
                await RefuseAsync(socket, secure);
                return;
            }

            var session = new LdapSession(_ctx, socket, client, secure, ct);
            _sessions[session] = true;
            try
            {
                await session.RunAsync();
            }
            finally
            {
                _sessions.TryRemove(session, out _);
            }
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "LDAP: the connection of {Client} failed.", client);
        }
        finally
        {
            _perAddress.AddOrUpdate(client, 0, (_, n) => Math.Max(0, n - 1));
            _perAddress.TryRemove(new KeyValuePair<string, int>(client, 0));
            socket.Dispose();
        }
    }

    /// <summary>A notice of disconnection (busy) on a plain connection; an LDAPS one is just closed (the notice would need the handshake).</summary>
    private static async Task RefuseAsync(Socket socket, bool secure)
    {
        try
        {
            if (secure)
            {
                socket.Shutdown(SocketShutdown.Both);
                return;
            }

            var notice = LdapCodec.Encode(new LdapMessage(0, new ExtendedResponse(new LdapResult(LdapResultCode.Busy, string.Empty, "Too many connections."),
                LdapOids.NoticeOfDisconnection)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.SendAsync(notice, SocketFlags.None, timeout.Token);
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>One listening port with its accept loop.</summary>
    private sealed class Listener
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public Listener(LdapServer server, int port, bool secure)
        {
            _listener = Open(server._ctx.Options.Address, port);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => server.AcceptAsync(_listener, secure, _stop.Token));
        }

        public int Port { get; }

        public void Stop()
        {
            _stop.Cancel();
            _listener.Stop();
        }

        private static TcpListener Open(IPAddress address, int port)
        {
            var listener = new TcpListener(address, port);
            if (address.Equals(IPAddress.IPv6Any))
            {
                try
                {
                    listener.Server.DualMode = true;
                }
                catch (Exception ex) when (ex is SocketException or NotSupportedException)
                {
                    listener = new TcpListener(IPAddress.Any, port);
                }
            }

            try
            {
                listener.Start(128);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported && address.Equals(IPAddress.IPv6Any))
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start(128);
            }

            return listener;
        }
    }
}
