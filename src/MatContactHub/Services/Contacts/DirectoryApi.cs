using MatContactHub.Data;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

/// <summary>A number of a contact in the directory API: its type (<c>business</c>, <c>business2</c>, <c>mobile</c>, <c>home</c>, <c>other</c>, <c>fax</c>) and the number.</summary>
public sealed record DirectoryPhone(string Type, string Number, string? E164);

/// <summary>An entry of the directory API (caller-ID, search). <see cref="Id"/> is the stable token of the contact (the LDAP entryUUID, the vCard UID).</summary>
public sealed record DirectoryEntry(
    string Id,
    string DisplayName,
    string? FirstName,
    string? LastName,
    string? Company,
    string? Department,
    string? JobTitle,
    string? Email,
    IReadOnlyList<DirectoryPhone> Phones,
    string AddressBook);

/// <summary>The answer of a lookup: the contact, the type of the number that matched and the number as stored.</summary>
public sealed record DirectoryLookup(
    string Id,
    string DisplayName,
    string? Company,
    string? Department,
    string? Email,
    string NumberType,
    string Number,
    string AddressBook);

/// <summary>
/// The capability <c>contacts.directory</c> (a key capability: MatPlatform keeps a key per consuming app, <c>MAT_CONTACTS_DIRECTORY_URL</c>, <c>_INTERNAL_URL</c>,
/// <c>_KEY</c>): the apps read the address books with <c>AppsEnabled</c>, with the key as a bearer token, never a hidden contact. The endpoints answer under
/// <c>/api/provides/contacts.directory/</c> (the platform contract) and under <c>/api/directory/</c> (short form):
/// <list type="bullet">
/// <item><c>GET lookup?number=</c>: the best contact for a telephone number (caller-ID; <c>030 1234</c>, <c>+49301234</c> and <c>0049 30 1234</c> are the same number with
/// the default country), 404 when none, 400 without a number;</item>
/// <item><c>GET search?q=&amp;limit=</c>: the contacts that match (names, company, department, e-mail, numbers), <c>{ "items": [...] }</c>, at most 100 (default 20).</item>
/// </list>
/// Without the key of a binding every call answers 401 (<see cref="MatBindingAccess.Require"/>).
/// </summary>
public static class DirectoryApi
{
    /// <summary>The capability MatContactHub provides.</summary>
    public const string Capability = "contacts.directory";

    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    public static IEndpointRouteBuilder MapDirectoryApi(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/api/provides/" + Capability, "/api/directory" })
        {
            var group = app.MapGroup(prefix).AllowAnonymous().RequireRateLimiting("directory").AddEndpointFilter(MatBindingAccess.Require(Capability));
            group.MapGet("/lookup", LookupAsync);
            group.MapGet("/search", SearchAsync);
        }

        return app;
    }

