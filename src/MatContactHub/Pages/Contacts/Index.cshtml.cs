using System.Globalization;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Contacts;

/// <summary>
/// The contacts: the start page after the sign-in (module C). The toolbar searches names, company, department, e-mail addresses and numbers (written in any way),
/// filters by address book, origin (manual, synced, one data source) and hidden, and sorts by name, company, city or the last change. A selection of rows is exported
/// as vCards, hidden or shown (editors) and deleted (manual contacts of an editor); the whole list as the tools filter it is exported too. The handler Suggest answers
/// the search in the header bar.
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ContactRow> Items { get; private set; } = [];

    /// <summary>The person is an editor of at least one address book (New contact, Import, Hide, Delete).</summary>
    public bool CanCreate { get; private set; }

    /// <summary>The address of the export of the whole list (the tools in effect).</summary>
    public string ExportUrl(int version) => AddQuery(Toolbar.UrlWithout(string.Empty), "handler=Export&v=" + version.ToString(CultureInfo.InvariantCulture));

    public async Task OnGetAsync()
    {
        var actor = Actor.ForUser(Me);
        var criteria = Criteria();
        var books = await AddressBookService.ListAsync(db, actor, HttpContext.RequestAborted);
        var sources = await ContactService.SourcesAsync(db, actor, HttpContext.RequestAborted);
        CanCreate = books.Any(x => x.Role >= AddressBookRole.Editor);
        var list = await ContactService.ListAsync(db, actor, criteria, HttpContext.RequestAborted);
        Items = list.Items;
        if (criteria.AddressBookId is { } id)
        {
            ViewData["NavKey"] = "addressbook-" + id;
        }

        Toolbar = new ToolbarModel
        {
            Id = "contacts",
            Action = "/Contacts",
            Label = Text.T("Contacts.Title"),
            Search = criteria.Search,
            SearchField = true,
            SearchPlaceholder = Text.T("Contacts.Search"),
            Filters =
            [
                new FilterField("book", Text.T("Contacts.Col.AddressBook"), [.. books.Select(x => new FilterOption(x.Id.ToString(CultureInfo.InvariantCulture), x.Name))],
                    criteria.AddressBookId?.ToString(CultureInfo.InvariantCulture)),
                new FilterField("origin", Text.T("Contacts.Origin"),
                    [
                        new(ContactOrigin.Manual, Text.T("Contacts.Origin.Manual")),
                        new(ContactOrigin.Synced, Text.T("Contacts.Origin.Synced")),
                        .. sources.Select(s => new FilterOption(ContactOrigin.Source(s.Id), s.Name)),
                    ],
                    criteria.Origin),
                new FilterField("hidden", Text.T("Contacts.Hidden"), [new("no", Text.T("Contacts.Hidden.No")), new("yes", Text.T("Contacts.Hidden.Yes"))],
                    criteria.Hidden switch { true => "yes", false => "no", null => null }),
            ],
            SortOptions =
            [
                new("name", Text.T("Common.Name")), new("company", Text.T("Contacts.Col.Company")), new("city", Text.T("Contacts.Field.City")),
                new("changed", Text.T("Contacts.Col.Changed")),
            ],
            Sort = criteria.Sort,
            Descending = criteria.Descending,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Contacts.Many"),
            NounOne = Text.T("Contacts.One"),
        };
        Pager = new PagerModel { Page = criteria.Page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }

    /// <summary>The whole list as the tools filter it, as a vCard file (3.0 or 4.0).</summary>
    public async Task<IActionResult> OnGetExportAsync(int v = 3)
    {
        var export = await ContactService.ExportAsync(db, Actor.ForUser(Me), Criteria() with { Page = 1 }, v, HttpContext.RequestAborted);
        return ContactView.Download(export with { SingleName = null });
    }

    /// <summary>What the selection of rows does: <c>export</c> (vCard 3.0), <c>export4</c>, <c>hide</c>, <c>show</c> or <c>delete</c>.</summary>
    public async Task<IActionResult> OnPostSelectionAsync([FromForm] long[] ids, [FromForm(Name = "do")] string? action, [FromForm] string? returnUrl)
    {
        var actor = Actor.ForUser(Me);
        var ct = HttpContext.RequestAborted;
        if (ids.Length == 0)
        {
            FlashError(Text.T("Contacts.Selection.None"));
            return Back(returnUrl, "/Contacts");
        }

        switch (action)
        {
            case "export":
            case "export4":
                var export = await ContactService.ExportAsync(db, actor, ids, action == "export4" ? 4 : 3, ct);
                return ContactView.Download(export);
            case "hide":
            case "show":
                var changed = await ContactService.SetHiddenAsync(db, actor, ids, action == "hide", ct);
                Flash(Text.T(action == "hide" ? "Contacts.Selection.Hidden" : "Contacts.Selection.Shown", changed));
                break;
            case "delete":
                var deleted = await ContactService.DeleteManyAsync(db, actor, ids, ct);
                Flash(Text.T("Contacts.Selection.Deleted", deleted));
                break;
        }

        return Back(returnUrl, "/Contacts");
    }

    /// <summary>The suggestions of the search in the header bar (MatUi <c>data-autocomplete</c>): the best contacts, each with its company and number.</summary>
    public async Task<IActionResult> OnGetSuggestAsync(string? q, int limit = 8)
    {
        var rows = await ContactService.SuggestAsync(db, Actor.ForUser(Me), q, limit, HttpContext.RequestAborted);
        var items = rows.Select(r => new
        {
            title = r.DisplayName,
            text = string.Join(" · ", new[] { r.Company, r.Phone ?? r.Mobile, r.Email }.Where(x => !string.IsNullOrWhiteSpace(x))),
            href = "/Contacts/Edit?id=" + r.Id.ToString(CultureInfo.InvariantCulture),
            icon = "user",
            kind = r.AddressBook,
            id = "c" + r.Id.ToString(CultureInfo.InvariantCulture),
        });
        return new JsonResult(new { groups = new[] { new { name = Text.T("Contacts.Title"), items } } });
    }

    private ContactCriteria Criteria()
    {
        var hidden = ListQuery.Text(Request, "hidden") switch { "yes" => true, "no" => (bool?)false, _ => null };
        var origin = ListQuery.Text(Request, "origin");
        var sort = ListQuery.Text(Request, "sort") is "company" or "city" or "changed" ? ListQuery.Text(Request, "sort")! : "name";
        return new ContactCriteria(
            ListQuery.Text(Request, "q"),
            long.TryParse(ListQuery.Text(Request, "book"), out var b) ? b : null,
            sort,
            ListQuery.Descending(Request),
            ListQuery.Page(Request),
            PageSize,
            origin,
            hidden);
    }

    private static string AddQuery(string url, string query) => url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;
}
