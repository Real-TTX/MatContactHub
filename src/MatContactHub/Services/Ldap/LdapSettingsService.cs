using MatContactHub.Data;
using MatContactHub.Localization;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// Reads and writes the settings of the LDAP server in the one row of <see cref="SystemSetting"/> (module A owns this class). A save bumps
/// <c>Contacts.DirectoryChanges</c> (through <c>AppDbContext.SaveChanges</c>), so the server picks the new base DN and number format up.
/// </summary>
public static class LdapSettingsService
{
    /// <summary>The settings of the server; null unless the actor is an administrator (a person with the role, or the LDAP server itself).</summary>
    public static async Task<LdapSettings?> LoadAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.IsAdmin ? LdapSettings.From(await SettingsStore.LoadAsync(db, ct)) : null;

    /// <summary>
    /// Whether the server runs and its base DN: what a page of an address book shows about where the address book appears (its DN). For every signed-in person and
    /// app; null for a visitor.
    /// </summary>
    public static async Task<(bool Enabled, string BaseDn)?> DirectoryAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (actor.IsAnonymous)
        {
            return null;
        }

        var s = LdapSettings.From(await SettingsStore.LoadAsync(db, ct));
        return (s.Enabled, s.BaseDn);
    }

    /// <summary>Saves the settings (administrators only). Answers the field and the message of the first wrong value (nothing is saved then), else null.</summary>
    public static async Task<Outcome<LdapSettings>> SaveAsync(AppDbContext db, Actor actor, LdapSettings settings, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<LdapSettings>.Denied();
        }

        var baseDn = settings.BaseDn.Trim();
        if (!IsBaseDn(baseDn))
        {
            return Outcome<LdapSettings>.Fail(Text.T("Ldap.BaseDn.Invalid"));
        }

        if (settings.SizeLimit is < 1 or > 100000)
        {
            return Outcome<LdapSettings>.Fail(Text.T("Ldap.SizeLimit.Invalid"));
        }

        var country = settings.DefaultCountry.Trim().ToUpperInvariant();
        if (!Contacts.PhoneNormalizer.IsKnownCountry(country))
        {
            return Outcome<LdapSettings>.Fail(Text.T("System.DefaultCountry.Invalid"));
        }

        var s = await SettingsStore.LoadAsync(db, ct);
        s.LdapEnabled = settings.Enabled;
        s.LdapStartTlsEnabled = settings.StartTlsEnabled;
        s.LdapsEnabled = settings.LdapsEnabled;
        s.LdapBaseDn = baseDn;
        s.LdapAllowAnonymous = settings.AllowAnonymous;
        s.LdapNumberFormat = settings.NumberFormat;
        s.DefaultCountry = country;
        s.LdapSizeLimit = settings.SizeLimit;
        s.LdapCertificateSource = settings.CertificateSource;
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "ldap", "LDAP", ActivityLog.Text("Activity.Detail.Settings"), null, ct);
        return Outcome<LdapSettings>.Success(LdapSettings.From(s));
    }

    /// <summary>A plausible base DN: <c>attr=value</c> parts separated by commas (<c>dc=example,dc=com</c>, <c>o=Company</c>).</summary>
    public static bool IsBaseDn(string? dn)
        => !string.IsNullOrWhiteSpace(dn) && dn.Length <= 255 && dn.Split(',').All(IsRdn);

    private static bool IsRdn(string part)
    {
        var rdn = part.Trim();
        var equals = rdn.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 && equals < rdn.Length - 1 && rdn[..equals].All(char.IsAsciiLetterOrDigit);
    }
}
