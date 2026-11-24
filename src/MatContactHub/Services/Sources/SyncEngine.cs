using MatContactHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Sources;

/// <summary>
/// The sync of one data source (module B owns this class; FOUNDATION STUB: records a run that fails with "not implemented yet"). The real engine reads the records of the
/// connector, maps them (<see cref="FieldMapping"/>, transforms, <c>Contacts.PhoneNormalizer</c>), upserts by (DataSourceId, ExternalKey) – also a softly deleted
/// contact comes back as the same row –, skips unchanged ones by <see cref="Contact.SyncHash"/>, deletes the missing ones when <see cref="DataSource.DeleteMissing"/>,
/// never touches <see cref="Contact.Hidden"/>, and calls <c>Contacts.DirectoryChanges.Bump()</c> after bulk changes past the change tracker.
/// </summary>
public static class SyncEngine
{
    /// <summary>Runs the sync of a data source now and answers the finished run; null when the actor may not or the source does not exist.</summary>
    public static async Task<SyncRun?> RunAsync(AppDbContext db, Actor actor, long dataSourceId, SyncTrigger trigger, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.FirstOrDefaultAsync(s => s.Id == dataSourceId, ct) is not { } source)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var run = new SyncRun
        {
            DataSourceId = source.Id,
            Trigger = trigger,
            State = SyncState.Failed,
            StartDate = now,
            EndDate = now,
            Message = "The sync engine is not implemented yet.",
        };
        db.SyncRuns.Add(run);
        source.LastRunDate = now;
        source.LastRunState = run.State;
        source.LastError = run.Message;
        source.NextRunDate = source.IntervalMinutes > 0 ? now.AddMinutes(source.IntervalMinutes) : null;
        await db.SaveChangesAsync(ct);
        return run;
    }
}
