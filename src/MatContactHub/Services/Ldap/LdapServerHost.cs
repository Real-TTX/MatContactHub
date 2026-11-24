namespace MatContactHub.Services.Ldap;

/// <summary>
/// The LDAP server (module A owns it; FOUNDATION STUB: listens on nothing yet). The real host listens on <see cref="LdapSettings.Port"/> (LDAP + StartTLS) and
/// <see cref="LdapSettings.SecurePort"/> (LDAPS) when the settings say so, keeps an in-memory snapshot of the directory that it rebuilds when
/// <c>Contacts.DirectoryChanges.Version</c> changes, and answers Bind, Search, Unbind, Abandon and StartTLS (<c>System.Formats.Asn1</c>).
/// </summary>
public sealed class LdapServerHost(ILogger<LdapServerHost> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("The LDAP server is not implemented yet: nothing listens on {Port} and {SecurePort}.", LdapSettings.Port, LdapSettings.SecurePort);
        return Task.CompletedTask;
    }
}
