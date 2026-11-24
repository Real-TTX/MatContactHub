using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

/// <summary>A line of the list of address books.</summary>
public sealed record AddressBookRow(long Id, string Name, string? Description, string LdapName, bool LdapEnabled, bool AppsEnabled, bool IsShared, string? Color,
    int Contacts, AddressBookRole Role);

/// <summary>What the edit page of an address book shows and sends.</summary>
public sealed record AddressBookForm(string Name, string? Description, string LdapName, bool LdapEnabled, bool AppsEnabled, bool IsShared, string? Color, int SortOrder);

/// <summary>
/// The address books and THE check of what an actor may do in them (module C owns this class; FOUNDATION STUB: the list, the check, a simple save and delete;
/// the Access tab and the LDAP and apps tab follow). An administrator may do everything; a person has the role of <see cref="AddressBookAccess"/>, a shared address book
/// makes everybody a viewer; an app bound through <c>contacts.directory</c> (<see cref="Actor.Consumer"/>) reads the address books with <c>AppsEnabled</c>.
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
        foreach (var access in await db.AddressBookAccess.AsNoTracking().Where(a => a.UserId == userId).Select(a => new { a.AddressBookId, a.Role }).ToListAsync(ct))
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
            .Select(a => new { a.Id, a.Name, a.Description, a.LdapName, a.LdapEnabled, a.AppsEnabled, a.IsShared, a.Color, Contacts = db.Contacts.Count(c => c.AddressBookId == a.Id) })
            .ToListAsync(ct);
        return [.. rows.Select(a => new AddressBookRow(a.Id, a.Name, a.Description, a.LdapName, a.LdapEnabled, a.AppsEnabled, a.IsShared, a.Color, a.Contacts, roles[a.Id]))];
    }

    /// <summary>The form of an address book (administrators only; null for anybody else or when it does not exist).</summary>
    public static async Task<AddressBookForm?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        return await db.AddressBooks.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new AddressBookForm(a.Name, a.Description, a.LdapName, a.LdapEnabled, a.AppsEnabled, a.IsShared, a.Color, a.SortOrder))
            .FirstOrDefaultAsync(ct);
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

        var ldapName = string.IsNullOrWhiteSpace(form.LdapName) ? LdapNameOf(name) : form.LdapName.Trim();
        if (!IsLdapName(ldapName))
        {
            return Outcome<long>.Fail(Text.T("AddressBooks.LdapName.Invalid"));
        }

        if (await db.AddressBooks.AnyAsync(a => a.LdapName == ldapName && a.Id != (id ?? 0), ct))
        {
            return Outcome<long>.Fail(Text.T("AddressBooks.LdapName.Taken"));
        }

        AddressBook? book;
        if (id is { } existing)
        {
            book = await db.AddressBooks.FirstOrDefaultAsync(a => a.Id == existing, ct);
            if (book is null)
            {
                return Outcome<long>.Denied();
            }
        }
        else
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
        book.Color = Ui.IsColour(form.Color) ? form.Color : null;
        book.SortOrder = form.SortOrder;
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "addressbook", book.Name, id is null ? "created" : "changed", book.Id, ct);
        return Outcome<long>.Success(book.Id);
    }

    /// <summary>Deletes an address book (softly) with its contacts; administrators only. Refused while a data source writes into it.</summary>
    public static async Task<Outcome<bool>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.AddressBooks.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } book)
        {
            return Outcome<bool>.Denied();
        }

        if (await db.DataSources.AnyAsync(s => s.AddressBookId == id, ct))
        {
            return Outcome<bool>.Fail(Text.T("AddressBooks.Delete.HasSources"));
        }

        db.AddressBooks.Remove(book);
        foreach (var contact in await db.Contacts.Where(c => c.AddressBookId == id).ToListAsync(ct))
        {
            db.Contacts.Remove(contact);
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "addressbook", book.Name, "deleted", book.Id, ct);
        return Outcome<bool>.Success(true);
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

    /// <summary>A valid LDAP name: 1 to 64 letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit.</summary>
    public static bool IsLdapName(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 64 && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
