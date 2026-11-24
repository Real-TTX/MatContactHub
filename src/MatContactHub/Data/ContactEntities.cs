namespace MatContactHub.Data;

/// <summary>What a person may do in an address book (<see cref="AddressBookAccess"/>). Administrators may do everything.</summary>
public enum AddressBookRole
{
    None = 0,

    /// <summary>May see the contacts of the address book.</summary>
    Viewer = 1,

    /// <summary>May also create, change and delete the manual contacts of the address book.</summary>
    Editor = 2,
}

/// <summary>
/// An address book: a set of contacts (manual ones and those of the data sources that write into it). With <see cref="LdapEnabled"/> it is served by the LDAP server as
/// <c>ou=&lt;LdapName&gt;,&lt;BaseDn&gt;</c>; with <see cref="AppsEnabled"/> the apps bound through <c>contacts.directory</c> read it.
/// </summary>
public sealed class AddressBook : SoftEntity
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The value of the RDN in the LDAP tree (<c>ou=&lt;LdapName&gt;</c>): letters, digits, <c>-</c> and <c>_</c>. Unique among the address books that exist.</summary>
    public string LdapName { get; set; } = string.Empty;

    /// <summary>The LDAP server serves this address book.</summary>
    public bool LdapEnabled { get; set; }

    /// <summary>The apps bound through <c>contacts.directory</c> (caller-ID, search) read this address book.</summary>
    public bool AppsEnabled { get; set; }

    /// <summary>Every signed-in person is a viewer.</summary>
    public bool IsShared { get; set; }

    /// <summary>An optional accent (<c>#0f766e</c>) for the lists.</summary>
    public string? Color { get; set; }

    public int SortOrder { get; set; }

    public List<AddressBookAccess> Access { get; set; } = [];
}

/// <summary>The role of a person in an address book.</summary>
public sealed class AddressBookAccess : Entity
{
    public long AddressBookId { get; set; }

    public AddressBook? AddressBook { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public AddressBookRole Role { get; set; } = AddressBookRole.Viewer;
}

/// <summary>
/// A contact. A manual one (<see cref="DataSourceId"/> null) is edited in the interface; a synced one belongs to its data source (identified there by
/// <see cref="ExternalKey"/>) and is read-only: only <see cref="Hidden"/> is changed here (the sync never touches it). <see cref="Token"/> is stable for its whole life:
/// the LDAP <c>entryUUID</c> / <c>uid</c> and the vCard <c>UID</c>.
/// </summary>
public sealed class Contact : SoftEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long AddressBookId { get; set; }

    public AddressBook? AddressBook { get; set; }

    /// <summary>The data source the contact comes from; null = a manual contact.</summary>
    public long? DataSourceId { get; set; }

    public DataSource? DataSource { get; set; }

    /// <summary>The key of the record in the source (the value of <see cref="DataSource.KeyField"/>); unique per data source.</summary>
    public string? ExternalKey { get; set; }

    /// <summary>Not served by LDAP and the directory API (still in the lists of the interface).</summary>
    public bool Hidden { get; set; }

    public string? Salutation { get; set; }

    /// <summary>The academic title (Dr., Prof.).</summary>
    public string? Title { get; set; }

    public string? FirstName { get; set; }

    public string? MiddleName { get; set; }

    public string? LastName { get; set; }

    /// <summary>The name shown everywhere; computed when empty ("First Last", else the company).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string? Company { get; set; }

    public string? Department { get; set; }

    public string? JobTitle { get; set; }

    public string? Email { get; set; }

    public string? Email2 { get; set; }

    public string? Email3 { get; set; }

    public string? PhoneBusiness { get; set; }

    public string? PhoneBusiness2 { get; set; }

    public string? PhoneMobile { get; set; }

    public string? PhoneHome { get; set; }

    public string? PhoneOther { get; set; }

    public string? Fax { get; set; }

    public string? Street { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Country { get; set; }

    public string? Website { get; set; }

    public DateOnly? Birthday { get; set; }

    public string? CustomerNumber { get; set; }

    public string? Notes { get; set; }

    public string? Custom1 { get; set; }

    public string? Custom2 { get; set; }

    public string? Custom3 { get; set; }

    public string? Custom4 { get; set; }

    public string? Custom5 { get; set; }

    /// <summary>The hash of the mapped values of the last sync: an unchanged record is skipped.</summary>
    public string? SyncHash { get; set; }

    public DateTime? LastSyncDate { get; set; }
}

/// <summary>A pair of contacts marked "not a duplicate" (the smaller id first).</summary>
public sealed class DuplicateIgnore : Entity
{
    public long ContactIdLow { get; set; }

    public long ContactIdHigh { get; set; }
}
