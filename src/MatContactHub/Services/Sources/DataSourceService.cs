using System.Globalization;
using System.Text.Json;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services.Contacts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Sources;

/// <summary>A line of the list of data sources.</summary>
public sealed record DataSourceRow(long Id, string Name, DataSourceKind Kind, string AddressBook, bool Enabled, int IntervalMinutes, DateTime? LastRunDate,
    SyncState? LastRunState, DateTime? NextRunDate, int Contacts, string? LastError);

/// <summary>The tab General of a data source (what the page sends). The kind is chosen here for a new source and on the tab Connection afterwards.</summary>
public sealed record DataSourceForm(string Name, DataSourceKind Kind, long AddressBookId, bool Enabled, int IntervalMinutes, bool DeleteMissing);

/// <summary>New secrets from the page: an empty value keeps the stored one (the page never shows a stored secret).</summary>
public sealed record SecretChange(string? Password = null, string? Token = null, string? ApiKey = null, string? ConnectionString = null);

/// <summary>The tab Connection: the kind, its settings and new secrets.</summary>
public sealed record ConnectionForm(DataSourceKind Kind, SourceSettings Settings, SecretChange Secrets);

/// <summary>A CSV file uploaded on the tab Connection.</summary>
public sealed record UploadedFile(string FileName, long Length, Func<Stream> Open);

/// <summary>Which secrets are stored (never their values).</summary>
public sealed record SecretState(bool Password, bool Token, bool ApiKey, bool ConnectionString);

/// <summary>The uploaded CSV file of a data source.</summary>
public sealed record CsvFileInfo(string Name, long Size, DateTime ModifiedUtc);

/// <summary>A data source as its edit page shows it: no secret, only whether it is stored.</summary>
public sealed record DataSourceDetail(long Id, Guid Token, string Name, DataSourceKind Kind, long AddressBookId, string AddressBook, bool Enabled, int IntervalMinutes,
    bool DeleteMissing, string KeyField, SourceSettings Settings, SecretState Secrets, IReadOnlyList<MappingRule> Mappings, IReadOnlyList<string> Fields,
    DateTime? LastRunDate, SyncState? LastRunState, DateTime? NextRunDate, string? LastError, int Contacts, CsvFileInfo? CsvFile);

/// <summary>A record of the preview as a contact: its key, its name and a few fields, or why it would be skipped.</summary>
public sealed record MappedPreview(string? Key, string DisplayName, string? Company, string? Email, string? Phone, string? Problem);

/// <summary>The preview of a source: the fields found, the first records as they come and as contacts, or the error.</summary>
public sealed record SourcePreview(IReadOnlyList<string> Fields, IReadOnlyList<SourceRecord> Rows, IReadOnlyList<MappedPreview> Mapped, string? Error);

/// <summary>The tools of the list of runs: a text of the message, the state, the trigger.</summary>
public sealed record RunCriteria(string? Search = null, SyncState? State = null, SyncTrigger? Trigger = null);

/// <summary>A line of the runs of a data source.</summary>
public sealed record SyncRunRow(long Id, SyncTrigger Trigger, SyncState State, DateTime StartDate, DateTime? EndDate, int RecordsRead, int Created, int Updated,
    int Unchanged, int Deleted, int Skipped, string? Message);

/// <summary>A run with its numbers and its message.</summary>
public sealed record SyncRunDetail(long Id, Guid Token, long DataSourceId, string DataSource, SyncTrigger Trigger, SyncState State, DateTime StartDate, DateTime? EndDate,
    int RecordsRead, int Created, int Updated, int Unchanged, int Deleted, int Skipped, string? Message);

/// <summary>
/// The data sources (module B): the list, the tabs of the edit page (General, Connection with the secrets protected by DataProtection, Mapping), the preview,
/// the runs. Administrators only, checked here: everybody else gets nothing (null, an empty list or <see cref="Outcome{T}.Denied"/>).
/// </summary>
public static class DataSourceService
{
    /// <summary>The most records of the preview.</summary>
    public const int PreviewRows = 20;

