using System.Net;
using System.Text.Json;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Sources;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>
/// The sync engine against PostgreSQL, fed by a REST source on a fake server: create, update, unchanged, delete the missing ones, revive a deleted one, an error
/// deletes nothing, Hidden and the manual contacts stay untouched, records without a key and keys that come twice.
/// </summary>
public sealed class SyncEngineTests
{
    private static readonly Actor Admin = Actor.System("test");

    /// <summary>A world: a database, an address book, a manual contact, a REST data source whose answer the test sets.</summary>
    private sealed class World : IAsyncDisposable
    {
        public required TestDatabase T { get; init; }

        public required SourceTestKit Kit { get; init; }

        public required DataSource Source { get; init; }

        public required AddressBook Book { get; init; }

        public required Contact Manual { get; init; }

        /// <summary>The records the server answers (null: it answers 500).</summary>
        public List<Dictionary<string, object?>>? Records { get; set; } = [];

        public AppDbContext Db => T.Db;

        public async Task<SyncRun> SyncAsync()
        {
            T.Db.ChangeTracker.Clear();
            var run = await SyncEngine.RunAsync(T.Db, Admin, Kit.Env, Source.Id, SyncTrigger.Manual);
            T.Db.ChangeTracker.Clear();
            return run!;
        }

        public Task<List<Contact>> SyncedAsync(bool withDeleted = false)
            => (withDeleted ? Db.Contacts.IgnoreQueryFilters() : Db.Contacts).Where(c => c.DataSourceId == Source.Id).OrderBy(c => c.ExternalKey).AsNoTracking().ToListAsync();

        public async ValueTask DisposeAsync()
        {
            Kit.Dispose();
            await T.DisposeAsync();
        }
    }

    private static async Task<World> CreateAsync(bool deleteMissing = true)
    {
        var t = await TestDatabase.CreateAsync();
        World? world = null;
        var kit = new SourceTestKit(_ => world!.Records is { } records
            ? FakeHandler.Json(JsonSerializer.Serialize(new { items = records }))
            : FakeHandler.Json("""{"error":"down"}""", HttpStatusCode.InternalServerError));
        var book = new AddressBook { Name = "CRM", LdapName = "crm" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        var manual = new Contact { AddressBookId = book.Id, DisplayName = "Manual Person", PhoneBusiness = "+491" };
        var source = new DataSource
        {
            Name = "CRM API",
            Kind = DataSourceKind.Rest,
            AddressBookId = book.Id,
            KeyField = "id",
            DeleteMissing = deleteMissing,
            IntervalMinutes = 60,
            SettingsJson = new SourceSettings { Rest = new RestSettings { Url = "https://crm.example.com/api/contacts", ItemsPath = "items" } }.ToJson(DataSourceKind.Rest),
            Mappings =
            [
                new FieldMapping { TargetField = "FirstName", SourceExpression = "first", SortOrder = 0 },
                new FieldMapping { TargetField = "LastName", SourceExpression = "last", SortOrder = 1 },
                new FieldMapping { TargetField = "Company", SourceExpression = "company.name", SortOrder = 2 },
                new FieldMapping { TargetField = "PhoneBusiness", SourceExpression = "phone", Transform = FieldTransform.Phone, SortOrder = 3 },
            ],
        };
        t.Db.Contacts.Add(manual);
        t.Db.DataSources.Add(source);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();
        world = new World { T = t, Kit = kit, Source = source, Book = book, Manual = manual };
        return world;
    }

    private static Dictionary<string, object?> Person(string id, string first, string last, string? phone = null, string? company = null)
        => new() { ["id"] = id, ["first"] = first, ["last"] = last, ["phone"] = phone, ["company"] = company is null ? null : new { name = company } };

    [DbFact]
    public async Task The_first_run_creates_the_contacts()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace", "030 1234567", "Analytical Engines"), Person("2", "Grace", "Hopper"), Person("3", "Alan", "Turing")];
        var version = DirectoryChanges.Version;
        var run = await w.SyncAsync();
        Assert.Equal(SyncState.Succeeded, run.State);
        Assert.Equal((3, 3, 0, 0, 0, 0), (run.RecordsRead, run.Created, run.Updated, run.Unchanged, run.Deleted, run.Skipped));
        Assert.NotNull(run.EndDate);
        var contacts = await w.SyncedAsync();
        Assert.Equal(3, contacts.Count);
        var ada = contacts[0];
        Assert.Equal(("1", "Ada", "Lovelace", "Ada Lovelace", "Analytical Engines", "+49301234567"),
            (ada.ExternalKey, ada.FirstName, ada.LastName, ada.DisplayName, ada.Company, ada.PhoneBusiness));
        Assert.Equal(w.Book.Id, ada.AddressBookId);
        Assert.NotNull(ada.SyncHash);
        Assert.NotNull(ada.LastSyncDate);
        Assert.True(DirectoryChanges.Version > version);

        var source = await w.Db.DataSources.AsNoTracking().SingleAsync(s => s.Id == w.Source.Id);
        Assert.Equal(SyncState.Succeeded, source.LastRunState);
        Assert.NotNull(source.LastRunDate);
        Assert.Null(source.LastError);
        Assert.True(source.NextRunDate > DateTime.UtcNow.AddMinutes(55));
        Assert.Contains("company.name", SyncEngine.FieldsOf(source.DiscoveredFields));
        Assert.Equal(SyncState.Succeeded, (await w.Db.SyncRuns.AsNoTracking().SingleAsync()).State);
    }

