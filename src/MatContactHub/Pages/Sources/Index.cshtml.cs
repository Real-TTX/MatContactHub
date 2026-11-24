using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Sources;

/// <summary>
/// The data sources (administrators; module B): a list with search, the filters kind and state, sort, pager, the state of every source (running, waiting, the
/// last run), "Sync now" per line and "Sync all" (both checked in <see cref="DataSourceService"/>). The data sources are few, so the page filters the list of
/// <see cref="DataSourceService.ListAsync"/> in memory.
/// </summary>
public sealed class IndexModel(AppDbContext db, SyncQueue queue) : AppPageModel
{
    private const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<DataSourceRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var kind = Enum.TryParse<DataSourceKind>(ListQuery.Text(Request, "kind"), out var k) && Enum.IsDefined(k) ? k : (DataSourceKind?)null;
        var state = Enum.TryParse<SyncState>(ListQuery.Text(Request, "state"), out var s) && Enum.IsDefined(s) ? s : (SyncState?)null;
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var all = await DataSourceService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);

        IEnumerable<DataSourceRow> query = all;
        if (search is not null)
        {
            query = query.Where(x => x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || x.AddressBook.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        if (kind is not null)
        {
            query = query.Where(x => x.Kind == kind);
        }

        if (state is not null)
        {
            query = query.Where(x => x.LastRunState == state);
        }

        query = (sort, desc) switch
        {
            ("addressbook", false) => query.OrderBy(x => x.AddressBook, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            ("addressbook", true) => query.OrderByDescending(x => x.AddressBook, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            ("lastrun", false) => query.OrderBy(x => x.LastRunDate),
            ("lastrun", true) => query.OrderByDescending(x => x.LastRunDate),
            ("contacts", false) => query.OrderBy(x => x.Contacts),
            ("contacts", true) => query.OrderByDescending(x => x.Contacts),
            (_, true) => query.OrderByDescending(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var matching = query.ToList();
        Items = [.. ListQuery.Slice(matching, page, PageSize)];
        Toolbar = new ToolbarModel
        {
            Id = "sources",
            Action = "/Sources",
            Label = Text.T("Sources.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Sources.Search"),
            Filters =
            [
                new FilterField("kind", Text.T("Sources.Col.Kind"), [.. Enum.GetValues<DataSourceKind>().Select(x => new FilterOption(x.ToString(), Text.T("Sources.Kind." + x)))],
                    kind?.ToString()),
                new FilterField("state", Text.T("Sources.Col.State"), [.. Enum.GetValues<SyncState>().Select(x => new FilterOption(x.ToString(), Text.T("Sources.State." + x)))],
                    state?.ToString()),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("addressbook", Text.T("Contacts.Col.AddressBook")), new("lastrun", Text.T("Sources.Col.LastRun")),
                new("contacts", Text.T("Contacts.Title"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = matching.Count,
            Noun = Text.T("Sources.Many"),
            NounOne = Text.T("Sources.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = Toolbar.UrlWithPage };
    }

    public bool IsRunning(long id) => queue.IsRunning(id);

    public bool IsWaiting(long id) => queue.IsWaiting(id);

    public async Task<IActionResult> OnPostSyncAsync(long id)
    {
        if (!(await DataSourceService.RequestSyncAsync(db, Actor.ForUser(Me), queue, id, HttpContext.RequestAborted)).Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Sources.SyncRequested"));
        return LocalRedirect("/Sources");
    }

    public IActionResult OnPostSyncAll()
    {
        if (!DataSourceService.RequestSyncAll(Actor.ForUser(Me), queue).Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Sources.SyncRequested"));
        return LocalRedirect("/Sources");
    }
}
