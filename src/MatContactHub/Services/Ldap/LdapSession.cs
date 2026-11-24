using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MatContactHub.Services.Ldap;

/// <summary>What a session needs from the server around it (the host in the app, a fake in the tests).</summary>
public interface ILdapServerContext
{
    LdapServerOptions Options { get; }

    /// <summary>The snapshot in force (a search keeps the one it started with).</summary>
    LdapDirectory Directory { get; }

    /// <summary>The certificate of LDAPS and StartTLS, or null while there is none.</summary>
    X509Certificate2? Certificate { get; }

    PasswordService Passwords { get; }

    LdapBindGuard Guard { get; }

    LdapQueryLog Log { get; }

    ILogger Logger { get; }

    /// <summary>An account bound (the host notes the time of the last bind).</summary>
    void Bound(LdapAccountInfo account, string client);

    /// <summary>A bind failed (the host writes the log and, now and then, the activity).</summary>
    void BindFailed(string client, string name, int failures);
}

/// <summary>A connection as the page shows it.</summary>
public sealed record LdapConnectionInfo(string Client, bool Secure, string? BindName, DateTime SinceUtc, long Requests);

/// <summary>
/// One LDAP connection. It reads one message after the other (an idle connection is closed after <see cref="LdapServerOptions.IdleTimeout"/>, a message that
/// started must be complete within <see cref="LdapServerOptions.ReadTimeout"/>), answers Bind, StartTLS, "Who am I?" and the write operations (unwillingToPerform)
/// in order and runs the searches side by side (up to <see cref="LdapServerOptions.MaxOperationsPerConnection"/>, each cancelled by an Abandon of its message ID).
/// The answers are written one message at a time under a lock. Whatever arrives that is not LDAP ends the connection with a notice of disconnection
/// (protocolError); nothing a client sends can throw out of <see cref="RunAsync"/>.
/// </summary>
public sealed class LdapSession
{
    private static readonly HashSet<string> SearchControls = [LdapOids.PagedResults, LdapOids.ManageDsaIt];
    private static readonly HashSet<string> OtherControls = [LdapOids.ManageDsaIt];

    private readonly ILdapServerContext _ctx;
    private readonly Socket _socket;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _operations = new();
    private readonly CancellationTokenSource _closing;
    private readonly LdapPagedSearches _paged;
    private Stream _stream;
    private LdapIdentity _identity = LdapIdentity.Anonymous;
    private long _lastActivity = Environment.TickCount64;
    private long _requests;

    public LdapSession(ILdapServerContext ctx, Socket socket, string client, bool ldaps, CancellationToken stop)
    {
        _ctx = ctx;
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _closing = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _paged = new LdapPagedSearches(ctx.Options.MaxPagedSearchesPerConnection);
        Client = client;
        Ldaps = ldaps;
    }

    public string Client { get; }

    /// <summary>Came through the LDAPS port.</summary>
    public bool Ldaps { get; }

    /// <summary>TLS is on (LDAPS, or after StartTLS).</summary>
    public bool Secure { get; private set; }

    public DateTime SinceUtc { get; } = DateTime.UtcNow;

    public LdapConnectionInfo Info => new(Client, Secure, _identity.AccountName, SinceUtc, Interlocked.Read(ref _requests));

