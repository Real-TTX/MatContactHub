using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Contacts;

/// <summary>
/// A contact (module C owns it; FOUNDATION PLACEHOLDER: the fields of the tab General and the main numbers; the tabs General / Communication / Address / More / Source
/// follow). A manual contact is edited by an editor of its address book; of a synced contact only Hidden can be changed.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public long AddressBookId { get; set; }

    [BindProperty] public string FirstName { get; set; } = string.Empty;

    [BindProperty] public string LastName { get; set; } = string.Empty;

    [BindProperty] public string DisplayName { get; set; } = string.Empty;

    [BindProperty] public string Company { get; set; } = string.Empty;

    [BindProperty] public string Email { get; set; } = string.Empty;

    [BindProperty] public string PhoneBusiness { get; set; } = string.Empty;

    [BindProperty] public string PhoneMobile { get; set; } = string.Empty;

    [BindProperty] public bool Hidden { get; set; }

    public bool IsNew => Id is null;

    /// <summary>The contact comes from a data source: read-only but for Hidden.</summary>
    public bool Synced { get; private set; }

    public string? SourceName { get; private set; }

    /// <summary>The person may change the fields (an editor of the address book, a manual contact).</summary>
    public bool CanEdit { get; private set; }

    public List<Choice> AddressBooks { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await ContactService.GetAsync(db, Actor.ForUser(Me), id, HttpContext.RequestAborted) is not { } contact)
            {
                return NotFound();
            }

            var f = contact.Form;
            (AddressBookId, FirstName, LastName, DisplayName, Company, Email, PhoneBusiness, PhoneMobile, Hidden) =
                (f.AddressBookId, f.FirstName ?? string.Empty, f.LastName ?? string.Empty, f.DisplayName ?? string.Empty, f.Company ?? string.Empty, f.Email ?? string.Empty,
                    f.PhoneBusiness ?? string.Empty, f.PhoneMobile ?? string.Empty, f.Hidden);
            Synced = contact.Synced;
            SourceName = contact.SourceName;
            CanEdit = !contact.Synced && contact.Role >= AddressBookRole.Editor;
        }

        await LoadAsync();
        if (IsNew)
        {
            if (AddressBooks.Count == 0)
            {
                return NotFound();
            }

            AddressBookId = long.TryParse(Request.Query["book"], out var book) && AddressBooks.Any(b => b.Value == book.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ? book
                : long.Parse(AddressBooks[0].Value, System.Globalization.CultureInfo.InvariantCulture);
            CanEdit = true;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = new ContactForm(AddressBookId, FirstName, LastName, DisplayName, Company, Email, PhoneBusiness, PhoneMobile, Hidden);
        var saved = await ContactService.SaveAsync(db, Actor.ForUser(Me), Id, form, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            CanEdit = true;
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Contacts");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var deleted = await ContactService.DeleteAsync(db, Actor.ForUser(Me), Id ?? 0, HttpContext.RequestAborted);
        if (!deleted.Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Contacts.Deleted"));
        return LocalRedirect("/Contacts");
    }

    private async Task LoadAsync()
    {
        var books = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
        AddressBooks = [.. books.Where(b => b.Role >= AddressBookRole.Editor || b.Id == AddressBookId)
            .Select(b => new Choice(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), b.Name))];
    }
}
