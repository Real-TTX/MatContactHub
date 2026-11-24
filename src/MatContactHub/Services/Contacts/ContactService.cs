using System.Net.Mail;
using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

/// <summary>Where the contacts of the list come from: every one, the manual ones, the synced ones, or the ones of one data source.</summary>
public static class ContactOrigin
{
    public const string Manual = "manual";
    public const string Synced = "synced";

    /// <summary>The value of the filter for one data source (<c>s12</c>).</summary>
    public static string Source(long id) => "s" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// What the list of contacts shows and in which order (as it comes from the toolbar). <see cref="Origin"/> is null (all), <see cref="ContactOrigin.Manual"/>,
/// <see cref="ContactOrigin.Synced"/> or <see cref="ContactOrigin.Source"/>; <see cref="Hidden"/> null (all), true (only hidden ones) or false (only shown ones).
/// <see cref="Sort"/>: <c>name</c>, <c>company</c>, <c>city</c> or <c>changed</c>.
/// </summary>
public sealed record ContactCriteria(string? Search, long? AddressBookId, string Sort, bool Descending, int Page, int PageSize, string? Origin = null, bool? Hidden = null);

/// <summary>A line of the list of contacts.</summary>
public sealed record ContactRow(
    long Id,
    string DisplayName,
    string? Company,
    string? Department,
    string? JobTitle,
    string? Email,
    string? Phone,
    string? Mobile,
    string? City,
    long AddressBookId,
    string AddressBook,
    string? AddressBookColor,
    bool Synced,
    string? SourceName,
    bool Hidden,
    DateTime Changed,
    AddressBookRole Role);

public sealed record ContactList(IReadOnlyList<ContactRow> Items, int Total, int Matching);

/// <summary>A data source whose contacts the actor sees (the filter "origin" of the list).</summary>
public sealed record ContactSourceOption(long Id, string Name);

/// <summary>
/// A contact as the edit page needs it: the form, where it comes from (a synced one is read-only but for Hidden), its address book and the role of the person in it.
/// </summary>
public sealed record ContactDetail(
    long Id,
    Guid Token,
    ContactForm Form,
    bool Synced,
    long? SourceId,
    string? SourceName,
    string? ExternalKey,
    DateTime? LastSyncDate,
    AddressBookRole Role,
    string AddressBook,
    string? AddressBookColor,
    DateTime Created,
    DateTime Changed,
    string Name);

/// <summary>A wrong value of a form: the field (the property of <see cref="ContactForm"/>, empty for the whole form) and the message.</summary>
public sealed record ContactFieldError(string Field, string Message);

/// <summary>
/// The contacts (module C). Every call checks the actor through <see cref="AddressBookService.RolesAsync"/>: what may not be seen answers like what is not there
/// (null, <see cref="Outcome{T}.Denied"/>). A viewer reads; an editor of the address book creates, changes and deletes its manual contacts and sets
/// <see cref="Contact.Hidden"/> of every contact in it (a synced contact is otherwise read-only: the source wins). Every change bumps <see cref="DirectoryChanges"/>
/// (through <c>AppDbContext.SaveChanges</c>).
/// </summary>
public static class ContactService
{
    /// <summary>The most contacts one action of the list (hide, show, delete, export of a selection) takes.</summary>
    public const int MaxSelection = 500;

    /// <summary>The most contacts the export of a whole list writes.</summary>
    public const int MaxExport = 20000;

