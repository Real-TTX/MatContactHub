using System.Globalization;
using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Sources;

/// <summary>A line of the tab Mapping as the form sends it.</summary>
public sealed class MappingInput
{
    public string TargetField { get; set; } = string.Empty;

    public string? SourceExpression { get; set; }

    public FieldTransform Transform { get; set; }
}

/// <summary>
/// A data source (administrators; module B) with the tabs General, Connection (only the fields of the chosen kind show, switched without a reload), Mapping
/// (a line per field of the contact, the fields of the source as suggestions, Auto-map), Preview (the fields and the first records) and Runs. A new source has
/// the tab General only; after its first save the page goes on to Connection. The page reads and writes through <see cref="DataSourceService"/> only; the
/// secrets are never sent back (a stored one shows as "set").
/// </summary>
[RequestFormLimits(MultipartBodyLengthLimit = DataSourceService.MaxUpload + 1024 * 1024)]
[RequestSizeLimit(DataSourceService.MaxUpload + 1024 * 1024)]
public sealed class EditModel(AppDbContext db, SyncQueue queue, SourceEnvironment env) : AppPageModel
{
    /// <summary>The tabs of an existing data source, in order.</summary>
    public static readonly string[] Tabs = ["general", "connection", "mapping", "preview", "runs"];

    private const int RunsPageSize = 25;

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "general";

    // ---- General
    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public DataSourceKind Kind { get; set; } = DataSourceKind.Rest;

    [BindProperty] public long AddressBookId { get; set; }

    [BindProperty] public bool Enabled { get; set; } = true;

    [BindProperty] public int IntervalMinutes { get; set; } = 60;

    [BindProperty] public bool DeleteMissing { get; set; } = true;

    // ---- Connection: REST
    [BindProperty] public string RestUrl { get; set; } = string.Empty;

    [BindProperty] public string RestMethod { get; set; } = "GET";

    [BindProperty] public string? RestBody { get; set; }

    [BindProperty] public string? RestHeaders { get; set; }

    [BindProperty] public RestAuth RestAuth { get; set; }

    [BindProperty] public string? RestUser { get; set; }

    [BindProperty] public string? RestPassword { get; set; }

    [BindProperty] public string? RestToken { get; set; }

    [BindProperty] public string RestApiKeyHeader { get; set; } = "X-API-Key";

    [BindProperty] public string? RestApiKey { get; set; }

    [BindProperty] public string? RestItemsPath { get; set; }

    [BindProperty] public RestPagination RestPagination { get; set; }

    [BindProperty] public string RestPageParameter { get; set; } = "page";

    [BindProperty] public int RestPageStart { get; set; } = 1;

    [BindProperty] public string? RestPageSizeParameter { get; set; }

    [BindProperty] public int RestPageSize { get; set; } = 100;

    [BindProperty] public string? RestNextLinkPath { get; set; }

    [BindProperty] public int RestTimeout { get; set; } = 60;

    [BindProperty] public int RestMaxRecords { get; set; } = 100_000;

    [BindProperty] public bool RestIgnoreTls { get; set; }

    // ---- Connection: SQL Server
    [BindProperty] public string? SqlConnectionString { get; set; }

    [BindProperty] public string SqlQuery { get; set; } = string.Empty;

    [BindProperty] public int SqlTimeout { get; set; } = 60;

    [BindProperty] public bool SqlTrustServerCertificate { get; set; }

    // ---- Connection: CSV
    [BindProperty] public CsvOrigin CsvOrigin { get; set; }

    [BindProperty] public string? CsvUrl { get; set; }

    [BindProperty] public CsvDelimiter CsvDelimiter { get; set; }

    [BindProperty] public CsvEncoding CsvEncoding { get; set; }

    [BindProperty] public bool CsvHasHeader { get; set; } = true;

    [BindProperty] public IFormFile? CsvFile { get; set; }

    // ---- Mapping
    [BindProperty] public string KeyField { get; set; } = string.Empty;

    [BindProperty] public List<MappingInput> Mappings { get; set; } = [];

