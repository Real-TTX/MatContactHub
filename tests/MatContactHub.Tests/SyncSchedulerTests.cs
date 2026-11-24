using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatContactHub.Tests;

/// <summary>
/// When a data source is due, when it runs next, that one source runs once at a time (the queue of the scheduler), and the scheduler itself: the stale runs fail
/// at its start, a request runs once, Sync all runs every enabled source and at most <see cref="SyncScheduler.MaxParallel"/> at once.
/// </summary>
public sealed class SyncSchedulerTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, 60, -1, true)]
    [InlineData(true, 60, 0, true)]
    [InlineData(true, 60, 1, false)]
    [InlineData(false, 60, -1, false)]
    [InlineData(true, 0, -1, false)]
    public void A_source_is_due_when_it_is_on_has_an_interval_and_its_time_has_come(bool enabled, int interval, int minutes, bool due)
    {
        var source = new DataSource { Enabled = enabled, IntervalMinutes = interval, NextRunDate = Now.AddMinutes(minutes) };
        Assert.Equal(due, SyncEngine.IsDue(source, Now));
    }

    [Fact]
    public void A_source_without_a_next_run_is_not_due()
        => Assert.False(SyncEngine.IsDue(new DataSource { Enabled = true, IntervalMinutes = 60, NextRunDate = null }, Now));

    [Fact]
    public void The_next_run_follows_the_last_one_but_never_lies_in_the_past()
    {
        Assert.Equal(Now.AddMinutes(60), DataSourceService.NextRun(null, 60, Now));
        Assert.Equal(Now.AddMinutes(20), DataSourceService.NextRun(Now.AddMinutes(-40), 60, Now));
        Assert.Equal(Now, DataSourceService.NextRun(Now.AddDays(-2), 60, Now));
    }

    [DbFact]
    public async Task The_due_sources_are_found_in_the_database()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = new AddressBook { Name = "B", LdapName = "b" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        DataSource Source(string name, bool enabled, int interval, DateTime? next) => new() { Name = name, AddressBookId = book.Id, Enabled = enabled, IntervalMinutes = interval, NextRunDate = next };
        var due = Source("due", true, 60, now.AddMinutes(-5));
        t.Db.DataSources.AddRange(due, Source("later", true, 60, now.AddMinutes(5)), Source("off", false, 60, now.AddMinutes(-5)), Source("manual", true, 0, now.AddMinutes(-5)),
            Source("never", true, 60, null));
        var deleted = Source("deleted", true, 60, now.AddMinutes(-5));
        t.Db.DataSources.Add(deleted);
        await t.Db.SaveChangesAsync();
        t.Db.DataSources.Remove(deleted);
        await t.Db.SaveChangesAsync();

        Assert.Equal([due.Id], await SyncEngine.DueAsync(t.Db, Actor.System("test"), now));
        Assert.Empty(await SyncEngine.DueAsync(t.Db, Actor.Anonymous, now));
        Assert.Equal(4, (await SyncEngine.EnabledAsync(t.Db, Actor.System("test"))).Count);
    }

    [Fact]
    public void One_source_runs_once_at_a_time()
    {
        var queue = new SyncQueue();
        Assert.True(queue.TryStart(7, CancellationToken.None, out var first));
        Assert.True(queue.IsRunning(7));
        Assert.False(queue.TryStart(7, CancellationToken.None, out _));
        Assert.True(queue.TryStart(8, CancellationToken.None, out _));
        queue.Finish(7);
        Assert.False(queue.IsRunning(7));
        Assert.True(queue.TryStart(7, CancellationToken.None, out var second));
        Assert.NotSame(first, second);
    }

    [Fact]
    public void A_request_waits_until_its_run_starts()
    {
        var queue = new SyncQueue();
        queue.Request(3);
        Assert.True(queue.IsWaiting(3));
        Assert.True(queue.Reader.TryRead(out var id));
        Assert.Equal(3, id);
        Assert.True(queue.TryStart(3, CancellationToken.None, out _));
        Assert.False(queue.IsWaiting(3));
        queue.RequestAll();
        Assert.True(queue.Reader.TryRead(out var all));
        Assert.Equal(SyncQueue.All, all);
    }

    /// <summary>A REST server whose answers wait until the test opens the gate; it counts the requests that wait at the same time.</summary>
    private sealed class GateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private int _waiting;
        private int _requests;
        private int _mostAtOnce;

        public int Requests => Volatile.Read(ref _requests);

        public int MostAtOnce
        {
            get
            {
                lock (_lock)
                {
                    return _mostAtOnce;
                }
            }
        }

        public int Waiting => Volatile.Read(ref _waiting);

        public void Open() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var now = Interlocked.Increment(ref _waiting);
            lock (_lock)
            {
                _mostAtOnce = Math.Max(_mostAtOnce, now);
            }

            try
            {
                await _gate.Task.WaitAsync(cancellationToken);
                return FakeHandler.Json("[]");
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }
    }

    /// <summary>The scheduler as the app runs it: its own scopes on a test database, the sources answered by <see cref="GateHandler"/>.</summary>
    private sealed class SchedulerWorld : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public SchedulerWorld(TestDatabase t)
        {
            T = t;
            var connection = t.Db.Database.GetConnectionString()!;
            _services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseNpgsql(connection)).BuildServiceProvider();
            Folder = Path.Combine(Path.GetTempPath(), "mch-scheduler-" + Guid.NewGuid().ToString("N")[..10]);
            var env = new SourceEnvironment(new EphemeralDataProtectionProvider(), Folder, _ => new HttpClient(Handler, disposeHandler: false));
            Scheduler = new SyncScheduler(Queue, env, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SyncScheduler>.Instance);
        }

        public TestDatabase T { get; }

        public string Folder { get; }

        public GateHandler Handler { get; } = new();

        public SyncQueue Queue { get; } = new();

        public SyncScheduler Scheduler { get; }

        public async Task<long> AddSourceAsync(string name)
        {
            var book = await T.Db.AddressBooks.FirstOrDefaultAsync() ?? new AddressBook { Name = "B", LdapName = "b" };
            var source = new DataSource
            {
                Name = name,
                AddressBook = book,
                Kind = DataSourceKind.Rest,
                IntervalMinutes = 0,
                KeyField = "id",
                SettingsJson = new SourceSettings { Rest = new RestSettings { Url = "https://crm.example.com/" + name } }.ToJson(DataSourceKind.Rest),
            };
            T.Db.DataSources.Add(source);
            await T.Db.SaveChangesAsync();
            return source.Id;
        }

        public async Task<List<SyncRun>> RunsAsync() => await T.Db.SyncRuns.AsNoTracking().OrderBy(r => r.Id).ToListAsync();

        public async ValueTask DisposeAsync()
        {
            Handler.Open();
            await Scheduler.StopAsync(CancellationToken.None);
            Scheduler.Dispose();
            await _services.DisposeAsync();
            Handler.Dispose();
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // nothing was written
            }
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds (the scheduler works in the background), at most 20 seconds.</summary>
    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < end, "Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }

    [DbFact]
    public async Task The_scheduler_fails_stale_runs_at_its_start_and_runs_a_request_once()
    {
        await using var t = await TestDatabase.CreateAsync();
        await using var w = new SchedulerWorld(t);
        var id = await w.AddSourceAsync("crm");
        t.Db.SyncRuns.Add(new SyncRun { DataSourceId = id, State = SyncState.Running, StartDate = DateTime.UtcNow.AddHours(-1) });
        await t.Db.SaveChangesAsync();

        await w.Scheduler.StartAsync(CancellationToken.None);
        await UntilAsync(async () => (await w.RunsAsync()).Single().State == SyncState.Failed, "the stale run failed");

        // asked for twice while it runs: one run
        w.Queue.Request(id);
        await UntilAsync(() => Task.FromResult(w.Handler.Waiting == 1), "the run reads the source");
        Assert.True(w.Queue.IsRunning(id));
        w.Queue.Request(id);
        await UntilAsync(() => Task.FromResult(!w.Queue.IsWaiting(id)), "the second request is dropped");
        w.Handler.Open();
        await UntilAsync(() => Task.FromResult(!w.Queue.IsRunning(id)), "the run ended");
        var runs = await w.RunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal((SyncState.Succeeded, SyncTrigger.Manual), (runs[1].State, runs[1].Trigger));
        Assert.Equal(1, w.Handler.Requests);
    }

    [DbFact]
    public async Task Sync_all_runs_every_enabled_source_at_most_three_at_a_time()
    {
        await using var t = await TestDatabase.CreateAsync();
        await using var w = new SchedulerWorld(t);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await w.AddSourceAsync("s" + i));
        }

        var off = await w.AddSourceAsync("off");
        await t.Db.DataSources.Where(s => s.Id == off).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));

        await w.Scheduler.StartAsync(CancellationToken.None);
        w.Queue.RequestAll();
        await UntilAsync(() => Task.FromResult(w.Handler.Waiting == SyncScheduler.MaxParallel), "three runs read their source");
        await Task.Delay(300);
        Assert.Equal(SyncScheduler.MaxParallel, w.Handler.Waiting);
        Assert.Equal(5, ids.Count(w.Queue.IsRunning));

        w.Handler.Open();
        await UntilAsync(async () => (await w.RunsAsync()).Count(r => r.State == SyncState.Succeeded) == 5, "every enabled source ran");
        Assert.Equal(SyncScheduler.MaxParallel, w.Handler.MostAtOnce);
        Assert.DoesNotContain(await w.RunsAsync(), r => r.DataSourceId == off);
    }

    [Fact]
    public void Cancel_stops_the_run_of_a_source_and_the_app_stopping_too()
    {
        var queue = new SyncQueue();
        Assert.False(queue.Cancel(1));
        using var stopping = new CancellationTokenSource();
        Assert.True(queue.TryStart(1, stopping.Token, out var run));
        Assert.True(queue.Cancel(1));
        Assert.True(run.IsCancellationRequested);

        Assert.True(queue.TryStart(2, stopping.Token, out var other));
        stopping.Cancel();
        Assert.True(other.IsCancellationRequested);
    }
}
