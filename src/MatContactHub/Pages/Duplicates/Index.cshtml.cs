using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Pages.Duplicates;

/// <summary>The duplicates among the contacts the person may see (module C owns it; FOUNDATION PLACEHOLDER: the groups; Merge and Not a duplicate follow).</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<DuplicateGroup> Groups { get; private set; } = [];

    public async Task OnGetAsync() => Groups = await DuplicateService.FindAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
}
