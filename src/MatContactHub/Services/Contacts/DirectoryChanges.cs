namespace MatContactHub.Services.Contacts;

/// <summary>
/// THE seam between the modules that change the directory (contacts, address books, the sync, the LDAP accounts and settings) and those that keep a copy of it
/// (the in-memory snapshot of the LDAP server, caches of the directory API): a version counter that goes up with every change.
/// <list type="bullet">
/// <item><c>AppDbContext.SaveChanges</c> bumps it by itself after a successful save that added, changed or deleted a <c>Contact</c>, <c>AddressBook</c>,
/// <c>AddressBookAccess</c>, <c>LdapAccount</c>, <c>LdapAccountAddressBook</c> or <c>SystemSetting</c>.</item>
/// <item>Code that changes these tables past the change tracker (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL, a bulk insert) MUST call <see cref="Bump"/> after
/// the change is committed.</item>
/// <item>A reader remembers the <see cref="Version"/> it built its copy from and rebuilds when <see cref="Version"/> differs (or waits for <see cref="Changed"/>).</item>
/// </list>
/// Static and process-wide (one app instance per database); it starts at 1 at every start, so a reader with version 0 always builds first.
/// </summary>
public static class DirectoryChanges
{
    private static long _version = 1;

    /// <summary>The current version of the directory.</summary>
    public static long Version => Interlocked.Read(ref _version);

    /// <summary>Raised after every <see cref="Bump"/> with the new version (on the thread that bumped: keep the handler short, never block).</summary>
    public static event Action<long>? Changed;

    /// <summary>Marks the directory as changed; answers the new version.</summary>
    public static long Bump()
    {
        var version = Interlocked.Increment(ref _version);
        Changed?.Invoke(version);
        return version;
    }
}
