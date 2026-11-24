using System.Text.Json;
using System.Text.Json.Serialization;
using MatContactHub.Data;

namespace MatContactHub.Services.Sources;

/// <summary>How a REST API is signed in to.</summary>
public enum RestAuth
{
    None = 0,

    /// <summary>A user and a password (<c>Authorization: Basic</c>).</summary>
    Basic = 1,

    /// <summary>A token (<c>Authorization: Bearer</c>).</summary>
    Bearer = 2,

    /// <summary>A key in a header of its own (<c>X-API-Key</c>).</summary>
    ApiKey = 3,
}

/// <summary>How the records of a REST API come in several answers.</summary>
public enum RestPagination
{
    /// <summary>One answer holds every record.</summary>
    None = 0,

    /// <summary>A page number in the query (<c>?page=1&amp;size=100</c>), until a page is empty.</summary>
    Page = 1,

    /// <summary>The answer names the address of the next page (a field of the body or the <c>Link</c> header).</summary>
    NextLink = 2,
}

/// <summary>Where a CSV file comes from.</summary>
public enum CsvOrigin
{
    /// <summary>A file uploaded on the page (kept in <c>/data/sources/&lt;Token&gt;.csv</c>).</summary>
    Upload = 0,

    /// <summary>An address the file is fetched from at every sync.</summary>
    Url = 1,
}

/// <summary>The character between the fields of a CSV file.</summary>
public enum CsvDelimiter
{
    /// <summary>Found in the first line: the most frequent of <c>;</c>, <c>,</c>, tab and <c>|</c>.</summary>
    Auto = 0,
    Semicolon = 1,
    Comma = 2,
    Tab = 3,
    Pipe = 4,
}

/// <summary>The encoding of a CSV file (a byte order mark wins).</summary>
public enum CsvEncoding
{
    Utf8 = 0,
    Windows1252 = 1,
    Latin1 = 2,
}

/// <summary>The settings of a REST API that are no secret.</summary>
public sealed record RestSettings
{
    public string Url { get; init; } = string.Empty;

    /// <summary>GET or POST.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>The JSON body of a POST (optional).</summary>
    public string? Body { get; init; }

    /// <summary>Extra headers, one per line: <c>Name: value</c>.</summary>
    public string? Headers { get; init; }

    public RestAuth Auth { get; init; }

    /// <summary>The user of <see cref="RestAuth.Basic"/>.</summary>
    public string? User { get; init; }

    /// <summary>The header of <see cref="RestAuth.ApiKey"/>.</summary>
    public string ApiKeyHeader { get; init; } = "X-API-Key";

    /// <summary>The path of the array of records in the answer (<c>data.items</c>); empty = the answer is the array.</summary>
    public string? ItemsPath { get; init; }

    public RestPagination Pagination { get; init; }

    public string PageParameter { get; init; } = "page";

    public int PageStart { get; init; } = 1;

    /// <summary>The parameter of the page size (optional).</summary>
    public string? PageSizeParameter { get; init; }

    public int PageSize { get; init; } = 100;

    /// <summary>The path of the address of the next page in the body (<c>links.next</c>); empty = the <c>Link</c> header (<c>rel="next"</c>).</summary>
    public string? NextLinkPath { get; init; }

    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>The most records one sync reads (a safety cap).</summary>
    public int MaxRecords { get; init; } = 100_000;

    /// <summary>Accept any TLS certificate (a self-signed one of an internal server). Dangerous: only with the explicit warning of the page.</summary>
    public bool IgnoreTlsErrors { get; init; }
}

/// <summary>The settings of a SQL Server query that are no secret (the connection string is one).</summary>
public sealed record SqlServerSettings
{
    public string Query { get; init; } = string.Empty;

    public int CommandTimeoutSeconds { get; init; } = 60;

    public bool TrustServerCertificate { get; init; }
}

/// <summary>The settings of a CSV file.</summary>
public sealed record CsvSettings
{
    public CsvOrigin Origin { get; init; }

    public string? Url { get; init; }

    /// <summary>The name of the uploaded file (to show it; the file itself is <c>/data/sources/&lt;Token&gt;.csv</c>).</summary>
    public string? FileName { get; init; }

    public CsvDelimiter Delimiter { get; init; }

    public CsvEncoding Encoding { get; init; }

    /// <summary>The first line names the fields; without it they are <c>Column1</c> .. <c>ColumnN</c>.</summary>
    public bool HasHeader { get; init; } = true;
}

/// <summary>The settings of a data source that are no secret (<see cref="DataSource.SettingsJson"/>): the part of its kind.</summary>
public sealed record SourceSettings
{
    public RestSettings Rest { get; init; } = new();

    public SqlServerSettings SqlServer { get; init; } = new();

    public CsvSettings Csv { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settings stored for <paramref name="kind"/> (only its part is written).</summary>
    public string ToJson(DataSourceKind kind) => kind switch
    {
        DataSourceKind.Rest => JsonSerializer.Serialize(new { rest = Rest }, Json),
        DataSourceKind.SqlServer => JsonSerializer.Serialize(new { sqlServer = SqlServer }, Json),
        _ => JsonSerializer.Serialize(new { csv = Csv }, Json),
    };

    /// <summary>The settings of <see cref="DataSource.SettingsJson"/>; a part that is not there has its defaults.</summary>
    public static SourceSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SourceSettings();
        }

        try
        {
            var read = JsonSerializer.Deserialize<SourceSettings>(json, Json) ?? new SourceSettings();
            return read with { Rest = read.Rest ?? new(), SqlServer = read.SqlServer ?? new(), Csv = read.Csv ?? new() };
        }
        catch (JsonException)
        {
            return new SourceSettings();
        }
    }
}

/// <summary>The secrets of a data source (<see cref="DataSource.SecretProtected"/>, protected with DataProtection). They never go back to the browser.</summary>
public sealed record SourceSecrets
{
    /// <summary>The password of <see cref="RestAuth.Basic"/>.</summary>
    public string? Password { get; init; }

    /// <summary>The token of <see cref="RestAuth.Bearer"/>.</summary>
    public string? Token { get; init; }

    /// <summary>The key of <see cref="RestAuth.ApiKey"/>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The connection string of SQL Server.</summary>
    public string? ConnectionString { get; init; }

    public bool IsEmpty => Password is null && Token is null && ApiKey is null && ConnectionString is null;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SourceSecrets FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SourceSecrets();
        }

        try
        {
            return JsonSerializer.Deserialize<SourceSecrets>(json) ?? new SourceSecrets();
        }
        catch (JsonException)
        {
            return new SourceSecrets();
        }
    }
}
