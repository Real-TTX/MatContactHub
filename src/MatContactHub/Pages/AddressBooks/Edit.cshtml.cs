using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.AddressBooks;

/// <summary>
/// An address book (administrators; module C owns it; FOUNDATION PLACEHOLDER: the tab General with the switches of sharing; the tabs Access and LDAP &amp; apps follow).
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string LdapName { get; set; } = string.Empty;

    [BindProperty] public bool LdapEnabled { get; set; }

    [BindProperty] public bool AppsEnabled { get; set; }

    [BindProperty] public bool IsShared { get; set; }

    [BindProperty] public string Color { get; set; } = string.Empty;

    [BindProperty] public int SortOrder { get; set; }

    public bool IsNew => Id is null;

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
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = new AddressBookForm(Name, Description, LdapName, LdapEnabled, AppsEnabled, IsShared, Color, SortOrder);
        var saved = await AddressBookService.SaveAsync(db, Actor.ForUser(Me), Id, form, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            return Page();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/AddressBooks");
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

        Flash(Text.T("AddressBooks.Deleted"));
        return LocalRedirect("/AddressBooks");
    }
}
