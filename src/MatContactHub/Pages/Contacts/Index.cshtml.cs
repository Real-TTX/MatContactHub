using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Pages.Contacts;

/// <summary>
/// The contacts: the start page after the sign-in (module C owns it; FOUNDATION PLACEHOLDER: search, a filter by address book, sort and pager). The toolbar gets the
/// filters source / manual or synced / hidden, the list actions and the vCard export from module C.
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ContactRow> Items { get; private set; } = [];

    /// <summary>The person is an editor of at least one address book (the button "New contact").</summary>
    public bool CanCreate { get; private set; }

    public async Task OnGetAsync()
    {
        var actor = Actor.ForUser(Me);
        var search = ListQuery.Text(Request, "q");
        var book = long.TryParse(ListQuery.Text(Request, "book"), out var b) ? b : (long?)null;
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var books = await AddressBookService.ListAsync(db, actor, HttpContext.RequestAborted);
        CanCreate = books.Any(x => x.Role >= AddressBookRole.Editor);
        var list = await ContactService.ListAsync(db, actor, new ContactCriteria(search, book, sort, desc, page, PageSize), HttpContext.RequestAborted);
        Items = list.Items;
        if (book is { } id)
        {
            ViewData["NavKey"] = "addressbook-" + id;
        }

        Toolbar = new ToolbarModel
        {
            Id = "contacts",
            Action = "/Contacts",
            Label = Text.T("Contacts.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Contacts.Search"),
            Filters =
            [
                new FilterField("book", Text.T("Contacts.Col.AddressBook"), [.. books.Select(x => new FilterOption(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name))],
                    book?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("company", Text.T("Contacts.Col.Company")), new("changed", Text.T("Contacts.Col.Changed"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Contacts.Many"),
            NounOne = Text.T("Contacts.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}
