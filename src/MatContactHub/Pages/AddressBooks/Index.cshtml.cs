using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Pages.AddressBooks;

/// <summary>The address books the person may see (module C owns it; FOUNDATION PLACEHOLDER: a plain list; the toolbar follows).</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<AddressBookRow> Items { get; private set; } = [];

    public bool IsAdmin => Me.IsAdmin;

    public async Task OnGetAsync() => Items = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
}
