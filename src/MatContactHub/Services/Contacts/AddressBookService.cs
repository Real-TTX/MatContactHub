using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

/// <summary>A line of the list of address books (<see cref="Sources"/>: the data sources that write into it; <see cref="People"/>: the people with a role in it).</summary>
public sealed record AddressBookRow(long Id, string Name, string? Description, string LdapName, bool LdapEnabled, bool AppsEnabled, bool IsShared, string? Color,
    int Contacts, AddressBookRole Role, int Sources = 0, int People = 0);

/// <summary>The role of one person in an address book (what the tab Access sends).</summary>
public sealed record AddressBookAccessEntry(long UserId, AddressBookRole Role);

/// <summary>
/// What the edit page of an address book shows and sends. <see cref="Access"/> null leaves the roles as they are (a caller without the tab Access); a list replaces
/// them (a person not in it loses the role). <see cref="LdapName"/> null keeps the LDAP name of an address book (the field is hidden while LDAP is off), empty makes
/// one from the name.
/// </summary>
public sealed record AddressBookForm(string Name, string? Description, string? LdapName, bool LdapEnabled, bool AppsEnabled, bool IsShared, string? Color, int SortOrder,
    IReadOnlyList<AddressBookAccessEntry>? Access = null);

/// <summary>A person with a role in an address book, for the tab Access.</summary>
public sealed record AddressBookPerson(long UserId, string DisplayName, string Email, AddressBookRole Role, bool IsAdmin);

/// <summary>What the edit page shows beside the form: the contacts (manual and synced), the data sources that write into it, the people with a role.</summary>
public sealed record AddressBookFacts(int ManualContacts, int SyncedContacts, IReadOnlyList<string> Sources, IReadOnlyList<AddressBookPerson> People);

/// <summary>
/// The address books and THE check of what an actor may do in them (module C owns this class). An administrator may do everything; a person has the role of
/// <see cref="AddressBookAccess"/>, a shared address book makes everybody a viewer; an app bound through <c>contacts.directory</c> (<see cref="Actor.Consumer"/>) reads
/// the address books with <c>AppsEnabled</c>. Only administrators create, change and delete address books and give roles.
/// What may not be seen answers like what is not there (null / <see cref="Outcome{T}.Denied"/>).
/// </summary>
public static class AddressBookService
{
    /// <summary>The roles of the actor: every address book it may see, with its role.</summary>
    public static async Task<IReadOnlyDictionary<long, AddressBookRole>> RolesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (actor.IsAdmin)
        {
            return await db.AddressBooks.AsNoTracking().Select(a => a.Id).ToDictionaryAsync(id => id, _ => AddressBookRole.Editor, ct);
        }

        if (actor.Consumer is not null)
        {
            return await db.AddressBooks.AsNoTracking().Where(a => a.AppsEnabled).Select(a => a.Id).ToDictionaryAsync(id => id, _ => AddressBookRole.Viewer, ct);
        }

        if (actor.UserId is not { } userId)
        {
            return new Dictionary<long, AddressBookRole>();
        }

        var roles = await db.AddressBooks.AsNoTracking().Where(a => a.IsShared).Select(a => a.Id).ToDictionaryAsync(id => id, _ => AddressBookRole.Viewer, ct);
        // the roles in address books that exist (a deleted one keeps its rows, softly)
        foreach (var access in await db.AddressBookAccess.AsNoTracking().Where(a => a.UserId == userId && db.AddressBooks.Any(b => b.Id == a.AddressBookId))
                     .Select(a => new { a.AddressBookId, a.Role }).ToListAsync(ct))
        {
            if (!roles.TryGetValue(access.AddressBookId, out var role) || access.Role > role)
            {
                roles[access.AddressBookId] = access.Role;
            }
        }

