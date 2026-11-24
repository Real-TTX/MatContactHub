using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Sources;

/// <summary>
/// The queue of "Sync now" (the pages of the data sources, the platform action <c>syncAll</c>, the quick action of the launcher) and the register of the runs that
/// go on in this process: one run per data source at a time, whether it was asked for by hand or by the schedule. A singleton; the <see cref="SyncScheduler"/>
/// takes the requests. A run can be cancelled (<see cref="Cancel"/>).
/// </summary>
public sealed class SyncQueue
{
    /// <summary>The id that stands for "every enabled data source".</summary>
    public const long All = 0;

    private readonly Channel<long> _requests = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<long, byte> _waiting = new();

    /// <summary>Asks for a sync of one data source as soon as possible.</summary>
    public void Request(long dataSourceId)
    {
        _waiting.TryAdd(dataSourceId, 0);
        _requests.Writer.TryWrite(dataSourceId);
    }

    /// <summary>Asks for a sync of every enabled data source.</summary>
    public void RequestAll() => _requests.Writer.TryWrite(All);

    /// <summary>The requests (read by <see cref="SyncScheduler"/> only).</summary>
    public ChannelReader<long> Reader => _requests.Reader;

    /// <summary>Does a run of the data source go on now?</summary>
    public bool IsRunning(long dataSourceId) => _running.ContainsKey(dataSourceId);

    /// <summary>Is a sync of the data source asked for and not started yet?</summary>
    public bool IsWaiting(long dataSourceId) => _waiting.ContainsKey(dataSourceId);

    /// <summary>Cancels the run of a data source; false when none goes on.</summary>
    public bool Cancel(long dataSourceId)
    {
        if (!_running.TryGetValue(dataSourceId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Marks the start of a run of the data source; false (and nothing changes) when one goes on already. The token ends with <paramref name="stopping"/> too.</summary>
    public bool TryStart(long dataSourceId, CancellationToken stopping, out CancellationTokenSource cancel)
    {
        cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (_running.TryAdd(dataSourceId, cancel))
        {
            _waiting.TryRemove(dataSourceId, out _);
            return true;
        }

        cancel.Dispose();
        return false;
    }

    /// <summary>Marks the end of a run of the data source.</summary>
    public void Finish(long dataSourceId)
    {
        if (_running.TryRemove(dataSourceId, out var cancel))
        {
            cancel.Dispose();
        }
    }

    /// <summary>A request that is dropped (the source runs already, or it is gone) is not waiting any more.</summary>
    public void Forget(long dataSourceId) => _waiting.TryRemove(dataSourceId, out _);
}

/// <summary>
/// Runs the syncs in the background (module B). At the start the runs that a previous process left "Running" are marked failed. Every 30 seconds the data
/// sources that are due start (<see cref="SyncEngine.DueAt"/>: switched on, an interval, <c>NextRunDate</c> reached); the requests of <see cref="SyncQueue"/>
/// start at once. One run per data source at a time (<see cref="SyncQueue.TryStart"/>, the scheduled and the manual path alike), at most
/// <see cref="MaxParallel"/> runs at once. Each run has its own scope (its own <see cref="AppDbContext"/>) and the actor of the app.
/// </summary>
public sealed class SyncScheduler(SyncQueue queue, SourceEnvironment env, IServiceScopeFactory scopes, ILogger<SyncScheduler> logger) : BackgroundService
{
    /// <summary>How often the scheduler looks for due data sources.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>The most runs at the same time.</summary>
    public const int MaxParallel = 3;

    private static readonly Actor System = Actor.System("sync");

    private readonly SemaphoreSlim _slots = new(MaxParallel, MaxParallel);
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CultureInfo.CurrentUICulture = Languages.UiCulture(Languages.Default);
        try
        {
            await FailStaleRunsAsync(stoppingToken);
            var requests = ConsumeAsync(stoppingToken);
            using var timer = new PeriodicTimer(Tick);
            do
            {
                await StartDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));

            await requests;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // the app stops
        }
        finally
        {
            // the runs that go on are cancelled with the stopping token; wait for them to write their state
            await Task.WhenAll(_tasks.Keys).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task FailStaleRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stale = await SyncEngine.FailStaleRunsAsync(db, System, ct);
            if (stale > 0)
            {
                logger.LogWarning("{Count} sync runs of a previous process were still running: marked as failed.", stale);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The stale sync runs could not be marked as failed.");
        }
    }

    private async Task StartDueAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var id in await SyncEngine.DueAsync(db, System, DateTime.UtcNow, ct))
            {
                Start(id, SyncTrigger.Schedule, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The due data sources could not be read.");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(ct))
        {
            if (request != SyncQueue.All)
            {
                Start(request, SyncTrigger.Manual, ct);
                continue;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var id in await SyncEngine.EnabledAsync(db, System, ct))
                {
                    Start(id, SyncTrigger.Manual, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The data sources of Sync all could not be read.");
            }
        }
    }

    /// <summary>Starts a run of the data source unless one goes on.</summary>
    private void Start(long dataSourceId, SyncTrigger trigger, CancellationToken stopping)
    {
        if (!queue.TryStart(dataSourceId, stopping, out var cancel))
        {
            if (trigger == SyncTrigger.Manual)
            {
                queue.Forget(dataSourceId);
                logger.LogInformation("Data source {Source} is running already: the request is dropped.", dataSourceId);
            }

            return;
        }

        var task = Task.Run(() => RunAsync(dataSourceId, trigger, cancel.Token), CancellationToken.None);
        _tasks.TryAdd(task, 0);
        task.ContinueWith(t => _tasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task RunAsync(long dataSourceId, SyncTrigger trigger, CancellationToken ct)
    {
        // the messages of a run are stored: in the default language of the app, whatever the language of the server is
        CultureInfo.CurrentUICulture = Languages.UiCulture(Languages.Default);
        var slot = false;
        try
        {
            await _slots.WaitAsync(ct);
            slot = true;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await SyncEngine.RunAsync(db, System, env, dataSourceId, trigger, ct);
            if (run is null)
            {
                logger.LogInformation("Data source {Source} is gone: nothing to sync.", dataSourceId);
            }
            else
            {
                logger.LogInformation("Sync of data source {Source} ({Trigger}): {State}, {Read} read, {Created} created, {Updated} updated, {Deleted} deleted, {Skipped} skipped.",
                    dataSourceId, trigger, run.State, run.RecordsRead, run.Created, run.Updated, run.Deleted, run.Skipped);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // cancelled while it waited for a slot
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The sync of data source {Source} failed.", dataSourceId);
        }
        finally
        {
            if (slot)
            {
                _slots.Release();
            }

            queue.Finish(dataSourceId);
        }
    }

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}