    /// <summary>Ends the connection (the server stops, the listener was switched off).</summary>
    public void Close()
    {
        try
        {
            _closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
    }

    public async Task RunAsync()
    {
        try
        {
            if (Ldaps && !await HandshakeAsync())
            {
                return;
            }

            while (!_closing.IsCancellationRequested)
            {
                var pdu = await ReadMessageAsync();
                if (pdu is null)
                {
                    return;
                }

                LdapMessage message;
                try
                {
                    message = LdapCodec.Decode(pdu);
                }
                catch (LdapProtocolException ex)
                {
                    _ctx.Logger.LogInformation("LDAP {Client}: a message that is not LDAP ({Reason}); the connection is closed.", Client, ex.Message);
                    await NoticeAsync(LdapResultCode.ProtocolError, "The message is not valid LDAP.");
                    return;
                }

                Interlocked.Increment(ref _requests);
                Touch();
                if (!await DispatchAsync(message))
                {
                    return;
                }
            }
        }
        catch (LdapFrameException ex)
        {
            _ctx.Logger.LogInformation("LDAP {Client}: {Reason}; the connection is closed.", Client, ex.Message);
            await NoticeAsync(LdapResultCode.ProtocolError, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException
                                       or AuthenticationException or InvalidOperationException)
        {
            // the client went away, a timeout, the server stops
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "LDAP {Client}: the connection ended with an error.", Client);
        }
        finally
        {
            foreach (var op in _operations.Values)
            {
                Cancel(op);
            }

            Close();
            _paged.Clear();
        }
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The next message, or null when the client closed the connection or it was idle for too long (then after a notice of disconnection).</summary>
    private async Task<byte[]?> ReadMessageAsync()
    {
        var first = new byte[1];
        var read = _stream.ReadAsync(first, _closing.Token).AsTask();
        _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        while (true)
        {
            var idle = _ctx.Options.IdleTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));
            if (idle <= TimeSpan.Zero && _operations.IsEmpty)
            {
                await NoticeAsync(LdapResultCode.Unavailable, "The connection was idle for too long.");
                return null;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var delay = Task.Delay(idle > TimeSpan.FromSeconds(1) ? idle : TimeSpan.FromSeconds(1), wait.Token);
            var done = await Task.WhenAny(read, delay);
            await wait.CancelAsync();
            if (done == read)
            {
                break;
            }

            if (_closing.IsCancellationRequested)
            {
                return null;
            }
        }

        if (await read == 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(_ctx.Options.ReadTimeout);
        return await LdapPduReader.ReadRestAsync(_stream, first[0], _ctx.Options.MaxPduBytes, timeout.Token);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    // ------------------------------------------------------------------ the operations

    /// <summary>Answers one message; false ends the connection.</summary>
    private async Task<bool> DispatchAsync(LdapMessage m)
    {
        if (m.MessageId == 0 || _operations.ContainsKey(m.MessageId))
        {
            await NoticeAsync(LdapResultCode.ProtocolError, m.MessageId == 0 ? "The message ID 0 is reserved." : "The message ID is in use.");
            return false;
        }

        switch (m.Op)
        {
            case UnbindRequest:
                return false;

            case AbandonRequest abandon:
                if (_operations.TryGetValue(abandon.MessageId, out var op))
                {
                    Cancel(op);
                }

                return true;

            case BindRequest bind:
                if (UnknownCritical(m, OtherControls) is { } bindControl)
                {
                    await SendAsync(new LdapMessage(m.MessageId, new BindResponse(Unsupported(bindControl))));
                    return true;
                }

                // RFC 4511 4.2.1: the operations before a bind complete first
                await WaitForOperationsAsync();
                await SendAsync(new LdapMessage(m.MessageId, new BindResponse(await BindAsync(bind))));
                return true;

            case ExtendedRequest extended:
                if (UnknownCritical(m, OtherControls) is { } extendedControl)
                {
                    await SendAsync(new LdapMessage(m.MessageId, new ExtendedResponse(Unsupported(extendedControl))));
                    return true;
                }

                return await ExtendedAsync(m.MessageId, extended);

            case WriteRequest write:
                await SendAsync(new LdapMessage(m.MessageId, new WriteResponse(write.Response,
                    UnknownCritical(m, OtherControls) is { } writeControl ? Unsupported(writeControl)
                        : new LdapResult(LdapResultCode.UnwillingToPerform, string.Empty, "The directory is read-only."))));
                return true;

            case SearchRequest search:
                if (UnknownCritical(m, SearchControls) is { } searchControl)
                {
                    await SendAsync(new LdapMessage(m.MessageId, new SearchResultDone(Unsupported(searchControl))));
                    return true;
                }

                if (_operations.Count >= _ctx.Options.MaxOperationsPerConnection)
                {
                    await SendAsync(new LdapMessage(m.MessageId,
                        new SearchResultDone(new LdapResult(LdapResultCode.Busy, string.Empty, "Too many operations on this connection."))));
                    return true;
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                _operations[m.MessageId] = cts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SearchAsync(m.MessageId, search, m.Controls, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // abandoned, or the connection ends: no answer (RFC 4511 4.11)
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                    {
                        Close();
                    }
                    catch (Exception ex)
                    {
                        _ctx.Logger.LogError(ex, "LDAP {Client}: the search {Id} failed.", Client, m.MessageId);
                        await TrySendAsync(new LdapMessage(m.MessageId, new SearchResultDone(new LdapResult(LdapResultCode.Other, string.Empty, "The search failed."))));
                    }
                    finally
                    {
                        _operations.TryRemove(m.MessageId, out _);
                        cts.Dispose();
                        Touch();
                    }
                });
                return true;

            default:
                // a response, or something else a client does not send
                await NoticeAsync(LdapResultCode.ProtocolError, "This is not a request.");
                return false;
        }
    }

    private static string? UnknownCritical(LdapMessage m, HashSet<string> known)
        => m.Controls.FirstOrDefault(c => c.Critical && !known.Contains(c.Oid))?.Oid;

    private static LdapResult Unsupported(string oid) => new(LdapResultCode.UnavailableCriticalExtension, string.Empty, $"The critical control {oid} is not supported.");

    private async Task WaitForOperationsAsync()
    {
        while (!_operations.IsEmpty && !_closing.IsCancellationRequested)
        {
            await Task.Delay(10, _closing.Token);
        }
    }

    private static void Cancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ------------------------------------------------------------------ bind

    private async Task<LdapResult> BindAsync(BindRequest bind)
    {
        var watch = Stopwatch.StartNew();
        var result = await AuthenticateAsync(bind);
        _ctx.Log.Add(new LdapLogEntry(DateTime.UtcNow, Client, string.IsNullOrEmpty(bind.Name) ? null : Cut(bind.Name, 200), "bind", null, null, null, 0, result.Code,
            watch.Elapsed.TotalMilliseconds, Secure));
        return result;
    }

    private async Task<LdapResult> AuthenticateAsync(BindRequest bind)
    {
        // a bind starts as anonymous; only a successful one changes that (RFC 4511 4.2.1)
        _identity = LdapIdentity.Anonymous;
        _paged.Clear();
        var directory = _ctx.Directory;
        if (bind.Version is not (2 or 3))
        {
            return new LdapResult(LdapResultCode.ProtocolError, string.Empty, "Only LDAP version 3 is supported.");
        }

        if (bind.SaslMechanism is not null)
        {
            return new LdapResult(LdapResultCode.AuthMethodNotSupported, string.Empty, "Only simple binds are supported.");
        }

        var password = Encoding.UTF8.GetString(bind.Password);
        if (string.IsNullOrWhiteSpace(bind.Name))
        {
            if (password.Length > 0)
            {
                return new LdapResult(LdapResultCode.InvalidCredentials, string.Empty, "A password without a name.");
            }

            return directory.Settings.AllowAnonymous
                ? LdapResult.Ok
                : new LdapResult(LdapResultCode.InappropriateAuthentication, string.Empty, "Anonymous binds are off: bind with an LDAP account.");
        }

        if (password.Length == 0)
        {
            // an unauthenticated bind (RFC 4513 5.1.2) would look like a success to a careless client
            return new LdapResult(LdapResultCode.UnwillingToPerform, string.Empty, "A bind with a name needs a password.");
        }

        if (_ctx.Guard.IsBlocked(Client))
        {
            await Task.Delay(_ctx.Options.BindFailureDelay, _closing.Token);
            return new LdapResult(LdapResultCode.InvalidCredentials, string.Empty, "Too many failed binds from this address: try again later.");
        }

        var name = AccountNameOf(bind.Name, directory.Settings.BaseDn);
        var account = name is null ? null : directory.Accounts.GetValueOrDefault(name);
        bool ok;
        if (account is null)
        {
            _ctx.Passwords.SpendTime(password);
            ok = false;
        }
        else
        {
            ok = _ctx.Passwords.Verify(account.PasswordHash, password) && account.Enabled;
        }

        if (!ok || account is null)
        {
            var failures = _ctx.Guard.Fail(Client);
            _ctx.BindFailed(Client, Cut(name ?? bind.Name, 200), failures);
            await Task.Delay(_ctx.Options.BindFailureDelay, _closing.Token);
            return new LdapResult(LdapResultCode.InvalidCredentials, string.Empty, "The name or the password is wrong.");
        }

        _identity = new LdapIdentity(account.Name);
        _ctx.Bound(account, Client);
        return LdapResult.Ok;
    }

    /// <summary>
    /// The account a bind name means: <c>starface</c>, <c>cn=starface,&lt;base DN&gt;</c>, <c>uid=starface,&lt;base DN&gt;</c> (or <c>cn=starface</c> alone), in small
    /// letters; null for any other DN.
    /// </summary>
    public static string? AccountNameOf(string bindName, string baseDn)
    {
        var text = bindName.Trim();
        if (!text.Contains('='))
        {
            return text.ToLowerInvariant();
        }

        var rdns = LdapDn.Parse(text);
        if (rdns is not { Count: > 0 } || rdns[0].Attribute is not ("cn" or "uid"))
        {
            return null;
        }

        var rest = LdapDn.Join(rdns.Skip(1));
        if (rest.Length > 0 && rest != LdapDn.Normalize(baseDn))
        {
            return null;
        }

        return rdns[0].Value.Trim().ToLowerInvariant();
    }

    // ------------------------------------------------------------------ extended operations

    private async Task<bool> ExtendedAsync(int id, ExtendedRequest request)
    {
        switch (request.Name)
        {
            case LdapOids.StartTls:
                return await StartTlsAsync(id);
            case LdapOids.WhoAmI:
                var account = _ctx.Directory.AccountOf(_identity);
                var who = account is null ? string.Empty : $"dn:cn={LdapDn.Escape(account.Name)},{_ctx.Directory.Settings.BaseDn}";
                await SendAsync(new LdapMessage(id, new ExtendedResponse(LdapResult.Ok, null, Encoding.UTF8.GetBytes(who))));
                return true;
            default:
                // RFC 4511 4.12: an extended operation the server does not know is answered with protocolError
                await SendAsync(new LdapMessage(id, new ExtendedResponse(new LdapResult(LdapResultCode.ProtocolError, string.Empty,
                    $"The extended operation {Cut(request.Name, 100)} is not supported."))));
                return true;
        }
    }

    private async Task<bool> StartTlsAsync(int id)
    {
        LdapResult? refuse = null;
        var certificate = _ctx.Certificate;
        if (Secure)
        {
            refuse = new LdapResult(LdapResultCode.OperationsError, string.Empty, "TLS is on already.");
        }
        else if (!_ctx.Directory.Settings.StartTlsEnabled)
        {
            refuse = new LdapResult(LdapResultCode.ProtocolError, string.Empty, "StartTLS is off.");
        }
        else if (certificate is null)
        {
            refuse = new LdapResult(LdapResultCode.Unavailable, string.Empty, "There is no certificate yet.");
        }
        else if (!_operations.IsEmpty)
        {
            refuse = new LdapResult(LdapResultCode.OperationsError, string.Empty, "Operations are still running.");
        }

        if (refuse is not null)
        {
            await SendAsync(new LdapMessage(id, new ExtendedResponse(refuse)));
            return true;
        }

        await SendAsync(new LdapMessage(id, new ExtendedResponse(LdapResult.Ok, LdapOids.StartTls)));
        return await HandshakeAsync();
    }

    /// <summary>TLS on the connection (LDAPS at once, StartTLS after its answer). False when the handshake fails (the connection ends).</summary>
    private async Task<bool> HandshakeAsync()
    {
        var certificate = _ctx.Certificate;
        if (certificate is null)
        {
            return false;
        }

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(_ctx.Options.HandshakeTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            _ctx.Logger.LogInformation("LDAP {Client}: the TLS handshake failed ({Reason}).", Client, ex.Message);
            await ssl.DisposeAsync();
            return false;
        }

        _stream = ssl;
        Secure = true;
        Touch();
        return true;
    }

    // ------------------------------------------------------------------ search

    private async Task SearchAsync(int id, SearchRequest request, IReadOnlyList<LdapControl> controls, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var directory = _ctx.Directory;
        var who = _identity;
        var serverLimit = Math.Max(1, directory.Settings.SizeLimit);
        var timeLimit = request.TimeLimit > 0 ? TimeSpan.FromSeconds(Math.Min(request.TimeLimit, _ctx.Options.MaxTimeLimit.TotalSeconds)) : _ctx.Options.MaxTimeLimit;
        var paged = controls.FirstOrDefault(c => c.Oid == LdapOids.PagedResults);

        LdapResult result;
        IReadOnlyList<LdapEntry> entries;
        List<LdapControl> responseControls = [];
        if (paged is not null)
        {
            int size;
            byte[] cookie;
            try
            {
                (size, cookie) = LdapCodec.DecodePagedResults(paged.Value);
            }
            catch (LdapProtocolException ex)
            {
                await SendAsync(new LdapMessage(id, new SearchResultDone(new LdapResult(LdapResultCode.ProtocolError, string.Empty, ex.Message))), ct);
                return;
            }

            var page = _paged.Next(directory, who, request, size, cookie, serverLimit, timeLimit, ct);
            result = page.Result;
            entries = page.Entries;
            responseControls.Add(new LdapControl(LdapOids.PagedResults, false, LdapCodec.EncodePagedResults(page.Total, page.Cookie)));
        }
        else
        {
            var limit = request.SizeLimit > 0 ? Math.Min(request.SizeLimit, serverLimit) : serverLimit;
            var outcome = LdapSearchEngine.Execute(directory, who, request, limit, timeLimit, ct);
            result = outcome.Result;
            entries = outcome.Entries;
        }

        var selection = new LdapAttributeSelection(request.Attributes);
        var batch = new List<LdapMessage>(64);
        foreach (var entry in entries)
        {
            batch.Add(new LdapMessage(id, selection.ToResult(entry, request.TypesOnly)));
            if (batch.Count == 64)
            {
                ct.ThrowIfCancellationRequested();
                await SendAsync(batch, ct);
                batch.Clear();
            }
        }

        batch.Add(new LdapMessage(id, new SearchResultDone(result), responseControls));
        ct.ThrowIfCancellationRequested();
        await SendAsync(batch, ct);
        _ctx.Log.Add(new LdapLogEntry(DateTime.UtcNow, Client, who.AccountName, "search", Cut(request.BaseDn, 300), request.Scope switch
        {
            LdapScope.BaseObject => "base",
            LdapScope.SingleLevel => "one",
            _ => "sub",
        }, Cut(request.Filter.ToString(), 500), entries.Count, result.Code, watch.Elapsed.TotalMilliseconds, Secure));
    }

    // ------------------------------------------------------------------ writing

    private Task SendAsync(LdapMessage message, CancellationToken ct = default) => SendAsync([message], ct);

    /// <summary>Writes messages in one go (under the lock, so the answers of parallel searches never mix inside a message). A client that does not read ends the connection.</summary>
    private async Task SendAsync(IReadOnlyList<LdapMessage> messages, CancellationToken ct = default)
    {
        var buffers = messages.Select(LdapCodec.Encode).ToList();
        await _write.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            timeout.CancelAfter(_ctx.Options.WriteTimeout);
            foreach (var buffer in buffers)
            {
                await _stream.WriteAsync(buffer, timeout.Token);
            }

            await _stream.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Close();
            throw new IOException("The client does not read its answers.");
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task TrySendAsync(LdapMessage message)
    {
        try
        {
            await SendAsync(message);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <summary>The unsolicited notice of disconnection (RFC 4511 4.4.1), best effort: the connection ends after it.</summary>
    private Task NoticeAsync(LdapResultCode code, string message)
        => TrySendAsync(new LdapMessage(0, new ExtendedResponse(new LdapResult(code, string.Empty, message), LdapOids.NoticeOfDisconnection)));

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
