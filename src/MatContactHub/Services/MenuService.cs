using MatContactHub.Data;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Services;

/// <summary>What the menu shows: the address books of the person and whether they may create a contact somewhere.</summary>
public sealed class MenuModel
{
    public IReadOnlyList<(long Id, string Name, string? Color)> AddressBooks { get; init; } = [];

    /// <summary>The person is an editor of at least one address book (the main action "New contact").</summary>
    public bool CanCreate { get; init; }
}

/// <summary>One menu of the switcher at the foot of the menu.</summary>
public sealed record MenuArea(string Id, string Title, string Icon);

/// <summary>
/// The menu: the area <c>contacts</c> (New contact, Contacts, Address books, Duplicates, then the address books of the person), <c>settings</c> (the account) and
/// <c>admin</c> (Data sources, LDAP, Users, Connections, Activity, System) for administrators. The layout draws it through
/// <c>Pages/Shared/MenuViewComponent</c>, which builds the model once per request.
/// </summary>
public static class MenuService
{
    /// <summary>The menus of the switcher: the contacts, the settings of the person and, for an administrator, the administration.</summary>
    public static IReadOnlyList<MenuArea> Areas(bool admin)
    {
        var list = new List<MenuArea>
        {
            new("contacts", Localization.Text.T("Shell.Area.Contacts"), "users"),
            new("settings", Localization.Text.T("Settings.Title"), "settings"),
        };
        if (admin)
        {
            list.Add(new MenuArea("admin", Localization.Text.T("Admin.Title"), "shield"));
        }

        return list;
    }

    /// <summary>The menu a page belongs to, by its address.</summary>
    public static string AreaOf(string? path)
    {
        path ??= "/";
        bool Under(string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        if (Under("/Admin") || Under("/Sources"))
        {
            return "admin";
        }

        return Under("/Account") ? "settings" : "contacts";
    }

    /// <summary>The menu of the actor: the address books they see (through the permission check of <see cref="AddressBookService"/>).</summary>
    public static async Task<MenuModel> GetAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        var books = await AddressBookService.ListAsync(db, actor, ct);
        return new MenuModel
        {
            AddressBooks = [.. books.Select(b => (b.Id, b.Name, b.Color))],
            CanCreate = books.Any(b => b.Role >= AddressBookRole.Editor),
        };
    }
}
