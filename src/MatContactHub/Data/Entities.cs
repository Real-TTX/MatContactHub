namespace MatContactHub.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated. A deleted record is hidden by a query filter.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

public enum UserRole
{
    Admin = 1,
    User = 2,
}

public sealed class User : SoftEntity
{
    /// <summary>The address to sign in with; unique, stored in lower case.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsActive { get; set; } = true;

    /// <summary>The IANA time zone dates are shown in.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The language of the interface: en or de; empty = the language of the browser.</summary>
    public string Language { get; set; } = string.Empty;

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>standard, paper, contrast or oled.</summary>
    public string Scheme { get; set; } = "standard";

    /// <summary>The person of MatIdentity (the claim <c>sub</c>) this account is linked with; null = a local account only.</summary>
    public string? ExternalSubject { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>The ID token of a sign-in through MatIdentity (for the sign-out there); null for a local sign-in. A secret: never exported.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>The session of MatIdentity (the claim <c>sid</c>) this session started with: the back-channel logout ends it by this.</summary>
    public string? IdentitySessionId { get; set; }
}

/// <summary>The log of the administration (Administration → Activity): what was created, changed, deleted, synchronised. Never a secret.</summary>
public sealed class Activity : Entity
{
    public long? UserId { get; set; }

    /// <summary>An app bound through <c>contacts.directory</c> or the platform API that did it.</summary>
    public string? Consumer { get; set; }

    /// <summary>contact, addressbook, source, sync, ldap, user, setting …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The name of the item at the time (it may be gone later).</summary>
    public string? Target { get; set; }

    /// <summary>The id of the item (its kind follows from <see cref="Action"/>), for a link on the activity page.</summary>
    public long? TargetId { get; set; }

    public string? Detail { get; set; }

    public DateTime OccurredDate { get; set; }
}

/// <summary>How a telephone number is written (the LDAP server, the directory API; <c>Services/Contacts/PhoneNormalizer.Format</c>).</summary>
public enum NumberFormat
{
    /// <summary>As the contact has them (as typed or as the source delivered them).</summary>
    AsStored = 0,

    /// <summary><c>+493012345678</c></summary>
    E164 = 1,

    /// <summary><c>030 12345678</c> (the national format of <see cref="SystemSetting.DefaultCountry"/>; foreign numbers stay international).</summary>
    National = 2,

    /// <summary><c>00493012345678</c> (digits only, the international prefix as 00: what many PBXs dial).</summary>
    InternationalDigits = 3,
}

/// <summary>Where the certificate of LDAPS and StartTLS comes from.</summary>
public enum LdapCertificateSource
{
    /// <summary>Made by MatContactHub at the first start (kept in <c>/data/ldap/</c>).</summary>
    SelfSigned = 0,

    /// <summary>A PFX uploaded by an administrator (kept in <c>/data/ldap/</c>).</summary>
    Uploaded = 1,
}

/// <summary>
/// The settings of the whole installation (one row, Id 1; <c>Services/SettingsStore</c>). The LDAP columns belong to the LDAP module (<c>Services/Ldap/LdapSettingsService</c>),
/// <see cref="DefaultCountry"/> is shared by the phone numbers of everybody (sync, duplicates, LDAP).
/// </summary>
public sealed class SystemSetting : Entity
{
    /// <summary>Whether anyone may create an account (the first account of an installation can always sign up and becomes the administrator).</summary>
    public bool RegistrationOpen { get; set; }

    /// <summary>The address MatContactHub is reached at from outside (links in mails, the directory API); empty = the address of the request.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>The country of numbers without an international prefix (ISO 3166 alpha-2, <c>DE</c>): normalisation to E.164, the national format.</summary>
    public string DefaultCountry { get; set; } = "DE";

    /// <summary>How many days the history of the sync runs is kept.</summary>
    public int SyncRunDays { get; set; } = 90;

    /// <summary>The LDAP server answers at all (port 10389 in the container).</summary>
    public bool LdapEnabled { get; set; } = true;

    /// <summary>The extended operation StartTLS on the plain port.</summary>
    public bool LdapStartTlsEnabled { get; set; } = true;

    /// <summary>LDAPS on port 10636 in the container.</summary>
    public bool LdapsEnabled { get; set; } = true;

    /// <summary>The root of the directory: <c>dc=matcontacthub,dc=local</c>. Every LDAP-enabled address book is <c>ou=&lt;LdapName&gt;,&lt;BaseDn&gt;</c>.</summary>
    public string LdapBaseDn { get; set; } = "dc=matcontacthub,dc=local";

    /// <summary>Searches without a bind are answered (with every LDAP-enabled address book). Off by default.</summary>
    public bool LdapAllowAnonymous { get; set; }

    public NumberFormat LdapNumberFormat { get; set; } = NumberFormat.AsStored;

    /// <summary>The most entries one search answers (the client may ask for fewer).</summary>
    public int LdapSizeLimit { get; set; } = 1000;

    public LdapCertificateSource LdapCertificateSource { get; set; } = LdapCertificateSource.SelfSigned;

    /// <summary>The file name of the uploaded PFX in <c>/data/ldap/</c>; null = none.</summary>
    public string? LdapCertificateFile { get; set; }

    /// <summary>The password of the uploaded PFX, protected with DataProtection; null = none.</summary>
    public string? LdapCertificatePasswordProtected { get; set; }

    /// <summary>The certificate in use (for the page): its thumbprint, subject and end.</summary>
    public string? LdapCertificateThumbprint { get; set; }

    public string? LdapCertificateSubject { get; set; }

    public DateTime? LdapCertificateExpires { get; set; }
}
