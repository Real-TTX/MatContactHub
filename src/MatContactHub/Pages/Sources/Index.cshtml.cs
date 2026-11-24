using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Sources;

/// <summary>The data sources (administrators; module B owns it; FOUNDATION PLACEHOLDER: a plain list and "Sync all"; toolbar, pager and the run detail follow).</summary>
public sealed class IndexModel(AppDbContext db, SyncQueue queue) : AppPageModel
{
    public IReadOnlyList<DataSourceRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync() => Items = await DataSourceService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);

    public IActionResult OnPostSyncAll()
    {
        queue.RequestAll();
        Flash(Text.T("Sources.SyncRequested"));
        return LocalRedirect("/Sources");
    }
}
