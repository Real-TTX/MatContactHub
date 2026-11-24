using System.Text;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Sources;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>
/// The business layer of the data sources: administrators only, the secrets protected and never answered, an empty secret keeps the stored one, only the
/// part of the chosen kind is stored, the mapping, the preview, the runs, the delete.
/// </summary>
public sealed class SourceServiceTests
{
    private static readonly Actor Admin = Actor.System("test");

    private static async Task<(TestDatabase T, SourceTestKit Kit, long Book)> CreateAsync(Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        var t = await TestDatabase.CreateAsync();
        var book = new AddressBook { Name = "CRM", LdapName = "crm" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        return (t, new SourceTestKit(answer), book.Id);
    }

    private static ConnectionForm Rest(string url, RestAuth auth = RestAuth.None, SecretChange? secrets = null)
        => new(DataSourceKind.Rest, new SourceSettings { Rest = new RestSettings { Url = url, Auth = auth, User = "reader", ItemsPath = "items" } }, secrets ?? new SecretChange());

    [DbFact]
    public async Task Everybody_but_an_administrator_gets_nothing()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("CRM", DataSourceKind.Rest, book, true, 60, true))).Value;
        var user = await t.AddUserAsync("Stan");
        var queue = new SyncQueue();
        foreach (var actor in new[] { Actor.ForPerson(user.Id, false, user.Email), Actor.ForConsumer("phone"), Actor.Anonymous })
        {
            Assert.True((await DataSourceService.RequestSyncAsync(t.Db, actor, queue, id)).NotAllowed);
            Assert.True(DataSourceService.RequestSyncAll(actor, queue).NotAllowed);
            Assert.True((await DataSourceService.CancelSyncAsync(t.Db, actor, queue, id)).NotAllowed);
            Assert.False(queue.IsWaiting(id));
            Assert.False(queue.Reader.TryRead(out var _));
            Assert.Empty(await DataSourceService.ListAsync(t.Db, actor));
            Assert.Null(await DataSourceService.GetAsync(t.Db, actor, kit.Env, id));
            Assert.True((await DataSourceService.SaveAsync(t.Db, actor, id, new DataSourceForm("X", DataSourceKind.Rest, book, true, 60, true))).NotAllowed);
            Assert.True((await DataSourceService.SaveAsync(t.Db, actor, null, new DataSourceForm("X", DataSourceKind.Rest, book, true, 60, true))).NotAllowed);
            Assert.True((await DataSourceService.SaveConnectionAsync(t.Db, actor, kit.Env, id, Rest("https://x.test"), null)).NotAllowed);
            Assert.True((await DataSourceService.SaveMappingAsync(t.Db, actor, id, "id", [])).NotAllowed);
            Assert.Null(await DataSourceService.PreviewAsync(t.Db, actor, kit.Env, id));
            Assert.Null(await DataSourceService.RunsAsync(t.Db, actor, id, new RunCriteria(), 1, 10));
            Assert.True((await DataSourceService.DeleteAsync(t.Db, actor, kit.Env, id)).NotAllowed);
            Assert.Empty(await SyncEngine.DueAsync(t.Db, actor, DateTime.UtcNow.AddDays(1)));
        }

        Assert.Equal("CRM", (await DataSourceService.GetAsync(t.Db, Admin, kit.Env, id))!.Name);
        Assert.Single(await t.Db.DataSources.ToListAsync());
    }

    [DbFact]
    public async Task An_administrator_asks_for_a_sync_and_cancels_it()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("CRM", DataSourceKind.Rest, book, true, 60, true))).Value;
        var queue = new SyncQueue();

        Assert.True((await DataSourceService.RequestSyncAsync(t.Db, Admin, queue, id)).Ok);
        Assert.True(queue.IsWaiting(id));
        Assert.True(queue.Reader.TryRead(out var requested));
        Assert.Equal(id, requested);
        Assert.True((await DataSourceService.RequestSyncAsync(t.Db, Admin, queue, id + 99)).NotAllowed);
        Assert.True(DataSourceService.RequestSyncAll(Admin, queue).Ok);
        Assert.True(queue.Reader.TryRead(out var all));
        Assert.Equal(SyncQueue.All, all);

        Assert.False((await DataSourceService.CancelSyncAsync(t.Db, Admin, queue, id)).Value);
        Assert.True(queue.TryStart(id, CancellationToken.None, out var run));
        Assert.True((await DataSourceService.CancelSyncAsync(t.Db, Admin, queue, id)).Value);
        Assert.True(run.IsCancellationRequested);
    }

    [DbFact]
    public async Task The_general_tab_checks_its_values_and_schedules_the_next_run()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        Assert.False((await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm(" ", DataSourceKind.Rest, book, true, 60, true))).Ok);
        Assert.False((await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("A", DataSourceKind.Rest, 999, true, 60, true))).Ok);
        Assert.False((await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("A", DataSourceKind.Rest, book, true, 20000, true))).Ok);
        Assert.False((await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("A", (DataSourceKind)9, book, true, 60, true))).Ok);

        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm(" CSV import ", DataSourceKind.Csv, book, true, 30, false))).Value;
        var s = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(("CSV import", DataSourceKind.Csv, 30, false), (s.Name, s.Kind, s.IntervalMinutes, s.DeleteMissing));
        Assert.InRange(s.NextRunDate!.Value, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));

        await DataSourceService.SaveAsync(t.Db, Admin, id, new DataSourceForm("CSV import", DataSourceKind.Rest, book, true, 0, false));
        s = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Null(s.NextRunDate);
        Assert.Equal(DataSourceKind.Csv, s.Kind);
    }

    [DbFact]
    public async Task Secrets_are_protected_never_answered_and_kept_when_left_empty()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;

        Assert.False((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/c", RestAuth.Basic), null)).Ok);
        var saved = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/c", RestAuth.Basic, new SecretChange(Password: "pa55word")), null);
        Assert.True(saved.Ok, saved.Error);

        var row = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.DoesNotContain("pa55word", row.SecretProtected, StringComparison.Ordinal);
        Assert.DoesNotContain("pa55word", row.SettingsJson, StringComparison.Ordinal);
        Assert.Equal("pa55word", kit.Env.Unprotect(row.SecretProtected).Password);

        var detail = (await DataSourceService.GetAsync(t.Db, Admin, kit.Env, id))!;
        Assert.True(detail.Secrets.Password);
        Assert.False(detail.Secrets.Token);
        Assert.DoesNotContain("pa55word", System.Text.Json.JsonSerializer.Serialize(detail), StringComparison.Ordinal);

        // an empty password keeps the stored one
        Assert.True((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/v2", RestAuth.Basic), null)).Ok);
        row = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("pa55word", kit.Env.Unprotect(row.SecretProtected).Password);
        Assert.Equal("https://api.test/v2", SourceSettings.FromJson(row.SettingsJson).Rest.Url);

        // another sign-in drops the secrets it does not need
        Assert.True((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/v2", RestAuth.Bearer, new SecretChange(Token: "t0k")), null)).Ok);
        row = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        var secrets = kit.Env.Unprotect(row.SecretProtected);
        Assert.Equal(("t0k", null), (secrets.Token, secrets.Password));
        Assert.True((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/v2"), null)).Ok);
        Assert.Null((await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id)).SecretProtected);
    }

    [DbFact]
    public async Task A_password_is_stored_as_typed()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;
        Assert.True((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/c", RestAuth.Basic, new SecretChange(Password: " pa55 word ")), null)).Ok);
        var row = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(" pa55 word ", kit.Env.Unprotect(row.SecretProtected).Password);
    }

    [DbFact]
    public async Task Values_too_long_for_the_database_are_worded_errors()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;
        ConnectionForm Post(string body, SecretChange? secrets = null) => new(DataSourceKind.Rest,
            new SourceSettings { Rest = new RestSettings { Url = "https://api.test/c", Method = "POST", Body = body, Auth = RestAuth.Bearer } }, secrets ?? new SecretChange(Token: "t"));

        // past the limit of the page
        var url = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/" + new string('a', 2000)), null);
        Assert.Equal(Text.T("Sources.TooLong", Text.T("Sources.Rest.Url"), 2000), url.Error);
        var token = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Post("{}", new SecretChange(Token: new string('t', 4001))), null);
        Assert.Equal(Text.T("Sources.TooLong", Text.T("Sources.Rest.Token"), 4000), token.Error);

        // within the limits, but JSON escapes make the stored settings longer than their column
        var body = "[\"" + string.Concat(Enumerable.Repeat("\\\"", 4995)) + "\"]";
        Assert.InRange(body.Length, 9000, 10_000);
        var escaped = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Post(body), null);
        Assert.Equal(Text.T("Sources.Error.SettingsTooLarge"), escaped.Error);

        // a long body with umlauts and quotes in strings stays within the column (no \u escapes)
        var german = "{\"q\":\"" + new string('ä', 9000) + "'<>+\"}";
        var saved = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Post(german), null);
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(german, SourceSettings.FromJson((await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id)).SettingsJson).Rest.Body);
    }

    [DbFact]
    public async Task Only_the_part_of_the_chosen_kind_is_stored_and_a_query_must_only_read()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("SQL", DataSourceKind.Rest, book, true, 60, true))).Value;
        var settings = new SourceSettings
        {
            Rest = new RestSettings { Url = "https://leftover.test" },
            SqlServer = new SqlServerSettings { Query = "DELETE FROM Contacts" },
        };
        var bad = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id,
            new ConnectionForm(DataSourceKind.SqlServer, settings, new SecretChange(ConnectionString: "Server=sql.test;Database=crm;User Id=r;Password=p")), null);
        Assert.False(bad.Ok);
        Assert.False((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id,
            new ConnectionForm(DataSourceKind.SqlServer, settings with { SqlServer = new SqlServerSettings { Query = "SELECT 1" } }, new SecretChange(ConnectionString: "Nonsense=;=")), null)).Ok);

        var good = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id,
            new ConnectionForm(DataSourceKind.SqlServer, settings with { SqlServer = new SqlServerSettings { Query = "SELECT Id FROM dbo.Contacts" } },
                new SecretChange(ConnectionString: "Server=sql.test;Database=crm;User Id=r;Password=p")), null);
        Assert.True(good.Ok, good.Error);
        var row = await t.Db.DataSources.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(DataSourceKind.SqlServer, row.Kind);
        Assert.DoesNotContain("leftover", row.SettingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=p", row.SettingsJson, StringComparison.Ordinal);
        Assert.Equal("SELECT Id FROM dbo.Contacts", SourceSettings.FromJson(row.SettingsJson).SqlServer.Query);
    }

    [DbFact]
    public async Task A_csv_upload_is_kept_in_the_data_folder_and_removed_with_its_source()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("CSV", DataSourceKind.Csv, book, true, 60, true))).Value;
        var form = new ConnectionForm(DataSourceKind.Csv, new SourceSettings { Csv = new CsvSettings() }, new SecretChange());
        Assert.False((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, form, null)).Ok);
        var bytes = Encoding.UTF8.GetBytes("id;Vorname\n1;Ada\n");
        Assert.False((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, form, new UploadedFile("evil.exe", bytes.Length, () => new MemoryStream(bytes)))).Ok);
        var saved = await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, form, new UploadedFile("Kunden.csv", bytes.Length, () => new MemoryStream(bytes)));
        Assert.True(saved.Ok, saved.Error);

        var detail = (await DataSourceService.GetAsync(t.Db, Admin, kit.Env, id))!;
        Assert.Equal("Kunden.csv", detail.CsvFile!.Name);
        var path = kit.Env.CsvPath(detail.Token);
        Assert.Equal(Path.Combine(kit.Folder, "sources", detail.Token + ".csv"), path);
        Assert.True(File.Exists(path));

        // saved again without a new file: the file and its name stay
        Assert.True((await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, form, null)).Ok);
        Assert.Equal("Kunden.csv", (await DataSourceService.GetAsync(t.Db, Admin, kit.Env, id))!.CsvFile!.Name);

        var preview = (await DataSourceService.PreviewAsync(t.Db, Admin, kit.Env, id))!;
        Assert.Null(preview.Error);
        Assert.Equal(["id", "Vorname"], preview.Fields);
        Assert.Equal(["id", "Vorname"], (await DataSourceService.GetAsync(t.Db, Admin, kit.Env, id))!.Fields);

        Assert.True((await DataSourceService.DeleteAsync(t.Db, Admin, kit.Env, id)).Ok);
        Assert.False(File.Exists(path));
    }

    [DbFact]
    public async Task The_mapping_is_replaced_and_checked()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;
        Assert.False((await DataSourceService.SaveMappingAsync(t.Db, Admin, id, " ", [])).Ok);
        Assert.False((await DataSourceService.SaveMappingAsync(t.Db, Admin, id, "id", [new("Password", "x", FieldTransform.None)])).Ok);
        Assert.False((await DataSourceService.SaveMappingAsync(t.Db, Admin, id, "id", [new("FirstName", "a", FieldTransform.None), new("firstname", "b", FieldTransform.None)])).Ok);

        Assert.True((await DataSourceService.SaveMappingAsync(t.Db, Admin, id, "id",
            [new("FirstName", "first", FieldTransform.Trim), new("LastName", "", FieldTransform.None), new("PhoneMobile", "mobile", FieldTransform.Phone)])).Ok);
        Assert.True((await DataSourceService.SaveMappingAsync(t.Db, Admin, id, " uuid ",
            [new("PhoneMobile", "cell", FieldTransform.Phone), new("DisplayName", "{first} {last}", FieldTransform.None)])).Ok);
        var detail = (await DataSourceService.GetAsync(t.Db, Admin, kit.Env, id))!;
        Assert.Equal("uuid", detail.KeyField);
        Assert.Equal([("PhoneMobile", "cell"), ("DisplayName", "{first} {last}")], detail.Mappings.Select(m => (m.TargetField, m.SourceExpression)));
        Assert.Equal(2, await t.Db.FieldMappings.CountAsync());
    }

    [DbFact]
    public async Task The_preview_shows_the_records_as_they_come_and_as_contacts()
    {
        var json = """{"items":[{"id":"1","first":"Ada","last":"Lovelace","tel":"030 123456"},{"first":"No","last":"Key"}]}""";
        var (t, kit, book) = await CreateAsync(_ => FakeHandler.Json(json));
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;
        await DataSourceService.SaveConnectionAsync(t.Db, Admin, kit.Env, id, Rest("https://api.test/c"), null);
        await DataSourceService.SaveMappingAsync(t.Db, Admin, id, "id",
            [new("FirstName", "first", FieldTransform.None), new("LastName", "last", FieldTransform.None), new("PhoneBusiness", "tel", FieldTransform.Phone)]);
        var preview = (await DataSourceService.PreviewAsync(t.Db, Admin, kit.Env, id))!;
        Assert.Null(preview.Error);
        Assert.Equal(["id", "first", "last", "tel"], preview.Fields);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(("1", "Ada Lovelace", "+4930123456", null), (preview.Mapped[0].Key, preview.Mapped[0].DisplayName, preview.Mapped[0].Phone, preview.Mapped[0].Problem));
        Assert.Equal(Text.T("Sources.Preview.NoKey"), preview.Mapped[1].Problem);
        Assert.Empty(await t.Db.Contacts.ToListAsync());

        // a failing source is the error of the preview, not an exception
        var failing = new SourceTestKit(_ => FakeHandler.Json("nope", System.Net.HttpStatusCode.NotFound));
        using (failing)
        {
            var error = (await DataSourceService.PreviewAsync(t.Db, Admin, failing.Env, id))!;
            Assert.Contains("404", error.Error, StringComparison.Ordinal);
            Assert.Empty(error.Rows);
        }
    }

    [DbFact]
    public async Task The_runs_are_listed_newest_first_with_their_tools()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;
        var start = DateTime.UtcNow.AddHours(-3);
        t.Db.SyncRuns.AddRange(
            new SyncRun { DataSourceId = id, State = SyncState.Succeeded, Trigger = SyncTrigger.Schedule, StartDate = start, EndDate = start.AddSeconds(5) },
            new SyncRun { DataSourceId = id, State = SyncState.Failed, Trigger = SyncTrigger.Manual, StartDate = start.AddHours(1), EndDate = start.AddHours(1), Message = "The server answered 502" },
            new SyncRun { DataSourceId = id, State = SyncState.Succeeded, Trigger = SyncTrigger.Manual, StartDate = start.AddHours(2), EndDate = start.AddHours(2) });
        await t.Db.SaveChangesAsync();

        var (rows, total, matching) = (await DataSourceService.RunsAsync(t.Db, Admin, id, new RunCriteria(), 1, 2))!.Value;
        Assert.Equal((3, 3, 2), (total, matching, rows.Count));
        Assert.True(rows[0].StartDate > rows[1].StartDate);
        Assert.Equal(1, (await DataSourceService.RunsAsync(t.Db, Admin, id, new RunCriteria(State: SyncState.Failed), 1, 10))!.Value.Matching);
        Assert.Equal(2, (await DataSourceService.RunsAsync(t.Db, Admin, id, new RunCriteria(Trigger: SyncTrigger.Manual), 1, 10))!.Value.Matching);
        Assert.Equal(1, (await DataSourceService.RunsAsync(t.Db, Admin, id, new RunCriteria(Search: "502"), 1, 10))!.Value.Matching);

        var runId = (await t.Db.SyncRuns.FirstAsync(r => r.State == SyncState.Failed)).Id;
        var detail = (await DataSourceService.GetRunAsync(t.Db, Admin, runId))!;
        Assert.Equal(("API", "The server answered 502"), (detail.DataSource, detail.Message));
        Assert.Null(await DataSourceService.GetRunAsync(t.Db, Actor.Anonymous, runId));

        await DataSourceService.DeleteAsync(t.Db, Admin, kit.Env, id);
        Assert.Null(await DataSourceService.GetRunAsync(t.Db, Admin, runId));
    }

    [DbFact]
    public async Task Deleting_a_source_deletes_its_contacts_softly_and_leaves_the_others()
    {
        var (t, kit, book) = await CreateAsync();
        await using var _ = t;
        using var __ = kit;
        var id = (await DataSourceService.SaveAsync(t.Db, Admin, null, new DataSourceForm("API", DataSourceKind.Rest, book, true, 60, true))).Value;
        t.Db.Contacts.AddRange(
            new Contact { AddressBookId = book, DataSourceId = id, ExternalKey = "1", DisplayName = "Synced" },
            new Contact { AddressBookId = book, DisplayName = "Manual" });
        await t.Db.SaveChangesAsync();
        Assert.True((await DataSourceService.DeleteAsync(t.Db, Admin, kit.Env, id)).Ok);
        t.Db.ChangeTracker.Clear();
        Assert.Equal(["Manual"], await t.Db.Contacts.Select(c => c.DisplayName).ToListAsync());
        Assert.Equal(2, await t.Db.Contacts.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await DataSourceService.ListAsync(t.Db, Admin));
    }
}
