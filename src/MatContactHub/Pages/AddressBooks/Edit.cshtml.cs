using System.Globalization;
using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Ldap;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.AddressBooks;

/// <summary>A role of a person as the tab Access sends it.</summary>
public sealed class AccessInput
{
    public long UserId { get; set; }

    public AddressBookRole Role { get; set; } = AddressBookRole.Viewer;
}

/// <summary>
/// An address book (administrators; module C) with the tabs General / Access / LDAP &amp; apps in one form. Access: everybody (IsShared) or the people with the role
/// Viewer or Editor; a person is added in one line (Add saves the form and stays on the tab), removed by the button of the row. LDAP &amp; apps: served by the LDAP
/// server as <c>ou=&lt;LDAP name&gt;,&lt;base DN&gt;</c>, read by the apps of <c>contacts.directory</c>. Delete is refused while a data source writes into it,
/// otherwise its contacts go with it.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    public static readonly string[] Tabs = ["general", "access", "ldap"];

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string LdapName { get; set; } = string.Empty;

    [BindProperty] public bool LdapEnabled { get; set; }

    [BindProperty] public bool AppsEnabled { get; set; }

    [BindProperty] public bool IsShared { get; set; }

    [BindProperty] public string Color { get; set; } = string.Empty;

    [BindProperty] public int SortOrder { get; set; }

    [BindProperty] public List<AccessInput> Access { get; set; } = [];

    /// <summary>The person of the line "add" of the tab Access.</summary>
    [BindProperty] public long? NewUserId { get; set; }

    [BindProperty] public AddressBookRole NewRole { get; set; } = AddressBookRole.Viewer;

    public bool IsNew => Id is null;

    public AddressBookFacts? Facts { get; private set; }

    /// <summary>Every active account (names for the rows of Access, the choices of the line "add").</summary>
    public IReadOnlyList<AddressBookPerson> People { get; private set; } = [];

    public string BaseDn { get; private set; } = LdapSettings.DefaultBaseDn;

    public bool LdapServerEnabled { get; private set; }

    /// <summary>The name in the head of the page (the saved one, not what was typed).</summary>
    public string Title { get; private set; } = string.Empty;

    public IReadOnlyList<Choice> RoleChoices { get; } =
    [
        new(nameof(AddressBookRole.Viewer), Text.T("AddressBooks.Role.Viewer"), Text.T("AddressBooks.Role.Viewer.Hint")),
        new(nameof(AddressBookRole.Editor), Text.T("AddressBooks.Role.Editor"), Text.T("AddressBooks.Role.Editor.Hint")),
    ];

    /// <summary>The people who can still be added (not in the list yet).</summary>
    public IEnumerable<Choice> Candidates => People.Where(p => Access.All(a => a.UserId != p.UserId))
        .Select(p => new Choice(p.UserId.ToString(CultureInfo.InvariantCulture), p.DisplayName, p.Email));

    public AddressBookPerson? PersonOf(long userId) => People.FirstOrDefault(p => p.UserId == userId);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.IsAdmin)
        {
            return NotFound();
        }

        if (Id is { } id)
        {
            if (await AddressBookService.GetAsync(db, Actor.ForUser(Me), id, HttpContext.RequestAborted) is not { } book)
            {
                return NotFound();
            }

            (Name, Description, LdapName, LdapEnabled, AppsEnabled, IsShared, Color, SortOrder) =
                (book.Name, book.Description ?? string.Empty, book.LdapName, book.LdapEnabled, book.AppsEnabled, book.IsShared, book.Color ?? string.Empty, book.SortOrder);
            Access = [.. (book.Access ?? []).Select(a => new AccessInput { UserId = a.UserId, Role = a.Role })];
        }

        await LoadAsync();
        return Page();
    }

    public Task<IActionResult> OnPostAsync() => SaveAsync(stay: false);

    /// <summary>Adds the person of the line "add" (and saves the rest of the form with it).</summary>
    public Task<IActionResult> OnPostAddAccessAsync()
    {
        if (NewUserId is { } user && Access.All(a => a.UserId != user))
        {
            Access.Add(new AccessInput { UserId = user, Role = NewRole == AddressBookRole.Editor ? AddressBookRole.Editor : AddressBookRole.Viewer });
        }

        Tab = "access";
        return SaveAsync(stay: true);
    }

    /// <summary>Removes the role of one person (and saves the rest of the form with it).</summary>
    public Task<IActionResult> OnPostRemoveAccessAsync(long userId)
    {
        Access.RemoveAll(a => a.UserId == userId);
        Tab = "access";
        return SaveAsync(stay: true);
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var deleted = await AddressBookService.DeleteAsync(db, Actor.ForUser(Me), Id ?? 0, HttpContext.RequestAborted);
        if (deleted.NotAllowed)
        {
            return NotFound();
        }

        if (!deleted.Ok)
        {
            FlashError(deleted.Error!);
            return LocalRedirect($"/AddressBooks/Edit?id={Id}");
        }

        Flash(Text.T("AddressBooks.Deleted", deleted.Value));
        return LocalRedirect("/AddressBooks");
    }

    private async Task<IActionResult> SaveAsync(bool stay)
    {
        var form = new AddressBookForm(Name, Description, LdapName, LdapEnabled, AppsEnabled, IsShared, Color, SortOrder,
            [.. Access.Select(a => new AddressBookAccessEntry(a.UserId, a.Role))]);
        var saved = await AddressBookService.SaveAsync(db, Actor.ForUser(Me), Id, form, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            // the messages of the service name their field: it is shown at the field, on its tab
            var field = saved.Error == Text.T("AddressBooks.Name.Invalid") ? nameof(Name)
                : saved.Error == Text.T("AddressBooks.LdapName.Invalid") || saved.Error == Text.T("AddressBooks.LdapName.Taken") ? nameof(LdapName)
                : string.Empty;
            ModelState.AddModelError(field, saved.Error!);
            Tab = field == nameof(LdapName) ? "ldap" : field == nameof(Name) ? "general" : Tab;
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", Name.Trim()));
        return stay ? LocalRedirect($"/AddressBooks/Edit?id={saved.Value}&tab={Tab}") : LocalRedirect("/AddressBooks");
    }

    private async Task LoadAsync()
    {
        var actor = Actor.ForUser(Me);
        Tab = Tabs.Contains(Tab) ? Tab : "general";
        People = await AddressBookService.PeopleAsync(db, actor, HttpContext.RequestAborted);
        var ldap = await LdapSettingsService.LoadAsync(db, HttpContext.RequestAborted);
        BaseDn = ldap.BaseDn;
        LdapServerEnabled = ldap.Enabled;
        if (Id is { } id)
        {
            Facts = await AddressBookService.FactsAsync(db, actor, id, HttpContext.RequestAborted);
            Title = (await AddressBookService.GetAsync(db, actor, id, HttpContext.RequestAborted))?.Name ?? Name;
        }
        else
        {
            Title = Text.T("AddressBooks.New");
        }
    }
}