    [DbFact]
    public async Task A_second_run_updates_what_changed_and_skips_the_rest()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();
        var before = await w.SyncedAsync();
        w.Records = [Person("1", "Ada", "King"), Person("2", "Grace", "Hopper")];
        var run = await w.SyncAsync();
        Assert.Equal((2, 0, 1, 1, 0), (run.RecordsRead, run.Created, run.Updated, run.Unchanged, run.Deleted));
        var after = await w.SyncedAsync();
        Assert.Equal("King", after[0].LastName);
        Assert.Equal("Ada King", after[0].DisplayName);
        Assert.Equal(before[0].Token, after[0].Token);
        Assert.Equal(before[1].UpdateDate, after[1].UpdateDate);

        var third = await w.SyncAsync();
        Assert.Equal((0, 0, 2), (third.Created, third.Updated, third.Unchanged));
    }

    [DbFact]
    public async Task A_changed_mapping_rewrites_the_contacts()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        await w.SyncAsync();
        var mapping = await w.Db.FieldMappings.SingleAsync(m => m.TargetField == "LastName");
        mapping.Transform = FieldTransform.Upper;
        await w.Db.SaveChangesAsync();
        var run = await w.SyncAsync();
        Assert.Equal(1, run.Updated);
        Assert.Equal("LOVELACE", (await w.SyncedAsync())[0].LastName);
    }

    [DbFact]
    public async Task Contacts_gone_from_the_source_are_deleted_softly_and_come_back_as_the_same_row()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();
        var grace = (await w.SyncedAsync())[1];

        // Hidden is the person's choice: the sync never touches it, also not when the contact comes back
        await w.Db.Contacts.Where(c => c.Id == grace.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Hidden, true));

        w.Records = [Person("1", "Ada", "Lovelace")];
        var version = DirectoryChanges.Version;
        var run = await w.SyncAsync();
        Assert.Equal((1, 1), (run.Unchanged, run.Deleted));
        Assert.True(DirectoryChanges.Version > version);
        Assert.Single(await w.SyncedAsync());
        var deleted = (await w.SyncedAsync(withDeleted: true)).Single(c => c.ExternalKey == "2");
        Assert.Equal(SoftEntity.Deleted, deleted.UpdateState);

        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper-Murray")];
        var back = await w.SyncAsync();
        Assert.Equal((1, 0, 1), (back.Created, back.Updated, back.Unchanged));
        Assert.Contains(Text.T("Sources.Run.Revived", 1, "2"), back.Message ?? string.Empty, StringComparison.Ordinal);
        var revived = (await w.SyncedAsync()).Single(c => c.ExternalKey == "2");
        Assert.Equal((grace.Id, grace.Token), (revived.Id, revived.Token));
        Assert.Equal("Hopper-Murray", revived.LastName);
        Assert.NotEqual(SoftEntity.Deleted, revived.UpdateState);
        Assert.True(revived.Hidden);
    }

    [DbFact]
    public async Task A_record_that_comes_back_unchanged_is_revived_too()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        await w.SyncAsync();
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        var run = await w.SyncAsync();
        Assert.Equal(1, run.Created);
        Assert.Equal(2, (await w.SyncedAsync()).Count);
    }

    [DbFact]
    public async Task An_error_deletes_nothing()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();
        w.Records = null;
        var run = await w.SyncAsync();
        Assert.Equal(SyncState.Failed, run.State);
        Assert.Equal(0, run.Deleted);
        Assert.Contains("500", run.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await w.SyncedAsync()).Count);
        var source = await w.Db.DataSources.AsNoTracking().SingleAsync(s => s.Id == w.Source.Id);
        Assert.Equal(SyncState.Failed, source.LastRunState);
        Assert.Contains("500", source.LastError, StringComparison.Ordinal);
        Assert.NotNull(source.NextRunDate);
    }

    [DbFact]
    public async Task An_error_on_a_later_page_keeps_what_was_read_but_deletes_nothing()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();

        // page 1 answers one changed record, page 2 fails
        var settings = new SourceSettings
        {
            Rest = new RestSettings { Url = "https://crm.example.com/api/contacts", ItemsPath = "items", Pagination = RestPagination.Page, PageParameter = "page" },
        };
        await w.Db.DataSources.Where(s => s.Id == w.Source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingsJson, settings.ToJson(DataSourceKind.Rest)));
        var kit = new SourceTestKit(request => request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal)
            ? FakeHandler.Json(JsonSerializer.Serialize(new { items = new[] { Person("1", "Ada", "Byron") } }))
            : FakeHandler.Json("{}", HttpStatusCode.BadGateway));
        using (kit)
        {
            var run = await SyncEngine.RunAsync(w.Db, Admin, kit.Env, w.Source.Id, SyncTrigger.Schedule);
            w.Db.ChangeTracker.Clear();
            Assert.Equal(SyncState.Failed, run!.State);
            Assert.Equal((1, 1, 0), (run.RecordsRead, run.Updated, run.Deleted));
        }

        var contacts = await w.SyncedAsync();
        Assert.Equal(2, contacts.Count);
        Assert.Equal("Byron", contacts[0].LastName);
    }

    [DbFact]
    public async Task An_empty_answer_deletes_nothing()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        await w.SyncAsync();
        w.Records = [];
        var run = await w.SyncAsync();
        Assert.Equal(SyncState.Succeeded, run.State);
        Assert.Equal(0, run.Deleted);
        Assert.Contains(Text.T("Sources.Run.NoDeleteEmpty", 1), run.Message, StringComparison.Ordinal);
        Assert.Single(await w.SyncedAsync());
    }

    [DbFact]
    public async Task Without_delete_missing_nothing_is_deleted()
    {
        await using var w = await CreateAsync(deleteMissing: false);
        w.Records = [Person("1", "Ada", "Lovelace"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        var run = await w.SyncAsync();
        Assert.Equal(0, run.Deleted);
        Assert.Equal(2, (await w.SyncedAsync()).Count);
    }

    [DbFact]
    public async Task Hidden_and_the_manual_contacts_stay_untouched()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        await w.SyncAsync();
        var ada = (await w.SyncedAsync())[0];
        await w.Db.Contacts.Where(c => c.Id == ada.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Hidden, true));
        var manualBefore = await w.Db.Contacts.AsNoTracking().SingleAsync(c => c.Id == w.Manual.Id);
        w.Records = [Person("1", "Ada", "King"), Person("2", "Grace", "Hopper")];
        await w.SyncAsync();
        var after = await w.Db.Contacts.AsNoTracking().SingleAsync(c => c.Id == ada.Id);
        Assert.True(after.Hidden);
        Assert.Equal("King", after.LastName);
        var manual = await w.Db.Contacts.AsNoTracking().SingleAsync(c => c.Id == w.Manual.Id);
        Assert.Equal(("Manual Person", "+491", SoftEntity.Created), (manual.DisplayName, manual.PhoneBusiness, manual.UpdateState));
        Assert.Equal(manualBefore.UpdateDate, manual.UpdateDate);
    }

    [DbFact]
    public async Task Records_without_a_key_are_skipped_and_the_last_of_a_twice_key_wins()
    {
        await using var w = await CreateAsync();
        w.Records =
        [
            Person("1", "Ada", "Lovelace"),
            new() { ["first"] = "No", ["last"] = "Key" },
            Person("", "Empty", "Key"),
            Person("1", "Ada", "Byron"),
        ];
        var run = await w.SyncAsync();
        Assert.Equal(SyncState.Succeeded, run.State);
        Assert.Equal((4, 1, 2), (run.RecordsRead, run.Created, run.Skipped));
        Assert.Contains(Text.T("Sources.Run.NoKey", 2, "2, 3"), run.Message, StringComparison.Ordinal);
        Assert.Contains(Text.T("Sources.Run.Duplicate", 1, "1"), run.Message, StringComparison.Ordinal);
        Assert.Equal("Byron", Assert.Single(await w.SyncedAsync()).LastName);
    }

    [DbFact]
    public async Task A_new_address_book_moves_the_contacts()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        await w.SyncAsync();
        var other = new AddressBook { Name = "Partners", LdapName = "partners" };
        w.Db.AddressBooks.Add(other);
        await w.Db.SaveChangesAsync();
        await w.Db.DataSources.Where(s => s.Id == w.Source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AddressBookId, other.Id));
        var run = await w.SyncAsync();
        Assert.Equal(1, run.Updated);
        Assert.Equal(other.Id, (await w.SyncedAsync())[0].AddressBookId);
    }

    [DbFact]
    public async Task Without_a_key_field_the_run_fails()
    {
        await using var w = await CreateAsync();
        await w.Db.DataSources.Where(s => s.Id == w.Source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.KeyField, string.Empty));
        w.Records = [Person("1", "Ada", "Lovelace")];
        var run = await w.SyncAsync();
        Assert.Equal(SyncState.Failed, run.State);
        Assert.Empty(await w.SyncedAsync());
    }

    [DbFact]
    public async Task A_cancelled_run_writes_nothing_and_says_so()
    {
        await using var w = await CreateAsync();
        w.Records = [Person("1", "Ada", "Lovelace")];
        using var cancel = new CancellationTokenSource();
        var kit = new SourceTestKit(_ =>
        {
            cancel.Cancel();
            return FakeHandler.Json(JsonSerializer.Serialize(new { items = w.Records }));
        });
        using (kit)
        {
            var run = await SyncEngine.RunAsync(w.Db, Admin, kit.Env, w.Source.Id, SyncTrigger.Manual, cancel.Token);
            Assert.Equal(SyncState.Cancelled, run!.State);
        }

        w.Db.ChangeTracker.Clear();
        Assert.Empty(await w.SyncedAsync());
        Assert.Equal(SyncState.Cancelled, (await w.Db.DataSources.AsNoTracking().SingleAsync(s => s.Id == w.Source.Id)).LastRunState);
    }

    [DbFact]
    public async Task Only_an_administrator_may_run_a_sync()
    {
        await using var w = await CreateAsync();
        var user = await w.T.AddUserAsync("Stan");
        Assert.Null(await SyncEngine.RunAsync(w.Db, Actor.ForPerson(user.Id, false, user.Email), w.Kit.Env, w.Source.Id, SyncTrigger.Manual));
        Assert.Null(await SyncEngine.RunAsync(w.Db, Actor.ForConsumer("phone"), w.Kit.Env, w.Source.Id, SyncTrigger.Manual));
        Assert.Empty(await w.Db.SyncRuns.ToListAsync());
    }

    [DbFact]
    public async Task Stale_running_runs_are_marked_failed()
    {
        await using var w = await CreateAsync();
        w.Db.SyncRuns.Add(new SyncRun { DataSourceId = w.Source.Id, State = SyncState.Running, StartDate = DateTime.UtcNow.AddHours(-1) });
        await w.Db.DataSources.Where(s => s.Id == w.Source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunState, SyncState.Running));
        await w.Db.SaveChangesAsync();
        Assert.Equal(1, await SyncEngine.FailStaleRunsAsync(w.Db, Admin));
        var run = await w.Db.SyncRuns.AsNoTracking().SingleAsync();
        Assert.Equal(SyncState.Failed, run.State);
        Assert.NotNull(run.EndDate);
        Assert.Equal(SyncState.Failed, (await w.Db.DataSources.AsNoTracking().SingleAsync()).LastRunState);
    }

    [DbFact]
    public async Task A_csv_source_syncs_end_to_end()
    {
        await using var w = await CreateAsync();
        var token = (await w.Db.DataSources.AsNoTracking().SingleAsync()).Token;
        Directory.CreateDirectory(w.Kit.Env.SourcesDirectory);
        var cp1252 = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        await File.WriteAllBytesAsync(w.Kit.Env.CsvPath(token), cp1252.GetBytes("Kundennummer;Vorname;Nachname;Telefon\r\n100;Jürgen;Müller;089 123456\r\n101;Änne;Groß;\r\n"));
        var settings = new SourceSettings { Csv = new CsvSettings { Encoding = CsvEncoding.Windows1252 } };
        await w.Db.DataSources.Where(s => s.Id == w.Source.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Kind, DataSourceKind.Csv).SetProperty(x => x.SettingsJson, settings.ToJson(DataSourceKind.Csv)).SetProperty(x => x.KeyField, "Kundennummer"));
        await w.Db.FieldMappings.Where(m => m.DataSourceId == w.Source.Id).ExecuteDeleteAsync();
        var rules = FieldMapper.AutoMap(["Kundennummer", "Vorname", "Nachname", "Telefon"], []);
        w.Db.FieldMappings.AddRange(rules.Select((r, i) => new FieldMapping
        {
            DataSourceId = w.Source.Id, TargetField = r.TargetField, SourceExpression = r.SourceExpression, Transform = r.Transform, SortOrder = i,
        }));
        await w.Db.SaveChangesAsync();
        var run = await w.SyncAsync();
        Assert.Equal((SyncState.Succeeded, 2), (run.State, run.Created));
        var contacts = await w.SyncedAsync();
        Assert.Equal(("100", "Jürgen Müller", "+4989123456", "100"), (contacts[0].ExternalKey, contacts[0].DisplayName, contacts[0].PhoneBusiness, contacts[0].CustomerNumber));
        Assert.Equal("Änne Groß", contacts[1].DisplayName);
    }
}
