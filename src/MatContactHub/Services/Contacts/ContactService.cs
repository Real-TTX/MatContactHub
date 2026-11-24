using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

/// <summary>What the list of contacts shows and in which order (as it comes from the toolbar).</summary>
public sealed record ContactCriteria(string? Search, long? AddressBookId, string Sort, bool Descending, int Page, int PageSize);

/// <summary>A line of the list of contacts.</summary>
public sealed record ContactRow(long Id, string DisplayName, string? Company, string? Email, string? Phone, string AddressBook, bool Synced, bool Hidden);

public sealed record ContactList(IReadOnlyList<ContactRow> Items, int Total, int Matching);

/// <summary>
/// What the edit page of a contact shows and sends (FOUNDATION STUB: the fields of the tab General and a few of Communication; module C adds the rest of the
/// contact, the tabs and the source information).
/// </summary>
public sealed record ContactForm(
    long AddressBookId,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    string? Company,
    string? Email,
    string? PhoneBusiness,
    string? PhoneMobile,
    bool Hidden);

/// <summary>A contact as the edit page needs it: the form, whether it comes from a data source (then read-only but for Hidden) and the role of the person.</summary>
public sealed record ContactDetail(long Id, Guid Token, ContactForm Form, bool Synced, string? SourceName, string? ExternalKey, DateTime? LastSyncDate, AddressBookRole Role);

/// <summary>
/// The contacts (module C owns this class; FOUNDATION STUB: a simple list with search and paging, a simple edit of manual contacts and the flag Hidden of synced ones).
/// Every call checks the actor through <see cref="AddressBookService.RolesAsync"/>: what may not be seen answers like what is not there. Every change bumps
/// <see cref="DirectoryChanges"/> (through <c>AppDbContext.SaveChanges</c>).
/// </summary>
public static class ContactService
{
    /// <summary>The contacts of the address books the actor may see.</summary>
    public static async Task<ContactList> ListAsync(AppDbContext db, Actor actor, ContactCriteria criteria, CancellationToken ct = default)
    {
        var roles = await AddressBookService.RolesAsync(db, actor, ct);
        var ids = roles.Keys.ToList();
        var visible = db.Contacts.AsNoTracking().Where(c => ids.Contains(c.AddressBookId));
        var query = visible;
        if (criteria.AddressBookId is { } book)
        {
            query = query.Where(c => c.AddressBookId == book);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var pattern = "%" + criteria.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(c => EF.Functions.ILike(c.DisplayName, pattern) || EF.Functions.ILike(c.Company ?? string.Empty, pattern)
                || EF.Functions.ILike(c.Email ?? string.Empty, pattern) || EF.Functions.ILike(c.PhoneBusiness ?? string.Empty, pattern)
                || EF.Functions.ILike(c.PhoneMobile ?? string.Empty, pattern));
        }

        query = (criteria.Sort, criteria.Descending) switch
        {
            ("company", false) => query.OrderBy(c => c.Company).ThenBy(c => c.DisplayName),
            ("company", true) => query.OrderByDescending(c => c.Company).ThenBy(c => c.DisplayName),
            ("changed", false) => query.OrderBy(c => c.UpdateDate),
            ("changed", true) => query.OrderByDescending(c => c.UpdateDate),
            (_, true) => query.OrderByDescending(c => c.DisplayName),
            _ => query.OrderBy(c => c.DisplayName),
        };

        var total = await visible.CountAsync(ct);
        var matching = await query.CountAsync(ct);
        var items = await query.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize)
            .Select(c => new ContactRow(c.Id, c.DisplayName, c.Company, c.Email, c.PhoneBusiness ?? c.PhoneMobile, c.AddressBook!.Name, c.DataSourceId != null, c.Hidden))
            .ToListAsync(ct);
        return new ContactList(items, total, matching);
    }

    /// <summary>A contact the actor may see, or null.</summary>
    public static async Task<ContactDetail?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var c = await db.Contacts.AsNoTracking().Include(x => x.DataSource).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return null;
        }

        var role = await AddressBookService.RoleAsync(db, actor, c.AddressBookId, ct);
        if (role == AddressBookRole.None)
        {
            return null;
        }

        var form = new ContactForm(c.AddressBookId, c.FirstName, c.LastName, c.DisplayName, c.Company, c.Email, c.PhoneBusiness, c.PhoneMobile, c.Hidden);
        return new ContactDetail(c.Id, c.Token, form, c.DataSourceId is not null, c.DataSource?.Name, c.ExternalKey, c.LastSyncDate, role);
    }

    /// <summary>
    /// Creates (<paramref name="id"/> null) or changes a contact in an address book where the actor is an editor (a viewer changes nothing, not even
    /// <see cref="ContactForm.Hidden"/>: it takes a contact out of LDAP and the directory API for everybody). Of a synced contact only
    /// <see cref="ContactForm.Hidden"/> is taken (the source wins for everything else). Answers the id.
    /// </summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, long? id, ContactForm form, CancellationToken ct = default)
    {
        Contact? contact = null;
        if (id is { } existing)
        {
            contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == existing, ct);
            if (contact is null || await AddressBookService.RoleAsync(db, actor, contact.AddressBookId, ct) < AddressBookRole.Editor)
            {
                return Outcome<long>.Denied();
            }

            if (contact.DataSourceId is not null)
            {
                if (contact.Hidden != form.Hidden)
                {
                    contact.Hidden = form.Hidden;
                    await db.SaveChangesAsync(ct);
                    await ActivityLog.AddAsync(db, actor, "contact", contact.DisplayName, form.Hidden ? "hidden" : "shown", contact.Id, ct);
                }

                return Outcome<long>.Success(contact.Id);
            }
        }

        if (await AddressBookService.RoleAsync(db, actor, form.AddressBookId, ct) < AddressBookRole.Editor)
        {
            return Outcome<long>.Fail(Text.T("Contacts.AddressBook.NotAllowed"));
        }

        if (contact is null)
        {
            contact = new Contact();
            db.Contacts.Add(contact);
        }

        contact.AddressBookId = form.AddressBookId;
        contact.FirstName = Clean(form.FirstName);
        contact.LastName = Clean(form.LastName);
        contact.Company = Clean(form.Company);
        contact.Email = Clean(form.Email);
        contact.PhoneBusiness = Clean(form.PhoneBusiness);
        contact.PhoneMobile = Clean(form.PhoneMobile);
        contact.Hidden = form.Hidden;
        contact.DisplayName = DisplayNameOf(Clean(form.DisplayName), contact.FirstName, contact.LastName, contact.Company);
        if (contact.DisplayName.Length == 0)
        {
            return Outcome<long>.Fail(Text.T("Contacts.Name.Required"));
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", contact.DisplayName, id is null ? "created" : "changed", contact.Id, ct);
        return Outcome<long>.Success(contact.Id);
    }

    /// <summary>Deletes (softly) a manual contact in an address book where the actor is an editor; a synced contact is never deleted here.</summary>
    public static async Task<Outcome<bool>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contact is null || contact.DataSourceId is not null || await AddressBookService.RoleAsync(db, actor, contact.AddressBookId, ct) < AddressBookRole.Editor)
        {
            return Outcome<bool>.Denied();
        }

        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", contact.DisplayName, "deleted", contact.Id, ct);
        return Outcome<bool>.Success(true);
    }

    /// <summary>The name shown: the given one, else "First Last", else the company; empty when there is none of them.</summary>
    public static string DisplayNameOf(string? displayName, string? firstName, string? lastName, string? company)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        var name = string.Join(' ', new[] { firstName, lastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return name.Length > 0 ? name : company?.Trim() ?? string.Empty;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
