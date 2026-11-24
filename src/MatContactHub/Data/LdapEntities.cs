namespace MatContactHub.Data;

/// <summary>
/// An account of the LDAP server (a telephone system such as STARFACE, the phones, a mail client): it binds as <c>cn=&lt;Name&gt;,&lt;BaseDn&gt;</c> or as plain
/// <c>&lt;Name&gt;</c> and reads every LDAP-enabled address book (<see cref="AllAddressBooks"/>) or the ones it is given (<see cref="LdapAccountAddressBook"/>).
/// </summary>
public sealed class LdapAccount : SoftEntity
{
    /// <summary>The bind name (<c>starface</c>): ASCII in small letters (LDAP compares it without case; a check constraint enforces it, so lower-case what a person types),
    /// unique among the accounts that exist.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The hash of the password (the format of <c>PasswordService</c>).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    /// <summary>Reads every LDAP-enabled address book (else only those of <see cref="AddressBooks"/>).</summary>
    public bool AllAddressBooks { get; set; } = true;

    public DateTime? LastBindDate { get; set; }

    public List<LdapAccountAddressBook> AddressBooks { get; set; } = [];
}

/// <summary>An address book an LDAP account reads (when it does not read all).</summary>
public sealed class LdapAccountAddressBook : Entity
{
    public long LdapAccountId { get; set; }

    public LdapAccount? LdapAccount { get; set; }

    public long AddressBookId { get; set; }

    public AddressBook? AddressBook { get; set; }
}
