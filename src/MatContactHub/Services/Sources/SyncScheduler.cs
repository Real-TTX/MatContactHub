using System.Threading.Channels;

namespace MatContactHub.Services.Sources;

/// <summary>
/// The queue of "Sync now" (the page of a data source, the platform action <c>syncAll</c>, the quick action of the launcher). A singleton; the
/// <see cref="SyncScheduler"/> takes the requests. Module B owns it.
/// </summary>
public sealed class SyncQueue
{
    /// <summary>The id that stands for "every enabled data source".</summary>
    public const long All = 0;

    private readonly Channel<long> _requests = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Asks for a sync of one data source as soon as possible.</summary>
    public void Request(long dataSourceId) => _requests.Writer.TryWrite(dataSourceId);

    /// <summary>Asks for a sync of every enabled data source.</summary>
    public void RequestAll() => _requests.Writer.TryWrite(All);

    /// <summary>The requests (read by <see cref="SyncScheduler"/> only).</summary>
    public ChannelReader<long> Reader => _requests.Reader;
}

/// <summary>
/// Runs the syncs in the background (module B owns it; FOUNDATION STUB: idles and only drains the queue). The real scheduler starts the data sources that are due
/// (<c>NextRunDate</c>), one run per source at a time, and the requests of <see cref="SyncQueue"/>.
/// </summary>
public sealed class SyncScheduler(SyncQueue queue, ILogger<SyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("The sync scheduler is not implemented yet: data sources are not synchronised.");
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                logger.LogInformation("Sync requested for {Source} (not implemented yet).", request == SyncQueue.All ? "every data source" : $"data source {request}");
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }
}
