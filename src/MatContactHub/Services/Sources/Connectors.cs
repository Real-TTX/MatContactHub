using MatContactHub.Data;
using MatContactHub.Localization;

namespace MatContactHub.Services.Sources;

/// <summary>One record of a source: its fields by name (without case: <c>FirstName</c> and <c>firstname</c> are one field), every value a string or null.</summary>
public sealed class SourceRecord : Dictionary<string, string?>
{
    public SourceRecord()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

/// <summary>A source that cannot be read, worded for the person (the message goes into the run and onto the page).</summary>
public sealed class SourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A connector reads the records of one data source, one after the other (a REST API, a SQL Server query, a CSV file). A connector is made for one read
/// (<see cref="Connectors.Create"/>) and says afterwards whether it stopped early (<see cref="Truncated"/>: the sync must not delete what it did not see then).
/// </summary>
public interface ISourceConnector
{
    /// <summary>The records, in the order of the source. A failure throws (a <see cref="SourceException"/> when it can be worded).</summary>
    IAsyncEnumerable<SourceRecord> ReadAsync(CancellationToken ct);

    /// <summary>The read stopped at a limit (<see cref="RestSettings.MaxRecords"/>) before the source ended.</summary>
    bool Truncated { get; }

    /// <summary>The fields the source has: those of the first <paramref name="sample"/> records, in the order they were first seen.</summary>
    async Task<IReadOnlyList<string>> DiscoverFieldsAsync(int sample, CancellationToken ct)
    {
        var fields = new FieldCollector();
        var n = 0;
        await foreach (var record in ReadAsync(ct))
        {
            fields.Add(record);
            if (++n >= sample)
            {
                break;
            }
        }

        return fields.Names;
    }
}

/// <summary>The names of the fields of records, in the order they were first seen, each once (without case).</summary>
public sealed class FieldCollector
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = [];

    public IReadOnlyList<string> Names => _names;

    public void Add(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (_seen.Add(name))
            {
                _names.Add(name);
            }
        }
    }

    public void Add(SourceRecord record) => Add(record.Keys);
}

/// <summary>Makes the connector of a data source.</summary>
public static class Connectors
{
    public static ISourceConnector Create(DataSource source, SourceEnvironment env)
    {
        var settings = SourceSettings.FromJson(source.SettingsJson);
        var secrets = env.Unprotect(source.SecretProtected);
        return source.Kind switch
        {
            DataSourceKind.Rest => new RestConnector(settings.Rest, secrets, env.Http(settings.Rest.IgnoreTlsErrors)),
            DataSourceKind.SqlServer => new SqlServerConnector(settings.SqlServer, secrets),
            DataSourceKind.Csv => CsvConnector.For(settings.Csv, source.Token, env),
            _ => throw new SourceException(Text.T("Sources.Error.Kind")),
        };
    }
}