    /// <summary>The contacts of the address books the actor may see, filtered, sorted and one page of them.</summary>
    public static async Task<ContactList> ListAsync(AppDbContext db, Actor actor, ContactCriteria criteria, CancellationToken ct = default)
    {
        var roles = await AddressBookService.RolesAsync(db, actor, ct);
        var ids = roles.Keys.ToList();
        var visible = db.Contacts.AsNoTracking().Where(c => ids.Contains(c.AddressBookId));
        var query = Sorted(Filtered(visible, criteria, await DefaultCountryAsync(db, ct)), criteria.Sort, criteria.Descending);

        var total = await visible.CountAsync(ct);
        var matching = await query.CountAsync(ct);
        var pageSize = Math.Clamp(criteria.PageSize, 1, 500);
        var items = await query.Skip(Math.Max(0, criteria.Page - 1) * pageSize).Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.DisplayName,
                c.Company,
                c.Department,
                c.JobTitle,
                Email = c.Email ?? c.Email2 ?? c.Email3,
                Phone = c.PhoneBusiness ?? c.PhoneBusiness2 ?? c.PhoneOther ?? c.PhoneHome,
                c.PhoneMobile,
                c.City,
                c.AddressBookId,
                Book = c.AddressBook!.Name,
                c.AddressBook.Color,
                Synced = c.DataSourceId != null,
                Source = c.DataSource!.Name,
                c.Hidden,
                c.UpdateDate,
            })
            .ToListAsync(ct);
        return new ContactList(
            [.. items.Select(c => new ContactRow(c.Id, c.DisplayName, c.Company, c.Department, c.JobTitle, c.Email, c.Phone, c.PhoneMobile, c.City, c.AddressBookId, c.Book,
                c.Color, c.Synced, c.Source, c.Hidden, c.UpdateDate, roles[c.AddressBookId]))],
            total, matching);
    }

    /// <summary>The data sources whose contacts the actor sees (for the filter "origin").</summary>
    public static async Task<IReadOnlyList<ContactSourceOption>> SourcesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        var ids = (await AddressBookService.RolesAsync(db, actor, ct)).Keys.ToList();
        return await db.DataSources.AsNoTracking().Where(s => ids.Contains(s.AddressBookId)).OrderBy(s => s.Name)
            .Select(s => new ContactSourceOption(s.Id, s.Name)).ToListAsync(ct);
    }

    /// <summary>A contact the actor may see, or null.</summary>
    public static async Task<ContactDetail?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var c = await db.Contacts.AsNoTracking().Include(x => x.DataSource).Include(x => x.AddressBook).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return null;
        }

        var role = await AddressBookService.RoleAsync(db, actor, c.AddressBookId, ct);
        if (role == AddressBookRole.None)
        {
            return null;
        }

        // a display name that is only "First Last" (or the company) stays empty in the form, so it follows a changed name; a synced one is shown as it is
        var form = ContactForm.Of(c);
        if (c.DataSourceId is null && c.DisplayName == DisplayNameOf(null, c.FirstName, c.LastName, c.Company))
        {
            form.DisplayName = null;
        }

        return new ContactDetail(c.Id, c.Token, form, c.DataSourceId is not null, c.DataSourceId, c.DataSource?.Name, c.ExternalKey, c.LastSyncDate, role,
            c.AddressBook?.Name ?? string.Empty, c.AddressBook?.Color, c.CreateDate, c.UpdateDate, c.DisplayName);
    }

    /// <summary>
    /// The address book a new contact goes into: <paramref name="wanted"/> when the actor is an editor there, else the one of the last manual contact the actor created
    /// (while it is still an editor there), else the first one where it is an editor. Null when it is an editor nowhere.
    /// </summary>
    public static async Task<long?> DefaultAddressBookAsync(AppDbContext db, Actor actor, long? wanted, CancellationToken ct = default)
    {
        var writable = (await AddressBookService.ListAsync(db, actor, ct)).Where(b => b.Role >= AddressBookRole.Editor).Select(b => b.Id).ToList();
        if (writable.Count == 0)
        {
            return null;
        }

        if (wanted is { } w && writable.Contains(w))
        {
            return w;
        }

        if (actor.UserId is { } userId)
        {
            var last = await db.Contacts.AsNoTracking().Where(c => c.CreateUserId == userId && c.DataSourceId == null && writable.Contains(c.AddressBookId))
                .OrderByDescending(c => c.CreateDate).ThenByDescending(c => c.Id).Select(c => (long?)c.AddressBookId).FirstOrDefaultAsync(ct);
            if (last is not null)
            {
                return last;
            }
        }

        return writable[0];
    }

    /// <summary>The wrong values of a form (no database needed): the lengths, the e-mail addresses, a name or a company, a birthday that is not in the future.</summary>
    public static IReadOnlyList<ContactFieldError> Validate(ContactForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var errors = new List<ContactFieldError>();
        var probe = new Contact();
        form.CopyTo(probe);
        probe.DisplayName = ContactFields.Clean(form.DisplayName) ?? string.Empty;
        foreach (var field in ContactFields.All)
        {
            var value = field.Get(probe);
            if (value is null)
            {
                continue;
            }

            if (value.Length > field.MaxLength)
            {
                errors.Add(new ContactFieldError(field.Name, Text.T("Contacts.Field.TooLong", field.MaxLength)));
            }
            else if (field.Kind == ContactFieldKind.Email && !IsEmail(value))
            {
                errors.Add(new ContactFieldError(field.Name, Text.T("Contacts.Email.Invalid")));
            }
        }

        if (DisplayNameOf(probe.DisplayName, probe.FirstName, probe.LastName, probe.Company).Length == 0)
        {
            errors.Add(new ContactFieldError("LastName", Text.T("Contacts.Name.Required")));
        }

        if (form.Birthday is { } birthday && (birthday > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) || birthday.Year < 1850))
        {
            errors.Add(new ContactFieldError("Birthday", Text.T("Contacts.Birthday.Invalid")));
        }

        return errors;
    }

    /// <summary>
    /// Creates (<paramref name="id"/> null) or changes a contact in an address book where the actor is an editor (a viewer changes nothing, not even
    /// <see cref="ContactForm.Hidden"/>: it takes a contact out of LDAP and the directory API for everybody). Of a synced contact only
    /// <see cref="ContactForm.Hidden"/> is taken (the source wins for everything else). Answers the id.
    /// </summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, long? id, ContactForm form, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
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

        if (Validate(form) is { Count: > 0 } errors)
        {
            return Outcome<long>.Fail(errors[0].Message);
        }

        if (contact is null)
        {
            contact = new Contact { CreateUserId = actor.UserId };
            db.Contacts.Add(contact);
        }

        contact.AddressBookId = form.AddressBookId;
        form.CopyTo(contact);
        contact.Hidden = form.Hidden;
        contact.DisplayName = DisplayNameOf(form.DisplayName, contact.FirstName, contact.LastName, contact.Company);
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

    /// <summary>
    /// Hides or shows the selected contacts: those in address books where the actor is an editor (manual and synced ones); the others are left as they are.
    /// Answers how many changed.
    /// </summary>
    public static async Task<int> SetHiddenAsync(AppDbContext db, Actor actor, IReadOnlyCollection<long> ids, bool hidden, CancellationToken ct = default)
    {
        var editable = await EditableBooksAsync(db, actor, ct);
        var wanted = ids.Distinct().Take(MaxSelection).ToList();
        var contacts = await db.Contacts.Where(c => wanted.Contains(c.Id) && editable.Contains(c.AddressBookId) && c.Hidden != hidden).ToListAsync(ct);
        if (contacts.Count == 0)
        {
            return 0;
        }

        contacts.ForEach(c => c.Hidden = hidden);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", Text.T("Contacts.Count", contacts.Count), hidden ? "hidden" : "shown", null, ct);
        return contacts.Count;
    }

    /// <summary>Deletes (softly) the selected manual contacts of address books where the actor is an editor; synced ones and the others are left. Answers how many.</summary>
    public static async Task<int> DeleteManyAsync(AppDbContext db, Actor actor, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var editable = await EditableBooksAsync(db, actor, ct);
        var wanted = ids.Distinct().Take(MaxSelection).ToList();
        var contacts = await db.Contacts.Where(c => wanted.Contains(c.Id) && editable.Contains(c.AddressBookId) && c.DataSourceId == null).ToListAsync(ct);
        if (contacts.Count == 0)
        {
            return 0;
        }

        db.Contacts.RemoveRange(contacts);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", Text.T("Contacts.Count", contacts.Count), "deleted", null, ct);
        return contacts.Count;
    }

    /// <summary>The selected contacts the actor may see, as vCards (<paramref name="version"/> 3 or 4), in the order of their names.</summary>
    public static async Task<VCardExport> ExportAsync(AppDbContext db, Actor actor, IReadOnlyCollection<long> ids, int version, CancellationToken ct = default)
    {
        var visible = (await AddressBookService.RolesAsync(db, actor, ct)).Keys.ToList();
        var wanted = ids.Distinct().Take(MaxExport).ToList();
        var contacts = await db.Contacts.AsNoTracking().Include(c => c.AddressBook)
            .Where(c => wanted.Contains(c.Id) && visible.Contains(c.AddressBookId)).OrderBy(c => c.DisplayName).ToListAsync(ct);
        return Export(contacts, version);
    }

    /// <summary>Every contact of the list as the toolbar filters it (search, filters, sort; not the page), as vCards; at most <see cref="MaxExport"/>.</summary>
    public static async Task<VCardExport> ExportAsync(AppDbContext db, Actor actor, ContactCriteria criteria, int version, CancellationToken ct = default)
    {
        var visible = (await AddressBookService.RolesAsync(db, actor, ct)).Keys.ToList();
        var query = db.Contacts.AsNoTracking().Include(c => c.AddressBook).Where(c => visible.Contains(c.AddressBookId));
        var contacts = await Sorted(Filtered(query, criteria, await DefaultCountryAsync(db, ct)), criteria.Sort, criteria.Descending).Take(MaxExport).ToListAsync(ct);
        return Export(contacts, version);
    }

    /// <summary>The contacts the search in the header bar suggests: the best matches by name, with their company and first number.</summary>
    public static async Task<IReadOnlyList<ContactRow>> SuggestAsync(AppDbContext db, Actor actor, string? search, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        var list = await ListAsync(db, actor, new ContactCriteria(search, null, "name", false, 1, Math.Clamp(limit, 1, 20)), ct);
        return list.Items;
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

    /// <summary>A plausible e-mail address (one @, a dot in the domain, no spaces).</summary>
    public static bool IsEmail(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace) && MailAddress.TryCreate(value, out var address) && address.Address == value
           && address.Host.Contains('.', StringComparison.Ordinal);

    /// <summary>The default country of the telephone numbers (the settings; DE without them).</summary>
    public static async Task<string> DefaultCountryAsync(AppDbContext db, CancellationToken ct = default)
        => await db.SystemSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.DefaultCountry).FirstOrDefaultAsync(ct) ?? PhoneNormalizer.FallbackCountry;

    /// <summary>The address books where the actor is an editor.</summary>
    internal static async Task<HashSet<long>> EditableBooksAsync(AppDbContext db, Actor actor, CancellationToken ct)
        => [.. (await AddressBookService.RolesAsync(db, actor, ct)).Where(r => r.Value >= AddressBookRole.Editor).Select(r => r.Key)];

    private static VCardExport Export(IReadOnlyList<Contact> contacts, int version)
        => new(VCard.Write(contacts, version == 4 ? VCardVersion.V4 : VCardVersion.V3), contacts.Count, contacts.Count == 1 ? contacts[0].DisplayName : null);

    private static IQueryable<Contact> Filtered(IQueryable<Contact> query, ContactCriteria criteria, string defaultCountry)
    {
        if (criteria.AddressBookId is { } book)
        {
            query = query.Where(c => c.AddressBookId == book);
        }

        switch (criteria.Origin)
        {
            case ContactOrigin.Manual:
                query = query.Where(c => c.DataSourceId == null);
                break;
            case ContactOrigin.Synced:
                query = query.Where(c => c.DataSourceId != null);
                break;
            case { Length: > 1 } origin when origin[0] == 's' && long.TryParse(origin.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var source):
                query = query.Where(c => c.DataSourceId == source);
                break;
        }

        if (criteria.Hidden is { } hidden)
        {
            query = query.Where(c => c.Hidden == hidden);
        }

        return ContactSearch.Apply(query, criteria.Search, defaultCountry);
    }

    private static IQueryable<Contact> Sorted(IQueryable<Contact> query, string sort, bool descending) => (sort, descending) switch
    {
        ("company", false) => query.OrderBy(c => c.Company == null).ThenBy(c => c.Company).ThenBy(c => c.DisplayName).ThenBy(c => c.Id),
        ("company", true) => query.OrderBy(c => c.Company == null).ThenByDescending(c => c.Company).ThenBy(c => c.DisplayName).ThenBy(c => c.Id),
        ("city", false) => query.OrderBy(c => c.City == null).ThenBy(c => c.City).ThenBy(c => c.DisplayName).ThenBy(c => c.Id),
        ("city", true) => query.OrderBy(c => c.City == null).ThenByDescending(c => c.City).ThenBy(c => c.DisplayName).ThenBy(c => c.Id),
        ("changed", false) => query.OrderBy(c => c.UpdateDate).ThenBy(c => c.Id),
        ("changed", true) => query.OrderByDescending(c => c.UpdateDate).ThenByDescending(c => c.Id),
        (_, true) => query.OrderByDescending(c => c.DisplayName).ThenBy(c => c.Id),
        _ => query.OrderBy(c => c.DisplayName).ThenBy(c => c.Id),
    };
}

/// <summary>Contacts written as vCards: the text, how many, and the name of the one contact (for the file name of a single export).</summary>
public sealed record VCardExport(string Text, int Count, string? SingleName);
