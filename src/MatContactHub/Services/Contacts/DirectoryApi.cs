using MatPlatform;

namespace MatContactHub.Services.Contacts;

/// <summary>An entry of the directory API (caller-ID, search).</summary>
public sealed record DirectoryEntry(string Id, string DisplayName, string? Company, string? Department, string? Email, IReadOnlyDictionary<string, string> Phones,
    string AddressBook);

/// <summary>
/// The capability <c>contacts.directory</c> (a key capability: MatPlatform keeps a key per consuming app, <c>MAT_CONTACTS_DIRECTORY_URL</c>, <c>_INTERNAL_URL</c>,
/// <c>_KEY</c>): the apps read the address books with <c>AppsEnabled</c>, with the key as a bearer token. The endpoints answer under
/// <c>/api/provides/contacts.directory/</c> (the platform contract) and under <c>/api/directory/</c> (short form):
/// <list type="bullet">
/// <item><c>GET lookup?number=</c>: the best contact for a telephone number (caller-ID), 404 when none;</item>
/// <item><c>GET search?q=&amp;limit=</c>: the contacts that match, <c>{ "items": [...] }</c>.</item>
/// </list>
/// Module C owns this class. FOUNDATION STUB: the routes and the key check are in place; lookup answers 404 and search an empty list.
/// </summary>
public static class DirectoryApi
{
    /// <summary>The capability MatContactHub provides.</summary>
    public const string Capability = "contacts.directory";

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
    public static Task<IResult> LookupAsync(HttpContext http, string? number)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Task.FromResult(string.IsNullOrWhiteSpace(number) ? Results.BadRequest() : Results.NotFound());
    }

    /// <summary><c>GET search?q=&amp;limit=</c>: the contacts of the address books shared with apps that match <paramref name="q"/>.</summary>
    public static Task<IResult> SearchAsync(HttpContext http, string? q, int? limit)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Task.FromResult(Results.Json(new { items = Array.Empty<DirectoryEntry>() }));
    }
}
