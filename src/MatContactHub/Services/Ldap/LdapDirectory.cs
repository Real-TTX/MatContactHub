namespace MatContactHub.Services.Ldap;

/// <summary>An LDAP account as the server knows it (from the snapshot): the bind name, the hash, what it reads.</summary>
public sealed record LdapAccountInfo(long Id, string Name, string PasswordHash, bool Enabled, bool AllAddressBooks, IReadOnlySet<long> AddressBookIds);

/// <summary>Who a connection is: anonymous (no bind, or a bind without name and password) or an LDAP account (its name in small letters).</summary>
public sealed record LdapIdentity(string? AccountName)
{
    public static readonly LdapIdentity Anonymous = new((string?)null);

    public bool IsAnonymous => AccountName is null;
}

/// <summary>
/// The directory the LDAP server answers from: an immutable snapshot of the settings, the LDAP-enabled address books, their contacts (without the hidden and the
/// deleted ones) and the LDAP accounts. <see cref="LdapDirectoryBuilder"/> makes it, <see cref="LdapServerHost"/> swaps it when
/// <c>Contacts.DirectoryChanges.Version</c> changes. Every search of every connection reads one snapshot from its start to its end.
/// <code>
/// ""                                   the root DSE
/// cn=Subschema                         the subschema (minimal)
/// dc=matcontacthub,dc=local            the base (LdapSettings.BaseDn)
///   ou=customers,dc=…                  an LDAP-enabled address book (its LdapName)
///     uid=&lt;Token&gt;,ou=customers,dc=…   a contact (inetOrgPerson)
/// </code>
/// </summary>
public sealed class LdapDirectory
{
    private readonly Dictionary<string, LdapEntry> _byKey;
    private readonly Dictionary<long, LdapEntry[]> _contacts;
    private readonly IReadOnlySet<long> _allBooks;

    internal LdapDirectory(long version, LdapSettings settings, LdapEntry rootDse, LdapEntry subschema, LdapEntry baseEntry, IReadOnlyList<LdapEntry> books,
        Dictionary<long, LdapEntry[]> contacts, IReadOnlyDictionary<string, LdapAccountInfo> accounts)
    {
        Version = version;
        BuiltUtc = DateTime.UtcNow;
        Settings = settings;
        Phone = new LdapPhone(settings.DefaultCountry);
        RootDse = rootDse;
        Subschema = subschema;
        Base = baseEntry;
        Books = books;
        _contacts = contacts;
        Accounts = accounts;
        _allBooks = books.Select(b => b.AddressBookId).ToHashSet();
        _byKey = new Dictionary<string, LdapEntry>(StringComparer.Ordinal) { [rootDse.Key] = rootDse, [subschema.Key] = subschema, [baseEntry.Key] = baseEntry };
        foreach (var book in books)
        {
            _byKey[book.Key] = book;
        }

        foreach (var entry in contacts.Values.SelectMany(c => c))
        {
            _byKey[entry.Key] = entry;
        }

        ContactCount = contacts.Values.Sum(c => c.Length);
    }

    /// <summary>The version of <c>DirectoryChanges</c> the snapshot was built from (0 = not built yet).</summary>
    public long Version { get; }

    public DateTime BuiltUtc { get; }

    public LdapSettings Settings { get; }

    /// <summary>The numbers of the filters, for the default country of the settings.</summary>
    public LdapPhone Phone { get; }

    public LdapEntry RootDse { get; }

    public LdapEntry Subschema { get; }

    public LdapEntry Base { get; }

    /// <summary>The LDAP-enabled address books, in their order.</summary>
    public IReadOnlyList<LdapEntry> Books { get; }

    /// <summary>The LDAP accounts by bind name (small letters), also the disabled ones.</summary>
    public IReadOnlyDictionary<string, LdapAccountInfo> Accounts { get; }

    public int ContactCount { get; }

    /// <summary>The entry with a key (<see cref="LdapDn.Normalize"/>), or null.</summary>
    public LdapEntry? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>The contacts of an address book, sorted by name.</summary>
    public IReadOnlyList<LdapEntry> ContactsOf(long addressBookId) => _contacts.TryGetValue(addressBookId, out var list) ? list : [];

    /// <summary>The account of an identity when it exists and is enabled, else null.</summary>
    public LdapAccountInfo? AccountOf(LdapIdentity who)
        => who.AccountName is { } name && Accounts.TryGetValue(name, out var account) && account.Enabled ? account : null;

    /// <summary>
    /// The address books an identity reads, or null when it may not search at all: an anonymous connection only when <see cref="LdapSettings.AllowAnonymous"/>
    /// (then every LDAP-enabled address book), an account that is gone or disabled never.
    /// </summary>
    public IReadOnlySet<long>? VisibleBooks(LdapIdentity who)
    {
        if (who.IsAnonymous)
        {
            return Settings.AllowAnonymous ? _allBooks : null;
        }

        if (AccountOf(who) is not { } account)
        {
            return null;
        }

        return account.AllAddressBooks ? _allBooks : account.AddressBookIds.Where(_allBooks.Contains).ToHashSet();
    }
}
