using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using MatContactHub.Data;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Ldap;

/// <summary>What the tab General shows of the server.</summary>
public sealed record LdapServerStatus(
    bool Enabled, bool StartTls, bool Ldaps, int? Port, int? SecurePort, string? PortError, string? SecurePortError, int ConfiguredPort, int ConfiguredSecurePort,
    long Version, DateTime? BuiltUtc, int AddressBooks, int Contacts, int Accounts, bool HasCertificate, string? Error, IReadOnlyList<LdapConnectionInfo> Connections);

/// <summary>
/// The LDAP server in the app (a hosted service, also a singleton for the pages). It keeps the in-memory snapshot of the directory (<see cref="LdapDirectory"/>):
/// built at the start and again in the background whenever <see cref="DirectoryChanges.Version"/> changes (a burst of changes is one rebuild), then swapped at once,
/// so the searches never wait for the database. With every new snapshot it loads the certificate when it changed (LDAPS, StartTLS: <see cref="LdapCertificateService"/>)
/// and starts or stops the listeners as the settings say (<see cref="LdapServer.Apply"/>). It notes the time of a bind (at most once a minute per account, past
/// the change tracker: that is no change of the directory) and writes failed binds to the log and, at most every 15 minutes per address, to the activity.
/// </summary>
public sealed class LdapServerHost : BackgroundService, ILdapServerContext
{
    private static readonly TimeSpan BindStampEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailedBindActivityEvery = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtectionProvider _protection;
    private readonly LdapServer _server;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly ConcurrentDictionary<long, DateTime> _bindStamps = new();
    private readonly ConcurrentDictionary<string, DateTime> _failedBindActivity = new(StringComparer.Ordinal);
    private LdapDirectory _directory = LdapDirectoryBuilder.Empty;
    private X509Certificate2? _certificate;
    private string? _certificateStamp;
    private string? _error;

    public LdapServerHost(IServiceScopeFactory scopes, IConfiguration configuration, IDataProtectionProvider protection, PasswordService passwords, LdapQueryLog log,
        ILogger<LdapServerHost> logger)
    {
        _scopes = scopes;
        _protection = protection;
        Options = LdapServerOptions.From(configuration);
        Folder = LdapCertificateService.FolderOf(configuration);
        Passwords = passwords;
        Log = log;
        Logger = logger;
        Guard = new LdapBindGuard(Options.BindFailureLimit, Options.BindFailureWindow);
        Searches = new SemaphoreSlim(Options.MaxConcurrentSearches, Options.MaxConcurrentSearches);
        _server = new LdapServer(this);
    }

    public LdapServerOptions Options { get; }

    /// <summary>The folder of the certificate.</summary>
    public string Folder { get; }

    public LdapDirectory Directory => Volatile.Read(ref _directory);

    public X509Certificate2? Certificate => Volatile.Read(ref _certificate);

    public PasswordService Passwords { get; }

    public LdapBindGuard Guard { get; }

    public SemaphoreSlim Searches { get; }

    public LdapQueryLog Log { get; }

    public ILogger Logger { get; }

    /// <summary>The real ports (when the configuration says 0, the system chose them), null while a listener is off.</summary>
    public int? Port => _server.PlainPort;

    public int? SecurePort => _server.SecurePort;

