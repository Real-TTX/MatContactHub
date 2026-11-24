using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Sources;

namespace MatContactHub.Tests;

/// <summary>When a data source is due, when it runs next, and that one source runs once at a time (the queue of the scheduler).</summary>
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