    // ---- what the page shows
    public DataSourceDetail? Source { get; private set; }

    public bool IsNew => Id is null;

    public bool Running => Id is { } id && queue.IsRunning(id);

    public bool Waiting => Id is { } id && queue.IsWaiting(id);

    public string Zone => Me.TimeZone;

    public List<Choice> Kinds { get; } = [.. Enum.GetValues<DataSourceKind>().Select(k => new Choice(k.ToString(), Text.T("Sources.Kind." + k)))];

    public List<Choice> AddressBooks { get; private set; } = [];

    public SourcePreview? Preview { get; private set; }

    public IReadOnlyList<SyncRunRow> Runs { get; private set; } = [];

    public ToolbarModel? RunsToolbar { get; private set; }

    public PagerModel? RunsPager { get; private set; }

    /// <summary>The fields of the source known from the preview or the last sync (the suggestions of the mapping).</summary>
    public IReadOnlyList<string> Fields => Source?.Fields ?? [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Tabs.Contains(Tab))
        {
            Tab = "general";
        }

        if (Id is not null)
        {
            if (!await LoadSourceAsync())
            {
                return NotFound();
            }

            Fill(Source!);
            if (Tab == "runs")
            {
                await LoadRunsAsync();
            }
        }
        else
        {
            Tab = "general";
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Saves the tab General (and creates a new source: then on to Connection).</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        Tab = "general";
        var form = new DataSourceForm(Name, Kind, AddressBookId, Enabled, IntervalMinutes, DeleteMissing);
        var saved = await DataSourceService.SaveAsync(db, Actor.ForUser(Me), Id, form, HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            return await FailAsync(saved.Error!);
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect(IsNew ? Address(saved.Value, "connection") : Address(saved.Value, "general"));
    }

    public async Task<IActionResult> OnPostConnectionAsync()
    {
        Tab = "connection";
        var settings = new SourceSettings
        {
            Rest = new RestSettings
            {
                Url = RestUrl,
                Method = RestMethod,
                Body = RestBody,
                Headers = RestHeaders,
                Auth = RestAuth,
                User = RestUser,
                ApiKeyHeader = RestApiKeyHeader,
                ItemsPath = RestItemsPath,
                Pagination = RestPagination,
                PageParameter = RestPageParameter,
                PageStart = RestPageStart,
                PageSizeParameter = RestPageSizeParameter,
                PageSize = RestPageSize,
                NextLinkPath = RestNextLinkPath,
                TimeoutSeconds = RestTimeout,
                MaxRecords = RestMaxRecords,
                IgnoreTlsErrors = RestIgnoreTls,
            },
            SqlServer = new SqlServerSettings { Query = SqlQuery, CommandTimeoutSeconds = SqlTimeout, TrustServerCertificate = SqlTrustServerCertificate },
            Csv = new CsvSettings { Origin = CsvOrigin, Url = CsvUrl, Delimiter = CsvDelimiter, Encoding = CsvEncoding, HasHeader = CsvHasHeader },
        };
        var secrets = new SecretChange(RestPassword, RestToken, RestApiKey, SqlConnectionString);
        var upload = CsvFile is { Length: > 0 } file ? new UploadedFile(file.FileName, file.Length, file.OpenReadStream) : null;
        var saved = await DataSourceService.SaveConnectionAsync(db, Actor.ForUser(Me), env, Id ?? 0, new ConnectionForm(Kind, settings, secrets), upload,
            HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            return await FailAsync(saved.Error!);
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect(Address(Id, "connection"));
    }

    public async Task<IActionResult> OnPostMappingAsync()
    {
        Tab = "mapping";
        var saved = await DataSourceService.SaveMappingAsync(db, Actor.ForUser(Me), Id ?? 0, KeyField, Rules(), HttpContext.RequestAborted);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            return await FailAsync(saved.Error!);
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect(Address(Id, "mapping"));
    }

    /// <summary>Fills the empty lines of the mapping (and the key) from the fields of the source; nothing is saved until Save.</summary>
    public async Task<IActionResult> OnPostAutoMapAsync()
    {
        Tab = "mapping";
        if (!await LoadSourceAsync())
        {
            return NotFound();
        }

        var rules = Rules();
        var suggested = FieldMapper.AutoMap(Fields, rules);
        var key = string.IsNullOrWhiteSpace(KeyField) ? FieldMapper.SuggestKey(Fields) : KeyField;
        ModelState.Clear();
        KeyField = key ?? string.Empty;
        var all = rules.Where(r => !string.IsNullOrWhiteSpace(r.SourceExpression)).Concat(suggested).ToList();
        Mappings = [.. FieldMapper.Targets.Select(t => all.FirstOrDefault(r => r.TargetField == t.Name) is { } r
            ? new MappingInput { TargetField = t.Name, SourceExpression = r.SourceExpression, Transform = r.Transform }
            : new MappingInput { TargetField = t.Name })];
        if (Fields.Count == 0)
        {
            FlashError(Text.T("Sources.AutoMap.NoFields"));
        }
        else
        {
            Flash(Text.T("Sources.AutoMap.Done", suggested.Count));
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        Tab = "preview";
        Preview = await DataSourceService.PreviewAsync(db, Actor.ForUser(Me), env, Id ?? 0, HttpContext.RequestAborted);
        if (Preview is null || !await LoadSourceAsync())
        {
            return NotFound();
        }

        Fill(Source!);
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSyncAsync()
    {
        if (!(await DataSourceService.RequestSyncAsync(db, Actor.ForUser(Me), queue, Id ?? 0, HttpContext.RequestAborted)).Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Sources.SyncRequested"));
        return LocalRedirect(Address(Id, Tabs.Contains(Tab) ? Tab : "general"));
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        var cancelled = await DataSourceService.CancelSyncAsync(db, Actor.ForUser(Me), queue, Id ?? 0, HttpContext.RequestAborted);
        if (!cancelled.Ok)
        {
            return NotFound();
        }

        Flash(cancelled.Value ? Text.T("Sources.CancelRequested") : Text.T("Sources.NotRunning"));
        return LocalRedirect(Address(Id, Tabs.Contains(Tab) ? Tab : "general"));
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        // a run that goes on stops first (nothing happens for somebody who may not delete)
        await DataSourceService.CancelSyncAsync(db, Actor.ForUser(Me), queue, Id ?? 0, HttpContext.RequestAborted);

        var deleted = await DataSourceService.DeleteAsync(db, Actor.ForUser(Me), env, Id ?? 0, HttpContext.RequestAborted);
        if (!deleted.Ok)
        {
            return NotFound();
        }

        Flash(Text.T("Sources.Deleted"));
        return LocalRedirect("/Sources");
    }

    /// <summary>The address of a tab of a data source.</summary>
    public static string Address(long? id, string tab) => id is null ? "/Sources/Edit" : $"/Sources/Edit?id={id}&tab={tab}";

    /// <summary>The value of a mapping line for the view (what was posted, else what is stored).</summary>
    public MappingInput MappingOf(string target) => Mappings.FirstOrDefault(m => m.TargetField == target) ?? new MappingInput { TargetField = target };

    private List<MappingRule> Rules() => [.. Mappings.Where(m => !string.IsNullOrWhiteSpace(m.TargetField))
        .Select(m => new MappingRule(m.TargetField, m.SourceExpression ?? string.Empty, m.Transform))];

    private async Task<IActionResult> FailAsync(string error)
    {
        ModelState.AddModelError(string.Empty, error);
        if (Id is not null && !await LoadSourceAsync())
        {
            return NotFound();
        }

        await LoadAsync();
        return Page();
    }

    private async Task<bool> LoadSourceAsync()
    {
        Source = Id is { } id ? await DataSourceService.GetAsync(db, Actor.ForUser(Me), env, id, HttpContext.RequestAborted) : null;
        return Source is not null;
    }

    /// <summary>The form fields from the stored source (a GET).</summary>
    private void Fill(DataSourceDetail s)
    {
        (Name, Kind, AddressBookId, Enabled, IntervalMinutes, DeleteMissing, KeyField) = (s.Name, s.Kind, s.AddressBookId, s.Enabled, s.IntervalMinutes, s.DeleteMissing, s.KeyField);
        var r = s.Settings.Rest;
        (RestUrl, RestMethod, RestBody, RestHeaders, RestAuth, RestUser, RestApiKeyHeader, RestItemsPath) = (r.Url, r.Method, r.Body, r.Headers, r.Auth, r.User, r.ApiKeyHeader, r.ItemsPath);
        (RestPagination, RestPageParameter, RestPageStart, RestPageSizeParameter, RestPageSize, RestNextLinkPath) =
            (r.Pagination, r.PageParameter, r.PageStart, r.PageSizeParameter, r.PageSize, r.NextLinkPath);
        (RestTimeout, RestMaxRecords, RestIgnoreTls) = (r.TimeoutSeconds, r.MaxRecords, r.IgnoreTlsErrors);
        var q = s.Settings.SqlServer;
        (SqlQuery, SqlTimeout, SqlTrustServerCertificate) = (q.Query, q.CommandTimeoutSeconds, q.TrustServerCertificate);
        var c = s.Settings.Csv;
        (CsvOrigin, CsvUrl, CsvDelimiter, CsvEncoding, CsvHasHeader) = (c.Origin, c.Url, c.Delimiter, c.Encoding, c.HasHeader);
        Mappings = [.. FieldMapper.Targets.Select(t => s.Mappings.FirstOrDefault(m => m.TargetField == t.Name) is { } m
            ? new MappingInput { TargetField = t.Name, SourceExpression = m.SourceExpression, Transform = m.Transform }
            : new MappingInput { TargetField = t.Name })];
    }

    private async Task LoadRunsAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var state = Enum.TryParse<SyncState>(ListQuery.Text(Request, "state"), out var st) && Enum.IsDefined(st) ? st : (SyncState?)null;
        var trigger = Enum.TryParse<SyncTrigger>(ListQuery.Text(Request, "trigger"), out var tr) && Enum.IsDefined(tr) ? tr : (SyncTrigger?)null;
        var page = ListQuery.Page(Request);
        var result = await DataSourceService.RunsAsync(db, Actor.ForUser(Me), Id ?? 0, new RunCriteria(search, state, trigger), page, RunsPageSize, HttpContext.RequestAborted);
        var (rows, total, matching) = result ?? ([], 0, 0);
        Runs = rows;
        RunsToolbar = new ToolbarModel
        {
            Id = "runs",
            Action = "/Sources/Edit",
            Label = Text.T("Sources.Tab.runs"),
            Search = search,
            SearchPlaceholder = Text.T("Sources.Runs.Search"),
            Filters =
            [
                new FilterField("state", Text.T("Sources.Col.State"), [.. Enum.GetValues<SyncState>().Select(x => new FilterOption(x.ToString(), Text.T("Sources.State." + x)))],
                    state?.ToString()),
                new FilterField("trigger", Text.T("Sources.Col.Trigger"), [.. Enum.GetValues<SyncTrigger>().Select(x => new FilterOption(x.ToString(), Text.T("Sources.Trigger." + x)))],
                    trigger?.ToString()),
            ],
            Total = total,
            Matching = matching,
            Noun = Text.T("Sources.Runs.Many"),
            NounOne = Text.T("Sources.Runs.One"),
            Keep = new Dictionary<string, string?> { ["id"] = Id?.ToString(CultureInfo.InvariantCulture), ["tab"] = "runs" },
        };
        RunsPager = new PagerModel { Page = page, PageSize = RunsPageSize, Total = matching, Url = RunsToolbar.UrlWithPage };
    }

    private async Task LoadAsync()
    {
        var books = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
        AddressBooks = [.. books.Select(b => new Choice(b.Id.ToString(CultureInfo.InvariantCulture), b.Name))];
        if (Mappings.Count == 0)
        {
            Mappings = [.. FieldMapper.Targets.Select(t => new MappingInput { TargetField = t.Name })];
        }
    }
}