    /// <summary>The state for the page (administrators; null for anybody else).</summary>
    public LdapServerStatus? Status(Actor actor)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var d = Directory;
        return new LdapServerStatus(d.Settings.Enabled, d.Settings.StartTlsEnabled, d.Settings.LdapsEnabled, _server.PlainPort, _server.SecurePort, _server.PlainError,
            _server.SecureError, Options.Port, Options.SecurePort, d.Version, d.Version == 0 ? null : d.BuiltUtc, d.Books.Count, d.ContactCount, d.Accounts.Count,
            Certificate is not null, _error, _server.Connections);
    }

    /// <summary>Waits until the snapshot is at least <paramref name="version"/> (the tests, a page that just saved). False after the timeout.</summary>
    public async Task<bool> WaitForVersionAsync(long version, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (Directory.Version < version)
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(20, ct);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DirectoryChanges.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                    _error = null;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    Logger.LogError(ex, "The LDAP directory could not be loaded; trying again soon.");
                }

                // the next change, or a look every minute (a listener that could not open its port tries again); a burst of changes is one rebuild
                await _changed.WaitAsync(_error is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(10), stoppingToken);
                await Task.Delay(200, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            DirectoryChanges.Changed -= OnChanged;
            await _server.StopAsync();
        }
    }

    private void OnChanged(long version)
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // a rebuild is due already
        }
    }

    /// <summary>A new snapshot when the directory changed, the certificate when it changed, the listeners as the settings say.</summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        var version = DirectoryChanges.Version;
        if (version != Directory.Version || Certificate is null)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var directory = await LdapDirectoryBuilder.LoadAsync(db, version, ct);
            await LoadCertificateAsync(db, ct);
            Volatile.Write(ref _directory, directory);
            Logger.LogDebug("LDAP snapshot {Version}: {Books} address books, {Contacts} contacts.", version, directory.Books.Count, directory.ContactCount);
        }

        var s = Directory.Settings;
        _server.Apply(s.Enabled, s.Enabled && s.LdapsEnabled && Certificate is not null);
    }

    private async Task LoadCertificateAsync(AppDbContext db, CancellationToken ct)
    {
        var stamp = await CertificateStampAsync(db, ct);
        if (Certificate is not null && stamp == _certificateStamp)
        {
            return;
        }

        try
        {
            var load = await LdapCertificateService.LoadAsync(db, _protection, Folder, ct);
            if (load.Problem is not null)
            {
                Logger.LogWarning("LDAP: the uploaded certificate cannot be read; the self-signed one is used.");
            }

            var old = Interlocked.Exchange(ref _certificate, load.Certificate);
            _certificateStamp = await CertificateStampAsync(db, ct);
            if (old is not null)
            {
                // connections that are in the middle of a handshake may still use it
                _ = Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
            }

            Logger.LogInformation("LDAP certificate {Thumbprint} ({Subject}), valid until {Expires:yyyy-MM-dd}.", load.Certificate.Thumbprint, load.Certificate.Subject,
                load.Certificate.NotAfter);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Logger.LogError(ex, "LDAP: no certificate (folder {Folder}); LDAPS and StartTLS are not available.", Folder);
        }
    }

    /// <summary>What names the certificate in force in the settings (a change of it means: load it again).</summary>
    private static async Task<string> CertificateStampAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.SystemSettings.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => new { x.LdapCertificateSource, x.LdapCertificateFile, x.LdapCertificateThumbprint, x.LdapCertificatePasswordProtected })
            .FirstOrDefaultAsync(ct);
        return s is null ? string.Empty : $"{s.LdapCertificateSource}|{s.LdapCertificateFile}|{s.LdapCertificateThumbprint}|{s.LdapCertificatePasswordProtected}";
    }

    // ------------------------------------------------------------------ ILdapServerContext

    public void Bound(LdapAccountInfo account, string client)
    {
        var now = DateTime.UtcNow;
        if (_bindStamps.TryGetValue(account.Id, out var last) && now - last < BindStampEvery)
        {
            return;
        }

        _bindStamps[account.Id] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // past the change tracker on purpose: the time of a bind is no change of the directory (no new snapshot for it)
                await db.LdapAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.LastBindDate, now));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "LDAP: the time of the bind of {Account} could not be saved.", account.Name);
            }
        });
    }

    public void BindFailed(string client, string name, int failures)
    {
        Logger.LogWarning("LDAP: failed bind as {Name} from {Client} ({Failures} in {Minutes} minutes).", name, client, failures, Options.BindFailureWindow.TotalMinutes);
        var now = DateTime.UtcNow;
        if (_failedBindActivity.TryGetValue(client, out var last) && now - last < FailedBindActivityEvery)
        {
            return;
        }

        if (_failedBindActivity.Count > 1000)
        {
            foreach (var (address, at) in _failedBindActivity.Where(p => now - p.Value >= FailedBindActivityEvery).ToList())
            {
                _failedBindActivity.TryRemove(new KeyValuePair<string, DateTime>(address, at));
            }
        }

        _failedBindActivity[client] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ActivityLog.AddAsync(db, Actor.System("ldap"), "ldap", name, ActivityLog.Text("Activity.Detail.FailedBind", client), null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "LDAP: the failed bind could not be written to the activity.");
            }
        });
    }
}
