using System.Globalization;
using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Contacts;

/// <summary>
/// A contact (module C) with the tabs General / Communication / Address / More / Source in one form. A manual contact is edited by an editor of its address book; of a
/// synced contact only Hidden can be changed (the source wins), and a viewer only looks. A new contact goes into the address book of <c>?book=</c>, else the one the
/// person used last. The buttons: Save, Back, Export (vCard), a space, Delete.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    /// <summary>The tabs of the page, and which fields each has (the tab of the first error is opened).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Tabs = new Dictionary<string, string[]>
    {
        ["general"] = ["AddressBookId", "Salutation", "Title", "FirstName", "MiddleName", "LastName", "DisplayName", "Company", "Department", "JobTitle", "Hidden"],
        ["communication"] = ["Email", "Email2", "Email3", "PhoneBusiness", "PhoneBusiness2", "PhoneMobile", "PhoneHome", "PhoneOther", "Fax", "Website"],
        ["address"] = ["Street", "PostalCode", "City", "State", "Country"],
        ["more"] = ["Birthday", "CustomerNumber", "Notes", "Custom1", "Custom2", "Custom3", "Custom4", "Custom5"],
        ["source"] = [],
    };

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public ContactForm Form { get; set; } = new();

    /// <summary>The tab that is open.</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }

    public bool IsNew => Id is null;

    public ContactDetail? Detail { get; private set; }

    /// <summary>The contact comes from a data source: read-only but for Hidden.</summary>
    public bool Synced => Detail?.Synced == true;

    /// <summary>The person may change the fields (an editor of the address book, a manual contact).</summary>
    public bool CanEdit { get; private set; }

    /// <summary>The person may change Hidden and save (an editor of the address book; a viewer only looks).</summary>
    public bool CanChangeHidden { get; private set; }

    public bool IsAdmin => Me.IsAdmin;

    public string Zone => Me.TimeZone;

    public List<Choice> AddressBooks { get; private set; } = [];

    /// <summary>The name in the head of the page and the breadcrumb.</summary>
    public string Title => IsNew ? Text.T("Contacts.New") : Detail?.Name ?? string.Empty;

    public async Task<IActionResult> OnGetAsync(long? book)
    {
        var actor = Actor.ForUser(Me);
        if (Id is { } id)
        {
            if (await LoadAsync(id) is null)
            {
                return NotFound();
            }

            Form = Detail!.Form;
        }
        else
        {
            if (await ContactService.DefaultAddressBookAsync(db, actor, book, HttpContext.RequestAborted) is not { } target)
            {
                return NotFound();
            }

            Form = new ContactForm { AddressBookId = target };
            CanEdit = CanChangeHidden = true;
        }

        Tab = Tabs.ContainsKey(Tab ?? string.Empty) && (Tab != "source" || !IsNew) ? Tab : "general";
        await LoadBooksAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var actor = Actor.ForUser(Me);
        if (Id is { } id && await LoadAsync(id) is null)
        {
            return NotFound();
        }

        if (!Synced)
        {
            foreach (var error in ContactService.Validate(Form))
            {
                ModelState.AddModelError("Form." + error.Field, error.Message);
            }
        }

        if (ModelState.ErrorCount == 0)
        {
            var saved = await ContactService.SaveAsync(db, actor, Id, Form, HttpContext.RequestAborted);
            if (saved.NotAllowed)
            {
                return NotFound();
            }

            if (saved.Ok)
            {
                // a synced contact posts only Hidden (its other fields are disabled): its name is the stored one
                var named = Synced ? Detail!.Form : Form;
                Flash(Text.T("Common.SavedNamed", ContactService.DisplayNameOf(named.DisplayName, named.FirstName, named.LastName, named.Company)));
                return LocalRedirect(Synced ? $"/Contacts/Edit?id={saved.Value}" : "/Contacts");
            }

            ModelState.AddModelError(string.Empty, saved.Error!);
        }

        if (IsNew)
        {
            CanEdit = CanChangeHidden = true;
        }
        else if (Synced)
        {
            Form = Detail!.Form;
        }

        Tab = FirstErrorTab() ?? (Tabs.ContainsKey(Tab ?? string.Empty) ? Tab : "general");
        await LoadBooksAsync();
        return Page();
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

    /// <summary>The contact as a vCard (3.0, or 4.0 with <c>v=4</c>).</summary>
    public async Task<IActionResult> OnGetExportAsync(int v = 3)
    {
        if (Id is not { } id)
        {
            return NotFound();
        }

        var export = await ContactService.ExportAsync(db, Actor.ForUser(Me), [id], v, HttpContext.RequestAborted);
        return export.Count == 0 ? NotFound() : ContactView.Download(export);
    }

    private async Task<ContactDetail?> LoadAsync(long id)
    {
        Detail = await ContactService.GetAsync(db, Actor.ForUser(Me), id, HttpContext.RequestAborted);
        if (Detail is not null)
        {
            CanChangeHidden = Detail.Role >= AddressBookRole.Editor;
            CanEdit = !Detail.Synced && CanChangeHidden;
        }

        return Detail;
    }

    private string? FirstErrorTab()
    {
        foreach (var (tab, fields) in Tabs)
        {
            if (fields.Any(f => ModelState.TryGetValue("Form." + f, out var state) && state.Errors.Count > 0))
            {
                return tab;
            }
        }

        return null;
    }

    private async Task LoadBooksAsync()
    {
        var books = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
        AddressBooks = [.. books.Where(b => b.Role >= AddressBookRole.Editor || b.Id == Form.AddressBookId)
            .Select(b => new Choice(b.Id.ToString(CultureInfo.InvariantCulture), b.Name, null, b.Color))];
    }
}
