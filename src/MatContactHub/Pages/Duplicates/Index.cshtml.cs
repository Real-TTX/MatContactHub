using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Duplicates;

/// <summary>
/// The duplicates among the contacts the person may see (module C): the groups of <see cref="DuplicateService.FindAsync"/> with search, the filter "the same" (number,
/// e-mail address, name and company), sort and pager. Each group is merged into the contact chosen to keep, or marked "not a duplicate"; both need the role
/// Editor in every address book of the group. The page filters the groups in memory (they are few compared with the contacts).
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 20;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var reason = Enum.TryParse<DuplicateReason>(ListQuery.Text(Request, "reason"), out var r) && Enum.IsDefined(r) ? r : (DuplicateReason?)null;
        var sort = ListQuery.Text(Request, "sort") ?? "size";
        var desc = ListQuery.Descending(Request, sort == "size");
        var page = ListQuery.Page(Request);
        var all = await DuplicateService.FindAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);

        IEnumerable<DuplicateGroup> query = all;
        if (search is not null)
        {
            query = query.Where(g => g.Matches.Any(m => Has(m.Value, search))
                || g.Contacts.Any(c => Has(c.DisplayName, search) || Has(c.Company, search) || Has(c.Email, search) || Has(c.Phone, search)));
        }

        if (reason is { } wanted)
        {
            query = query.Where(g => g.Reasons.Contains(wanted));
        }

        query = (sort, desc) switch
        {
            ("size", false) => query.OrderBy(g => g.Contacts.Count).ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase),
            ("size", true) => query.OrderByDescending(g => g.Contacts.Count).ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase),
            (_, true) => query.OrderByDescending(g => g.Label, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase),
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
            SortOptions = [new("size", Text.T("Duplicates.Sort.Size")), new("key", Text.T("Duplicates.Sort.Key"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "size",
            DefaultDescending = true,
            Total = all.Count,
            Matching = matching.Count,
            Noun = Text.T("Duplicates.Many"),
            NounOne = Text.T("Duplicates.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = Toolbar.UrlWithPage };
    }

    /// <summary>Merges the contacts of a group into the one chosen to keep.</summary>
    public async Task<IActionResult> OnPostMergeAsync([FromForm] long keep, [FromForm] long[] ids, [FromForm] string? returnUrl)
    {
        var merged = await DuplicateService.MergeAsync(db, Actor.ForUser(Me), keep, ids, HttpContext.RequestAborted);
        if (merged.NotAllowed)
        {
            return NotFound();
        }

        if (merged.Ok)
        {
            var count = ids.Count(id => id != keep);
            Flash(Text.T("Duplicates.Merged", count + 1));
        }
        else
        {
            FlashError(merged.Error!);
        }

        return Back(returnUrl, "/Duplicates");
    }

    /// <summary>Marks the contacts of a group as "not a duplicate" of each other.</summary>
    public async Task<IActionResult> OnPostIgnoreAsync([FromForm] long[] ids, [FromForm] string? returnUrl)
    {
        var ignored = await DuplicateService.IgnoreAsync(db, Actor.ForUser(Me), ids, HttpContext.RequestAborted);
        if (ignored.NotAllowed)
        {
            return NotFound();
        }

        if (ignored.Ok)
        {
            Flash(Text.T("Duplicates.Ignored"));
        }
        else
        {
            FlashError(ignored.Error!);
        }

        return Back(returnUrl, "/Duplicates");
    }

    private static bool Has(string? value, string search) => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
}
