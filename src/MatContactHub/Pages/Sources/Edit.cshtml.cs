using System.Globalization;
using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Sources;

/// <summary>
/// A data source (administrators; module B owns it; FOUNDATION PLACEHOLDER: the tab General; the tabs Connection / Mapping / Preview / Runs follow, and the fields of a
/// kind only show for that kind).
/// </summary>
public sealed class EditModel(AppDbContext db, SyncQueue queue) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public DataSourceKind Kind { get; set; } = DataSourceKind.Rest;

    [BindProperty] public long AddressBookId { get; set; }

    [BindProperty] public bool Enabled { get; set; } = true;

    [BindProperty] public int IntervalMinutes { get; set; } = 60;

    [BindProperty] public string KeyField { get; set; } = string.Empty;

    [BindProperty] public bool DeleteMissing { get; set; } = true;

    public bool IsNew => Id is null;

    public List<Choice> Kinds { get; } = [.. Enum.GetValues<DataSourceKind>().Select(k => new Choice(k.ToString(), Text.T("Sources.Kind." + k)))];

    public List<Choice> AddressBooks { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await DataSourceService.GetAsync(db, Actor.ForUser(Me), id, HttpContext.RequestAborted) is not { } source)
            {
                return NotFound();
            }

            (Name, Kind, AddressBookId, Enabled, IntervalMinutes, KeyField, DeleteMissing) =
                (source.Name, source.Kind, source.AddressBookId, source.Enabled, source.IntervalMinutes, source.KeyField, source.DeleteMissing);
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = new DataSourceForm(Name, Kind, AddressBookId, Enabled, IntervalMinutes, KeyField, DeleteMissing);
        var saved = await DataSourceService.SaveAsync(db, Actor.ForUser(Me), Id, form, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(string.Empty, saved.Error!);
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Sources");
    }

    public IActionResult OnPostSync()
    {
        if (Id is { } id)
        {
            queue.Request(id);
            Flash(Text.T("Sources.SyncRequested"));
        }

        return LocalRedirect($"/Sources/Edit?id={Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var deleted = await DataSourceService.DeleteAsync(db, Actor.ForUser(Me), Id ?? 0, HttpContext.RequestAborted);
        if (!deleted.Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Sources.Deleted"));
        return LocalRedirect("/Sources");
    }

    private async Task LoadAsync()
    {
        var books = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
        AddressBooks = [.. books.Select(b => new Choice(b.Id.ToString(CultureInfo.InvariantCulture), b.Name))];
    }
}
