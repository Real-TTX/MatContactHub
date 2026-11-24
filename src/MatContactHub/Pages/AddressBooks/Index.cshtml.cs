using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Pages.AddressBooks;

/// <summary>
/// The address books the person may see (module C owns it; FOUNDATION PLACEHOLDER: a list with search, the filter "shared with", sort and pager). The address books
/// of a person are few, so the page filters the list of <see cref="AddressBookService.ListAsync"/> in memory.
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<AddressBookRow> Items { get; private set; } = [];

    public bool IsAdmin => Me.IsAdmin;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var use = ListQuery.Text(Request, "use");
        var sort = ListQuery.Text(Request, "sort") ?? "order";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var all = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);

        IEnumerable<AddressBookRow> query = all;
        if (search is not null)
        {
            query = query.Where(b => Has(b.Name, search) || Has(b.Description, search) || Has(b.LdapName, search));
        }

        query = use switch
        {
            "shared" => query.Where(b => b.IsShared),
            "ldap" => query.Where(b => b.LdapEnabled),
            "apps" => query.Where(b => b.AppsEnabled),
            _ => query,
        };

        // the list of the service comes in the order of the address books (SortOrder, then the name)
        query = (sort, desc) switch
        {
            ("name", false) => query.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase),
            ("name", true) => query.OrderByDescending(b => b.Name, StringComparer.CurrentCultureIgnoreCase),
            ("contacts", false) => query.OrderBy(b => b.Contacts),
            ("contacts", true) => query.OrderByDescending(b => b.Contacts),
            (_, true) => query.Reverse(),
            _ => query,
        };

        var matching = query.ToList();
        Items = [.. ListQuery.Slice(matching, page, PageSize)];
        Toolbar = new ToolbarModel
        {
            Id = "addressbooks",
            Action = "/AddressBooks",
            Label = Text.T("AddressBooks.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("AddressBooks.Search"),
            Filters =
            [
                new FilterField("use", Text.T("AddressBooks.Col.Shared"),
                    [new("shared", Text.T("AddressBooks.Shared")), new("ldap", Text.T("AddressBooks.LdapEnabled")), new("apps", Text.T("AddressBooks.Apps"))], use),
            ],
            SortOptions = [new("order", Text.T("AddressBooks.SortOrder")), new("name", Text.T("Common.Name")), new("contacts", Text.T("Contacts.Title"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "order",
            Total = all.Count,
            Matching = matching.Count,
            Noun = Text.T("AddressBooks.Many"),
            NounOne = Text.T("AddressBooks.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching.Count, Url = Toolbar.UrlWithPage };
    }

    private static bool Has(string? value, string search) => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
}