    /// <summary><c>GET lookup?number=</c>: the best contact for a number of the address books shared with apps.</summary>
    public static async Task<IResult> LookupAsync(HttpContext http, AppDbContext db, string? number)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(number) || number.Length > 64)
        {
            return Results.BadRequest(new { errors = new Dictionary<string, string> { ["number"] = "A number is required." } });
        }

        var hit = await FindAsync(db, ActorOf(http), number, http.RequestAborted);
        return hit is null ? Results.NotFound() : Results.Json(hit);
    }

    /// <summary><c>GET search?q=&amp;limit=</c>: the contacts of the address books shared with apps that match <paramref name="q"/>.</summary>
    public static async Task<IResult> SearchAsync(HttpContext http, AppDbContext db, string? q, int? limit)
    {
        ArgumentNullException.ThrowIfNull(http);
        var items = await SearchEntriesAsync(db, ActorOf(http), q, limit ?? DefaultLimit, http.RequestAborted);
        return Results.Json(new { items });
    }

    /// <summary>
    /// The best contact for <paramref name="number"/> among the contacts an app may read (address books with <c>AppsEnabled</c>, not hidden): the numbers are compared
    /// in E.164; with several hits a business number beats a mobile, home and other one (the fax last), a manual contact beats a synced one, then the order of the
    /// address books. Null when no number matches.
    /// </summary>
    public static async Task<DirectoryLookup?> FindAsync(AppDbContext db, Actor actor, string number, CancellationToken ct = default)
    {
        var country = await ContactService.DefaultCountryAsync(db, ct);
        var wanted = PhoneNormalizer.Normalize(number, country);
        var patterns = ContactSearch.PhonePatterns(number, country, wholeNumber: true);
        if (wanted is null || patterns.Count == 0)
        {
            return null;
        }

        // the numbers that end in the digits (not the extensions of a switchboard); compared in E.164 below
        var candidates = await ContactSearch.WherePhone(await ReadableAsync(db, actor, ct), patterns).Include(c => c.AddressBook).OrderBy(c => c.Id).Take(500)
            .ToListAsync(ct);
        var best = candidates
            .SelectMany(c => ContactFields.Phones.Select((field, rank) => (Contact: c, Field: field, Rank: rank, Number: field.Get(c))))
            .Where(x => x.Number is not null && PhoneNormalizer.Normalize(x.Number, country) == wanted)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Contact.DataSourceId is null ? 0 : 1)
            .ThenBy(x => x.Contact.AddressBook?.SortOrder ?? 0)
            .ThenBy(x => x.Contact.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Contact.Id)
            .FirstOrDefault();
        if (best.Contact is not { } c)
        {
            return null;
        }

        return new DirectoryLookup(c.Token.ToString("D"), c.DisplayName, c.Company, c.Department, c.Email ?? c.Email2 ?? c.Email3, ContactFields.PhoneType(best.Field.Name),
            best.Number!, c.AddressBook?.Name ?? string.Empty);
    }

    /// <summary>The contacts an app may read that match <paramref name="search"/>, by name; at most <see cref="MaxLimit"/>.</summary>
    public static async Task<IReadOnlyList<DirectoryEntry>> SearchEntriesAsync(AppDbContext db, Actor actor, string? search, int limit, CancellationToken ct = default)
    {
        var country = await ContactService.DefaultCountryAsync(db, ct);
        var query = ContactSearch.Apply(await ReadableAsync(db, actor, ct), search, country);
        var contacts = await query.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).Take(Math.Clamp(limit, 1, MaxLimit)).Include(c => c.AddressBook).ToListAsync(ct);
        return [.. contacts.Select(c => Entry(c, country))];
    }

    private static Actor ActorOf(HttpContext http) => Actor.ForConsumer(MatBindingAccess.ConsumerOf(http) ?? "app");

    /// <summary>The contacts an app may read: of the address books the actor sees (for an app: those with <c>AppsEnabled</c>), never a hidden one.</summary>
    private static async Task<IQueryable<Contact>> ReadableAsync(AppDbContext db, Actor actor, CancellationToken ct)
    {
        var books = (await AddressBookService.RolesAsync(db, actor, ct)).Keys.ToList();
        return db.Contacts.AsNoTracking().Where(c => books.Contains(c.AddressBookId) && !c.Hidden);
    }

    private static DirectoryEntry Entry(Contact c, string country)
        => new(c.Token.ToString("D"), c.DisplayName, c.FirstName, c.LastName, c.Company, c.Department, c.JobTitle, c.Email ?? c.Email2 ?? c.Email3,
            [.. ContactFields.Phones.Select(f => (f, Number: f.Get(c))).Where(p => !string.IsNullOrWhiteSpace(p.Number))
                .Select(p => new DirectoryPhone(ContactFields.PhoneType(p.f.Name), p.Number!, PhoneNormalizer.Normalize(p.Number, country)))],
            c.AddressBook?.Name ?? string.Empty);
}
