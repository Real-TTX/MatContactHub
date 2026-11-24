using MatContactHub.Data;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// The settings of the LDAP server (the LDAP columns of <see cref="SystemSetting"/> and the shared <see cref="SystemSetting.DefaultCountry"/>). The certificate itself
/// (file, password, thumbprint) is not part of it: module A handles it on its own (tab Certificate).
/// </summary>
public sealed record LdapSettings(
    bool Enabled,
    bool StartTlsEnabled,
    bool LdapsEnabled,
    string BaseDn,
    bool AllowAnonymous,
    NumberFormat NumberFormat,
    string DefaultCountry,
    int SizeLimit,
    LdapCertificateSource CertificateSource)
{
    /// <summary>The port of LDAP and StartTLS inside the container (the compose files map it to 389 or 7389).</summary>
    public const int Port = 10389;

    /// <summary>The port of LDAPS inside the container (the compose files map it to 636 or 7636).</summary>
    public const int SecurePort = 10636;

    public const string DefaultBaseDn = "dc=matcontacthub,dc=local";

    public const int DefaultSizeLimit = 1000;

    /// <summary>The folder of the certificate in the data folder (<c>/data/ldap</c>).</summary>
    public const string Folder = "ldap";

    public static LdapSettings From(SystemSetting s) => new(s.LdapEnabled, s.LdapStartTlsEnabled, s.LdapsEnabled, s.LdapBaseDn, s.LdapAllowAnonymous, s.LdapNumberFormat,
        s.DefaultCountry, s.LdapSizeLimit, s.LdapCertificateSource);
}