        return roles;
    }

    /// <summary>The role of the actor in one address book (<see cref="AddressBookRole.None"/> when it may not see it or it does not exist).</summary>
    public static async Task<AddressBookRole> RoleAsync(AppDbContext db, Actor actor, long addressBookId, CancellationToken ct = default)
        => (await RolesAsync(db, actor, ct)).TryGetValue(addressBookId, out var role) ? role : AddressBookRole.None;

    /// <summary>The address books the actor may see, in their order.</summary>
    public static async Task<IReadOnlyList<AddressBookRow>> ListAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        var roles = await RolesAsync(db, actor, ct);
        var ids = roles.Keys.ToList();
        var rows = await db.AddressBooks.AsNoTracking().Where(a => ids.Contains(a.Id)).OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Description,
                a.LdapName,
                a.LdapEnabled,
                a.AppsEnabled,
                a.IsShared,
                a.Color,
                Contacts = db.Contacts.Count(c => c.AddressBookId == a.Id),
                Sources = db.DataSources.Count(s => s.AddressBookId == a.Id),
                People = db.AddressBookAccess.Count(x => x.AddressBookId == a.Id),
            })
            .ToListAsync(ct);
        return [.. rows.Select(a => new AddressBookRow(a.Id, a.Name, a.Description, a.LdapName, a.LdapEnabled, a.AppsEnabled, a.IsShared, a.Color, a.Contacts, roles[a.Id],
            a.Sources, a.People))];
    }

    /// <summary>The form of an address book with its roles (administrators only; null for anybody else or when it does not exist).</summary>
    public static async Task<AddressBookForm?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var form = await db.AddressBooks.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new AddressBookForm(a.Name, a.Description, a.LdapName, a.LdapEnabled, a.AppsEnabled, a.IsShared, a.Color, a.SortOrder, null))
            .FirstOrDefaultAsync(ct);
        if (form is null)
        {
            return null;
        }

        var access = await db.AddressBookAccess.AsNoTracking().Where(x => x.AddressBookId == id).OrderBy(x => x.UserId)
            .Select(x => new AddressBookAccessEntry(x.UserId, x.Role)).ToListAsync(ct);
        return form with { Access = access };
    }

    /// <summary>What the edit page shows beside the form (administrators only; null for anybody else or when it does not exist).</summary>
    public static async Task<AddressBookFacts?> FactsAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || !await db.AddressBooks.AnyAsync(a => a.Id == id, ct))
        {
            return null;
        }

        var manual = await db.Contacts.CountAsync(c => c.AddressBookId == id && c.DataSourceId == null, ct);
        var synced = await db.Contacts.CountAsync(c => c.AddressBookId == id && c.DataSourceId != null, ct);
        var sources = await db.DataSources.AsNoTracking().Where(s => s.AddressBookId == id).OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);
        var people = await db.AddressBookAccess.AsNoTracking().Where(x => x.AddressBookId == id && x.User != null)
            .Select(x => new { x.UserId, x.User!.DisplayName, x.User.Email, x.Role, IsAdmin = x.User.Role == UserRole.Admin })
            .OrderBy(p => p.DisplayName).ToListAsync(ct);
        return new AddressBookFacts(manual, synced, sources, [.. people.Select(p => new AddressBookPerson(p.UserId, p.DisplayName, p.Email, p.Role, p.IsAdmin))]);
    }

    /// <summary>The people who can be given a role (administrators only): every active account, by name.</summary>
    public static async Task<IReadOnlyList<AddressBookPerson>> PeopleAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return [];
        }

        return await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new AddressBookPerson(u.Id, u.DisplayName, u.Email, AddressBookRole.None, u.Role == UserRole.Admin)).ToListAsync(ct);
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes an address book; administrators only. Answers its id.</summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, long? id, AddressBookForm form, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<long>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length is 0 or > 120)
        {
            return Outcome<long>.Fail(Text.T("AddressBooks.Name.Invalid"));
        }

        AddressBook? book = null;
        if (id is { } existing)
        {
            book = await db.AddressBooks.FirstOrDefaultAsync(a => a.Id == existing, ct);
            if (book is null)
            {
                return Outcome<long>.Denied();
            }
        }

        // LDAP compares ou values without case (caseIgnoreMatch): the name is kept in small letters, so the unique index sees Kunden and kunden as one.
        // null (a caller without the field, e.g. the form while LDAP is off and the field is hidden) keeps the name an address book has; empty makes one
        // from the name, with -2, -3 … when another address book has it already. Only a typed name is refused as taken.
        string ldapName;
        if (form.LdapName is null && book is not null)
        {
            ldapName = book.LdapName;
        }
        else if (string.IsNullOrWhiteSpace(form.LdapName))
        {
            ldapName = await FreeLdapNameAsync(db, LdapNameOf(name), id ?? 0, ct);
        }
        else
        {
            ldapName = form.LdapName.Trim().ToLowerInvariant();
            if (!IsLdapName(ldapName))
            {
                return Outcome<long>.Fail(Text.T("AddressBooks.LdapName.Invalid"));
            }

            if (await db.AddressBooks.AnyAsync(a => a.LdapName == ldapName && a.Id != (id ?? 0), ct))
            {
                return Outcome<long>.Fail(Text.T("AddressBooks.LdapName.Taken"));
            }
        }

        if (book is null)
        {
            book = new AddressBook();
            db.AddressBooks.Add(book);
        }

        book.Name = name;
        book.Description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim();
        book.LdapName = ldapName;
        book.LdapEnabled = form.LdapEnabled;
        book.AppsEnabled = form.AppsEnabled;
        book.IsShared = form.IsShared;
        // null keeps the colour an address book has (a caller without the field); empty or not a colour clears it
        if (form.Color is not null || id is null)
        {
            book.Color = Ui.IsColour(form.Color) ? form.Color!.ToLowerInvariant() : null;
        }

        book.SortOrder = form.SortOrder;
        if (form.Access is { } access)
        {
            if (await ApplyAccessAsync(db, book, access, ct) is { } error)
            {
                return Outcome<long>.Fail(error);
            }
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "addressbook", book.Name, id is null ? ActivityLog.Text("Activity.Detail.Created") : ActivityLog.Text("Activity.Detail.Changed"), book.Id, ct);
        return Outcome<long>.Success(book.Id);
    }

    /// <summary>
    /// Deletes an address book (softly) with its contacts; administrators only. Refused while a data source writes into it (its synced contacts would come back
    /// with nowhere to go): delete the data source or point it elsewhere first. Answers how many contacts went with it.
    /// </summary>
    public static async Task<Outcome<int>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.AddressBooks.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } book)
        {
            return Outcome<int>.Denied();
        }

        if (await db.DataSources.AnyAsync(s => s.AddressBookId == id, ct))
        {
            return Outcome<int>.Fail(Text.T("AddressBooks.Delete.HasSources"));
        }

        // the contacts in one statement (an address book may hold thousands), the address book after them, both or neither
        int contacts;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            contacts = await db.Contacts.Where(c => c.AddressBookId == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UpdateState, SoftEntity.Deleted)
                .SetProperty(c => c.UpdateDate, DateTime.UtcNow)
                .SetProperty(c => c.UpdateUserId, actor.UserId), ct);
            book.UpdateState = SoftEntity.Deleted;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        DirectoryChanges.Bump();
        await ActivityLog.AddAsync(db, actor, "addressbook", book.Name, ActivityLog.Text("AddressBooks.Deleted.Log", contacts), book.Id, ct);
        return Outcome<int>.Success(contacts);
    }

    /// <summary>Makes the roles of <paramref name="book"/> what <paramref name="access"/> says (unsaved); answers the message of a wrong entry, else null.</summary>
    private static async Task<string?> ApplyAccessAsync(AppDbContext db, AddressBook book, IReadOnlyList<AddressBookAccessEntry> access, CancellationToken ct)
    {
        var wanted = new Dictionary<long, AddressBookRole>();
        foreach (var entry in access)
        {
            if (entry.Role is not (AddressBookRole.Viewer or AddressBookRole.Editor))
            {
                continue;
            }

            wanted[entry.UserId] = entry.Role;
        }

        var ids = wanted.Keys.ToList();
        if (await db.Users.CountAsync(u => ids.Contains(u.Id), ct) != ids.Count)
        {
            return Text.T("AddressBooks.Access.Unknown");
        }

        var existing = book.Id == 0 ? [] : await db.AddressBookAccess.Where(x => x.AddressBookId == book.Id).ToListAsync(ct);
        foreach (var row in existing)
        {
            if (wanted.TryGetValue(row.UserId, out var role))
            {
                row.Role = role;
                wanted.Remove(row.UserId);
            }
            else
            {
                db.AddressBookAccess.Remove(row);
            }
        }

        foreach (var (userId, role) in wanted)
        {
            db.AddressBookAccess.Add(new AddressBookAccess { AddressBook = book, UserId = userId, Role = role });
        }

        return null;
    }

    /// <summary>An LDAP name made from a name: small letters, digits and <c>-</c> (<c>Kunden &amp; Partner</c> → <c>kunden-partner</c>).</summary>
    public static string LdapNameOf(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal)
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var text = new string(chars);
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        text = text.Trim('-');
        return text.Length == 0 ? "contacts" : text.Length > 64 ? text[..64].TrimEnd('-') : text;
    }

    /// <summary>A made LDAP name no other address book has: the name itself, otherwise the name with -2, -3 … (within 64 characters).</summary>
    private static async Task<string> FreeLdapNameAsync(AppDbContext db, string made, long ownId, CancellationToken ct)
    {
        var stem = made.Length > 58 ? made[..58].TrimEnd('-') : made;
        var prefix = stem + "-";
        var taken = (await db.AddressBooks.Where(a => a.Id != ownId && (a.LdapName == made || a.LdapName.StartsWith(prefix)))
            .Select(a => a.LdapName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(made))
        {
            return made;
        }

        for (var n = 2; ; n++)
        {
            var candidate = prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A valid LDAP name: 1 to 64 letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit.</summary>
    public static bool IsLdapName(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 64 && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
