using System.Globalization;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Sources;

/// <summary>One run of a data source: its numbers, its message and, while it goes on, Cancel (administrators; module B).</summary>
public sealed class RunModel(AppDbContext db, SyncQueue queue) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public SyncRunDetail Run { get; private set; } = null!;

    public string Zone => Me.TimeZone;

    /// <summary>Does the run go on in this process (so Cancel can stop it)?</summary>
    public bool CanCancel => Run.State == SyncState.Running && queue.IsRunning(Run.DataSourceId);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await DataSourceService.GetRunAsync(db, Actor.ForUser(Me), Id, HttpContext.RequestAborted) is not { } run)
        {
            return NotFound();
        }

        Run = run;
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        if (await DataSourceService.GetRunAsync(db, Actor.ForUser(Me), Id, HttpContext.RequestAborted) is not { } run)
        {
            return NotFound();
        }

        var cancelled = run.State == SyncState.Running
            && (await DataSourceService.CancelSyncAsync(db, Actor.ForUser(Me), queue, run.DataSourceId, HttpContext.RequestAborted)).Value;
        Flash(cancelled ? Text.T("Sources.CancelRequested") : Text.T("Sources.NotRunning"));
        return LocalRedirect($"/Sources/Run?id={Id}");
    }

    /// <summary>How long a run took: <c>0:42</c>, <c>12:05</c>, <c>1:02:03</c>; empty while it goes on.</summary>
    public static string Duration(DateTime start, DateTime? end)
    {
        if (end is not { } stop)
        {
            return string.Empty;
        }

        var span = stop - start;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