    /// <summary>The records the preview reads to find the fields.</summary>
    public const int DiscoverySample = 200;

    /// <summary>The longest value the preview shows (the longest column of a contact): a longer one is cut, so the page stays small.</summary>
    public const int PreviewValueLength = 500;

    /// <summary>The largest CSV file.</summary>
    public const long MaxUpload = CsvConnector.MaxBytes;

    public static async Task<IReadOnlyList<DataSourceRow>> ListAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return [];
        }

        return await db.DataSources.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new DataSourceRow(s.Id, s.Name, s.Kind, s.AddressBook!.Name, s.Enabled, s.IntervalMinutes, s.LastRunDate, s.LastRunState, s.NextRunDate,
                db.Contacts.Count(c => c.DataSourceId == s.Id), s.LastError))
            .ToListAsync(ct);
    }

    /// <summary>The data source for its edit page (no secret); null when the actor may not or it does not exist.</summary>
    public static async Task<DataSourceDetail?> GetAsync(AppDbContext db, Actor actor, SourceEnvironment env, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.AsNoTracking().Include(s => s.Mappings).Include(s => s.AddressBook).FirstOrDefaultAsync(s => s.Id == id, ct) is not { } s)
        {
            return null;
        }

        var secrets = env.Unprotect(s.SecretProtected);
        var contacts = await db.Contacts.CountAsync(c => c.DataSourceId == id, ct);
        var settings = SourceSettings.FromJson(s.SettingsJson);
        CsvFileInfo? file = null;
        var path = env.CsvPath(s.Token);
        if (s.Kind == DataSourceKind.Csv && File.Exists(path))
        {
            var info = new FileInfo(path);
            file = new CsvFileInfo(settings.Csv.FileName ?? info.Name, info.Length, info.LastWriteTimeUtc);
        }

        return new DataSourceDetail(s.Id, s.Token, s.Name, s.Kind, s.AddressBookId, s.AddressBook?.Name ?? string.Empty, s.Enabled, s.IntervalMinutes, s.DeleteMissing,
            s.KeyField, settings, new SecretState(Has(secrets.Password), Has(secrets.Token), Has(secrets.ApiKey), Has(secrets.ConnectionString)),
            [.. s.Mappings.OrderBy(m => m.SortOrder).Select(m => new MappingRule(m.TargetField, m.SourceExpression, m.Transform))],
            SyncEngine.FieldsOf(s.DiscoveredFields), s.LastRunDate, s.LastRunState, s.NextRunDate, s.LastError, contacts, file);
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes the general settings of a data source; administrators only. Answers its id.</summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, long? id, DataSourceForm form, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<long>.Denied();
        }

        var name = (form.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 120)
        {
            return Outcome<long>.Fail(Text.T("Sources.Name.Invalid"));
        }

        if (!Enum.IsDefined(form.Kind))
        {
            return Outcome<long>.Fail(Text.T("Sources.Error.Kind"));
        }

        if (!await db.AddressBooks.AnyAsync(a => a.Id == form.AddressBookId, ct))
        {
            return Outcome<long>.Fail(Text.T("Sources.AddressBook.Required"));
        }

        if (form.IntervalMinutes is < 0 or > 10080)
        {
            return Outcome<long>.Fail(Text.T("Sources.Interval.Invalid"));
        }

        DataSource? source;
        if (id is { } existing)
        {
            source = await db.DataSources.FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (source is null)
            {
                return Outcome<long>.Denied();
            }
        }
        else
        {
            source = new DataSource { Kind = form.Kind };
            db.DataSources.Add(source);
        }

        var timing = source.Enabled != form.Enabled || source.IntervalMinutes != form.IntervalMinutes || id is null;
        source.Name = name;
        source.AddressBookId = form.AddressBookId;
        source.Enabled = form.Enabled;
        source.IntervalMinutes = form.IntervalMinutes;
        source.DeleteMissing = form.DeleteMissing;
        if (!form.Enabled || form.IntervalMinutes == 0)
        {
            source.NextRunDate = null;
        }
        else if (timing || source.NextRunDate is null)
        {
            source.NextRunDate = NextRun(source.LastRunDate, form.IntervalMinutes, DateTime.UtcNow);
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "source", source.Name, id is null ? ActivityLog.Text("Activity.Detail.Created") : ActivityLog.Text("Activity.Detail.Changed"), source.Id, ct);
        return Outcome<long>.Success(source.Id);
    }

    /// <summary>When a source with an interval runs next: an interval after the last run (not before now), a new one an interval from now.</summary>
    public static DateTime NextRun(DateTime? lastRun, int intervalMinutes, DateTime now)
    {
        if (lastRun is not { } last)
        {
            return now.AddMinutes(intervalMinutes);
        }

        var next = last.AddMinutes(intervalMinutes);
        return next > now ? next : now;
    }

    /// <summary>
    /// Saves the tab Connection: the kind, the settings of that kind (only its part is stored), the secrets it needs (a new value replaces the stored one, an
    /// empty one keeps it, the secrets the kind does not need are dropped) and an uploaded CSV file. Answers the message of the first wrong value.
    /// </summary>
    public static async Task<Outcome<bool>> SaveConnectionAsync(AppDbContext db, Actor actor, SourceEnvironment env, long id, ConnectionForm form, UploadedFile? upload,
        CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Outcome<bool>.Denied();
        }

        if (!Enum.IsDefined(form.Kind))
        {
            return Outcome<bool>.Fail(Text.T("Sources.Error.Kind"));
        }

        var stored = env.Unprotect(source.SecretProtected);
        static string? New(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var settings = form.Settings;
        SourceSecrets secrets;
        string? error;
        switch (form.Kind)
        {
            case DataSourceKind.Rest:
                (settings, secrets, error) = CheckRest(settings, stored, form.Secrets);
                break;
            case DataSourceKind.SqlServer:
                secrets = new SourceSecrets { ConnectionString = New(form.Secrets.ConnectionString) ?? stored.ConnectionString };
                (settings, error) = CheckSqlServer(settings, secrets);
                break;
            default:
                secrets = new SourceSecrets();
                var hasFile = upload is not null || File.Exists(env.CsvPath(source.Token));
                (settings, error) = CheckCsv(settings, upload, hasFile);
                break;
        }

        if (error is not null)
        {
            return Outcome<bool>.Fail(error);
        }

        var store = form.Kind == DataSourceKind.Csv && settings.Csv.Origin == CsvOrigin.Upload && upload is not null;
        if (form.Kind == DataSourceKind.Csv)
        {
            // the name of a new file, else the name of the file that is there stays
            var name = store ? Cut(Path.GetFileName(upload!.FileName), 255)
                : settings.Csv.Origin == CsvOrigin.Upload ? SourceSettings.FromJson(source.SettingsJson).Csv.FileName
                : null;
            settings = settings with { Csv = settings.Csv with { FileName = name } };
        }

        // what is stored must fit its column (a JSON escape or the protection make a value longer than it was typed)
        var json = settings.ToJson(form.Kind);
        var protectedSecrets = env.Protect(secrets);
        if (json.Length > DataSource.SettingsJsonMaxLength || protectedSecrets?.Length > DataSource.SecretProtectedMaxLength)
        {
            return Outcome<bool>.Fail(Text.T("Sources.Error.SettingsTooLarge"));
        }

        if (store)
        {
            Directory.CreateDirectory(env.SourcesDirectory);
            var path = env.CsvPath(source.Token);
            var temp = path + ".upload";
            await using (var input = upload!.Open())
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                await input.CopyToAsync(output, ct);
            }

            File.Move(temp, path, overwrite: true);
        }

        source.Kind = form.Kind;
        source.SettingsJson = json;
        source.SecretProtected = protectedSecrets;
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "source", source.Name, ActivityLog.Text("Activity.Detail.Connection"), source.Id, ct);
        return Outcome<bool>.Success(true);
    }

    private static (SourceSettings, SourceSecrets, string?) CheckRest(SourceSettings settings, SourceSecrets stored, SecretChange change)
    {
        static string? New(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var r = settings.Rest with
        {
            Url = settings.Rest.Url?.Trim() ?? string.Empty,
            Method = string.Equals(settings.Rest.Method, "POST", StringComparison.OrdinalIgnoreCase) ? "POST" : "GET",
            Body = New(settings.Rest.Body),
            Headers = New(settings.Rest.Headers),
            User = New(settings.Rest.User),
            ApiKeyHeader = New(settings.Rest.ApiKeyHeader) ?? "X-API-Key",
            ItemsPath = New(settings.Rest.ItemsPath),
            PageParameter = New(settings.Rest.PageParameter) ?? "page",
            PageSizeParameter = New(settings.Rest.PageSizeParameter),
            NextLinkPath = New(settings.Rest.NextLinkPath),
        };
        var secrets = r.Auth switch
        {
            // a password is kept as typed (a space at its end is part of it); a token and a key go into a header, where a space at an end does not survive
            RestAuth.Basic => new SourceSecrets { Password = string.IsNullOrWhiteSpace(change.Password) ? stored.Password : change.Password },
            RestAuth.Bearer => new SourceSecrets { Token = New(change.Token) ?? stored.Token },
            RestAuth.ApiKey => new SourceSecrets { ApiKey = New(change.ApiKey) ?? stored.ApiKey },
            _ => new SourceSecrets(),
        };
        var result = settings with { Rest = r };
        var tooLong = TooLong(
            (r.Url, 2000, "Sources.Rest.Url"), (r.Body, 10_000, "Sources.Rest.Body"), (r.Headers, 4000, "Sources.Rest.Headers"), (r.User, 200, "Sources.Rest.User"),
            (secrets.Password, 1000, "Sources.Rest.Password"), (secrets.Token, 4000, "Sources.Rest.Token"), (r.ApiKeyHeader, 100, "Sources.Rest.ApiKeyHeader"),
            (secrets.ApiKey, 4000, "Sources.Rest.ApiKey"), (r.ItemsPath, 200, "Sources.Rest.ItemsPath"), (r.PageParameter, 100, "Sources.Rest.PageParameter"),
            (r.PageSizeParameter, 100, "Sources.Rest.PageSizeParameter"), (r.NextLinkPath, 200, "Sources.Rest.NextLinkPath"));
        if (tooLong is not null)
        {
            return (result, secrets, tooLong);
        }

        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return (result, secrets, Text.T("Sources.Error.Url"));
        }

        if (r.Method == "POST" && r.Body is not null && !IsJson(r.Body))
        {
            return (result, secrets, Text.T("Sources.Rest.Body.Invalid"));
        }

        var badHeader = (r.Headers ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !RestConnector.ParseHeaders(l).Any());
        if (badHeader is not null)
        {
            return (result, secrets, Text.T("Sources.Rest.Headers.Invalid", badHeader));
        }

        if (!Enum.IsDefined(r.Auth) || !Enum.IsDefined(r.Pagination))
        {
            return (result, secrets, Text.T("Sources.Error.Kind"));
        }

        var missing = r.Auth switch
        {
            RestAuth.Basic when r.User is null => Text.T("Sources.Rest.User.Required"),
            RestAuth.Basic when secrets.Password is null => Text.T("Sources.Rest.Password.Required"),
            RestAuth.Bearer when secrets.Token is null => Text.T("Sources.Rest.Token.Required"),
            RestAuth.ApiKey when secrets.ApiKey is null => Text.T("Sources.Rest.ApiKey.Required"),
            RestAuth.ApiKey when !RestConnector.ParseHeaders(r.ApiKeyHeader + ": x").Any() => Text.T("Sources.Rest.Headers.Invalid", r.ApiKeyHeader),
            _ => null,
        };
        if (missing is not null)
        {
            return (result, secrets, missing);
        }

        if (r.Pagination == RestPagination.Page && (r.PageSize is < 1 or > 10_000 || r.PageStart is < 0 or > 1_000_000))
        {
            return (result, secrets, Text.T("Sources.Rest.PageSize.Invalid"));
        }

        if (r.TimeoutSeconds is < 1 or > 600)
        {
            return (result, secrets, Text.T("Sources.Range", Text.T("Sources.Rest.Timeout"), 1, 600));
        }

        if (r.MaxRecords is < 1 or > 1_000_000)
        {
            return (result, secrets, Text.T("Sources.Range", Text.T("Sources.Rest.MaxRecords"), 1, 1_000_000));
        }

        return (result, secrets, null);
    }

    private static (SourceSettings, string?) CheckSqlServer(SourceSettings settings, SourceSecrets secrets)
    {
        var s = settings.SqlServer with { Query = settings.SqlServer.Query?.Trim() ?? string.Empty };
        var result = settings with { SqlServer = s };
        if (secrets.ConnectionString is null)
        {
            return (result, Text.T("Sources.Error.ConnectionString"));
        }

        if (TooLong((secrets.ConnectionString, 4000, "Sources.Sql.ConnectionString"), (s.Query, 10_000, "Sources.Sql.Query")) is { } tooLong)
        {
            return (result, tooLong);
        }

        try
        {
            _ = new SqlConnectionStringBuilder(secrets.ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return (result, Text.T("Sources.Error.ConnectionStringInvalid", ex.Message));
        }

        if (SqlServerConnector.CheckQuery(s.Query) is { } refused)
        {
            return (result, refused);
        }

        return s.CommandTimeoutSeconds is < 1 or > 3600 ? (result, Text.T("Sources.Range", Text.T("Sources.Sql.Timeout"), 1, 3600)) : (result, null);
    }

    private static (SourceSettings, string?) CheckCsv(SourceSettings settings, UploadedFile? upload, bool hasFile)
    {
        var c = settings.Csv with { Url = string.IsNullOrWhiteSpace(settings.Csv.Url) ? null : settings.Csv.Url.Trim() };
        var result = settings with { Csv = c };
        if (!Enum.IsDefined(c.Origin) || !Enum.IsDefined(c.Delimiter) || !Enum.IsDefined(c.Encoding))
        {
            return (result, Text.T("Sources.Error.Kind"));
        }

        if (c.Origin == CsvOrigin.Url)
        {
            if (TooLong((c.Url, 2000, "Sources.Csv.Url")) is { } tooLong)
            {
                return (result, tooLong);
            }

            return Uri.TryCreate(c.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? (result, null) : (result, Text.T("Sources.Error.Url"));
        }

        if (upload is not null)
        {
            var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
            if (extension is not (".csv" or ".txt" or ".tsv"))
            {
                return (result, Text.T("Sources.Csv.File.Type"));
            }

            if (upload.Length is <= 0 or > MaxUpload)
            {
                return (result, Text.T("Sources.Csv.File.Size", MaxUpload / 1024 / 1024));
            }
        }

        return hasFile ? (result, null) : (result, Text.T("Sources.Error.NoFile"));
    }

    /// <summary>The first value that is longer than it may be (the page has the same limits, a post can pass them by), worded; null when all fit.</summary>
    private static string? TooLong(params ReadOnlySpan<(string? Value, int Max, string Label)> values)
    {
        foreach (var (value, max, label) in values)
        {
            if (value is not null && value.Length > max)
            {
                return Text.T("Sources.TooLong", Text.T(label), max);
            }
        }

        return null;
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the tab Mapping: the key (a field or a template) and the rules (a rule without an expression is left out). Answers the message of the first wrong value.
    /// </summary>
    public static async Task<Outcome<bool>> SaveMappingAsync(AppDbContext db, Actor actor, long id, string? keyField, IReadOnlyList<MappingRule> rules,
        CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.Include(s => s.Mappings).FirstOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Outcome<bool>.Denied();
        }

        var key = (keyField ?? string.Empty).Trim();
        if (key.Length is 0 or > 255)
        {
            return Outcome<bool>.Fail(Text.T("Sources.KeyField.Required"));
        }

        var kept = new List<FieldMapping>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var expression = (rule.SourceExpression ?? string.Empty).Trim();
            if (expression.Length == 0)
            {
                continue;
            }

            if (FieldMapper.Target(rule.TargetField) is not { } target || !seen.Add(target.Name) || !Enum.IsDefined(rule.Transform))
            {
                return Outcome<bool>.Fail(Text.T("Sources.Mapping.Invalid"));
            }

            if (expression.Length > 1000)
            {
                return Outcome<bool>.Fail(Text.T("Sources.Mapping.TooLong", Text.T("Sources.Target." + target.Name)));
            }

            kept.Add(new FieldMapping { DataSourceId = id, TargetField = target.Name, SourceExpression = expression, Transform = rule.Transform, SortOrder = kept.Count });
        }

        db.FieldMappings.RemoveRange(source.Mappings);
        db.FieldMappings.AddRange(kept);
        source.KeyField = key;
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "source", source.Name, ActivityLog.Text("Activity.Detail.Mapping"), source.Id, ct);
        return Outcome<bool>.Success(true);
    }

    /// <summary>
    /// Reads the first records of the source with its saved settings: the fields found (they are kept as the suggestions of the mapping), the first
    /// <see cref="PreviewRows"/> records and how they would become contacts. A failure is the error of the preview. Null when the actor may not.
    /// </summary>
    public static async Task<SourcePreview?> PreviewAsync(AppDbContext db, Actor actor, SourceEnvironment env, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.Include(s => s.Mappings).FirstOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return null;
        }

        var country = (await SettingsStore.LoadAsync(db, ct)).DefaultCountry;
        var rules = source.Mappings.OrderBy(m => m.SortOrder).Select(m => new MappingRule(m.TargetField, m.SourceExpression, m.Transform)).ToList();
        var rows = new List<SourceRecord>();
        var fields = new FieldCollector();
        string? error = null;
        try
        {
            var connector = Connectors.Create(source, env);
            var read = 0;
            await foreach (var record in connector.ReadAsync(ct))
            {
                fields.Add(record);
                if (rows.Count < PreviewRows)
                {
                    rows.Add(Shortened(record));
                }

                // a few more records than shown: a field that the first ones leave out is found too
                if (++read >= DiscoverySample)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = SyncEngine.MessageOf(ex);
        }

        if (fields.Names.Count > 0)
        {
            source.DiscoveredFields = SyncEngine.FieldsJson(fields.Names);
            await db.SaveChangesAsync(ct);
        }

        var mapped = rows.Select(r =>
        {
            var m = FieldMapper.Map(r, source.KeyField, rules, country);
            var problem = string.IsNullOrWhiteSpace(source.KeyField) ? Text.T("Sources.Error.NoKey")
                : m.Key is null ? Text.T("Sources.Preview.NoKey")
                : !FieldMapper.HasAnyValue(m) ? Text.T("Sources.Preview.Empty")
                : null;
            var contact = new Contact { ExternalKey = m.Key };
            FieldMapper.Write(contact, m.Values);
            return new MappedPreview(m.Key, contact.DisplayName, contact.Company, contact.Email, contact.PhoneBusiness ?? contact.PhoneMobile, problem);
        }).ToList();
        return new SourcePreview(fields.Names, rows, mapped, error);
    }

    /// <summary>A record of the preview with every value cut to <see cref="PreviewValueLength"/> (the mapping sees the cut values too: the preview only shows).</summary>
    private static SourceRecord Shortened(SourceRecord record)
    {
        var copy = new SourceRecord();
        foreach (var (name, value) in record)
        {
            copy[name] = value is { Length: > PreviewValueLength } ? value[..(PreviewValueLength - 1)] + "…" : value;
        }

        return copy;
    }

    /// <summary>
    /// Asks for a sync of a data source now ("Sync now"); administrators only. The run itself goes on in the background as the app (<see cref="SyncScheduler"/>):
    /// the permission is checked here, when it is asked for.
    /// </summary>
    public static async Task<Outcome<bool>> RequestSyncAsync(AppDbContext db, Actor actor, SyncQueue queue, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || !await db.DataSources.AnyAsync(s => s.Id == id, ct))
        {
            return Outcome<bool>.Denied();
        }

        queue.Request(id);
        return Outcome<bool>.Success(true);
    }

    /// <summary>Asks for a sync of every enabled data source ("Sync all", the platform action); administrators and the app only.</summary>
    public static Outcome<bool> RequestSyncAll(Actor actor, SyncQueue queue)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<bool>.Denied();
        }

        queue.RequestAll();
        return Outcome<bool>.Success(true);
    }

    /// <summary>Cancels the run of a data source; administrators only. The value: whether a run went on and was asked to stop.</summary>
    public static async Task<Outcome<bool>> CancelSyncAsync(AppDbContext db, Actor actor, SyncQueue queue, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || !await db.DataSources.AnyAsync(s => s.Id == id, ct))
        {
            return Outcome<bool>.Denied();
        }

        return Outcome<bool>.Success(queue.Cancel(id));
    }

    /// <summary>The runs of a data source, newest first; null when the actor may not or it does not exist.</summary>
    public static async Task<(IReadOnlyList<SyncRunRow> Rows, int Total, int Matching)?> RunsAsync(AppDbContext db, Actor actor, long id, RunCriteria criteria, int page,
        int pageSize, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || !await db.DataSources.AnyAsync(s => s.Id == id, ct))
        {
            return null;
        }

        var all = db.SyncRuns.AsNoTracking().Where(r => r.DataSourceId == id);
        var query = all;
        if (criteria.State is { } state)
        {
            query = query.Where(r => r.State == state);
        }

        if (criteria.Trigger is { } trigger)
        {
            query = query.Where(r => r.Trigger == trigger);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var pattern = "%" + criteria.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(r => EF.Functions.ILike(r.Message ?? string.Empty, pattern));
        }

        var total = await all.CountAsync(ct);
        var matching = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.StartDate).ThenByDescending(r => r.Id).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .Select(r => new SyncRunRow(r.Id, r.Trigger, r.State, r.StartDate, r.EndDate, r.RecordsRead, r.Created, r.Updated, r.Unchanged, r.Deleted, r.Skipped, r.Message))
            .ToListAsync(ct);
        return (rows, total, matching);
    }

    /// <summary>A run with its message; null when the actor may not or it does not exist (also when its data source is deleted).</summary>
    public static async Task<SyncRunDetail?> GetRunAsync(AppDbContext db, Actor actor, long runId, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        return await db.SyncRuns.AsNoTracking().Where(r => r.Id == runId && r.DataSource!.UpdateState != SoftEntity.Deleted)
            .Select(r => new SyncRunDetail(r.Id, r.Token, r.DataSourceId, r.DataSource!.Name, r.Trigger, r.State, r.StartDate, r.EndDate, r.RecordsRead, r.Created,
                r.Updated, r.Unchanged, r.Deleted, r.Skipped, r.Message))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Deletes (softly) a data source and its contacts, and removes its uploaded file; administrators only.</summary>
    public static async Task<Outcome<bool>> DeleteAsync(AppDbContext db, Actor actor, SourceEnvironment env, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Outcome<bool>.Denied();
        }

        // the contacts in one statement (a source may hold thousands), the source with them: both or neither. Deleted softly by hand: a Remove would let the
        // change tracker cut the contacts it tracks loose from the source (their DataSourceId set to null)
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Contacts.Where(c => c.DataSourceId == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UpdateState, SoftEntity.Deleted)
                .SetProperty(c => c.UpdateDate, DateTime.UtcNow)
                .SetProperty(c => c.UpdateUserId, actor.UserId), ct);
            source.UpdateState = SoftEntity.Deleted;
            source.NextRunDate = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // a bulk change past the change tracker and a save inside a transaction
        DirectoryChanges.Bump();
        var path = env.CsvPath(source.Token);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        await ActivityLog.AddAsync(db, actor, "source", source.Name, ActivityLog.Text("Activity.Detail.Deleted"), source.Id, ct);
        return Outcome<bool>.Success(true);
    }

    private static bool Has(string? value) => !string.IsNullOrEmpty(value);

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>A count for a page (<c>1,234</c> / <c>1.234</c>).</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentUICulture);
}
