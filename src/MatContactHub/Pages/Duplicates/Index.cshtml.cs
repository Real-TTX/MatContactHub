using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Pages.Duplicates;

/// <summary>
/// The duplicates among the contacts the person may see (module C owns it; FOUNDATION PLACEHOLDER: the groups with search, the filter reason, sort and pager;
/// Merge and Not a duplicate follow). The page filters the groups of <see cref="DuplicateService.FindAsync"/> in memory.
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var reason = Enum.TryParse<DuplicateReason>(ListQuery.Text(Request, "reason"), out var r) && Enum.IsDefined(r) ? r : (DuplicateReason?)null;
        var sort = ListQuery.Text(Request, "sort") ?? "key";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var all = await DuplicateService.FindAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);

        IEnumerable<DuplicateGroup> query = all;
        if (search is not null)
        {
            query = query.Where(g => Has(g.Key, search) || g.Contacts.Any(c => Has(c.DisplayName, search) || Has(c.Company, search) || Has(c.Email, search) || Has(c.Phone, search)));
        }

        if (reason is not null)
        {
            query = query.Where(g => g.Reason == reason);
        }

        query = (sort, desc) switch
        {
            ("size", false) => query.OrderBy(g => g.Contacts.Count).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase),
            ("size", true) => query.OrderByDescending(g => g.Contacts.Count).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase),
            (_, true) => query.OrderByDescending(g => g.Key, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase),
        };

        var matching = query.ToList();
        Groups = [.. ListQuery.Slice(matching, page, PageSize)];
        Toolbar = new ToolbarModel
        {
            Id = "duplicates",
            Action = "/Duplicates",
            Label = Text.T("Duplicates.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Contacts.Search"),
            Filters =
            [
                new FilterField("reason", Text.T("Duplicates.Reason"), [.. Enum.GetValues<DuplicateReason>().Select(x => new FilterOption(x.ToString(), Text.T("Duplicates.Reason." + x)))],
                    reason?.ToString()),
            ],
            SortOptions = [new("key", Text.T("Duplicates.Sort.Key")), new("size", Text.T("Duplicates.Sort.Size"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "key",
            Total = all.Count,
            Matching = matching.Count,
            Noun = Text.T("Duplicates.Many"),
            NounOne = Text.T("Duplicates.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = Toolbar.UrlWithPage };
    }

    private static bool Has(string? value, string search) => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
}
