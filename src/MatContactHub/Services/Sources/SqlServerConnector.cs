using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MatContactHub.Localization;
using Microsoft.Data.SqlClient;

namespace MatContactHub.Services.Sources;

/// <summary>
/// Reads the rows of a query against Microsoft SQL Server. Only a single SELECT (or WITH … SELECT) is taken (<see cref="CheckQuery"/>, a heuristic that refuses a
/// batch and every statement that changes something), and it runs in a read-only context: the connection asks for <c>ApplicationIntent=ReadOnly</c>, the
/// isolation level is READ COMMITTED and the query runs in a transaction that is always rolled back. The rows are read with
/// <see cref="CommandBehavior.SequentialAccess"/>; a column is a field (a name that comes twice gets <c>_2</c>), a value its text (invariant culture, a date
/// <c>yyyy-MM-dd</c>, a time stamp ISO 8601, binary data left out).
/// </summary>
public sealed partial class SqlServerConnector(SqlServerSettings settings, SourceSecrets secrets) : ISourceConnector
{
    public bool Truncated => false;

    public async IAsyncEnumerable<SourceRecord> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secrets.ConnectionString))
        {
            throw new SourceException(Text.T("Sources.Error.ConnectionString"));
        }

        if (CheckQuery(settings.Query) is { } refused)
        {
            throw new SourceException(refused);
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(secrets.ConnectionString) { ApplicationIntent = ApplicationIntent.ReadOnly, ApplicationName = "MatContactHub" };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new SourceException(Text.T("Sources.Error.ConnectionStringInvalid", ex.Message), ex);
        }

        if (settings.TrustServerCertificate)
        {
            builder.TrustServerCertificate = true;
        }

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var isolation = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", connection))
        {
            await isolation.ExecuteNonQueryAsync(ct);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using var command = new SqlCommand(settings.Query, connection, transaction)
            {
                CommandTimeout = Math.Clamp(settings.CommandTimeoutSeconds, 1, 3600),
            };
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            var names = ColumnNames(reader);
            while (await reader.ReadAsync(ct))
            {
                var record = new SourceRecord();
                for (var i = 0; i < names.Count; i++)
                {
                    // sequential access: every column once and in order
                    record[names[i]] = await reader.IsDBNullAsync(i, ct) ? null : ValueOf(reader.GetValue(i));
                }

                yield return record;
            }
        }
        finally
        {
            // a read-only context: whatever the query might have done is undone
            if (connection.State == ConnectionState.Open)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // the server ended the transaction already (an error of the query)
                }
            }
        }
    }

    /// <summary>The names of the columns: an empty one is <c>ColumnN</c>, a repeated one gets <c>_2</c>, <c>_3</c>.</summary>
    private static List<string> ColumnNames(SqlDataReader reader)
    {
        var names = new List<string>(reader.FieldCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Column" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }

            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
            {
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            names.Add(unique);
        }

        return names;
    }

    /// <summary>A value of a column as text.</summary>
    public static string? ValueOf(object value) => value switch
    {
        string s => s,
        DateTime { TimeOfDay.Ticks: 0 } d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static readonly string[] Forbidden =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE", "SHUTDOWN",
        "DBCC", "INTO", "OPENROWSET", "OPENDATASOURCE", "OPENQUERY", "OPENXML", "BULK", "WAITFOR", "RECONFIGURE", "KILL", "USE", "SET", "DECLARE", "GO", "COMMIT",
        "ROLLBACK", "TRANSACTION", "SAVE", "RECEIVE", "SEND", "ENABLE", "DISABLE", "CHECKPOINT", "READTEXT", "WRITETEXT", "UPDATETEXT",
    ];

    /// <summary>
    /// Is the query a single SELECT? A heuristic: comments, strings and quoted names are left out, then the query must start with SELECT or WITH, must not hold a
    /// second statement (a <c>;</c> other than at the end) and none of the words that change something (INSERT, UPDATE, DELETE, MERGE, DROP, EXEC, SELECT … INTO …)
    /// nor a system procedure (<c>sp_</c>, <c>xp_</c>). Answers the reason it is refused, or null.
    /// </summary>
    public static string? CheckQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Text.T("Sources.Query.Required");
        }

        var code = StripLiterals(query).Trim().TrimEnd(';').Trim();
        if (code.Contains(';', StringComparison.Ordinal))
        {
            return Text.T("Sources.Query.Batch");
        }

        var words = WordRegex().Matches(code).Select(m => m.Value.ToUpperInvariant()).ToList();
        if (words.Count == 0 || words[0] is not ("SELECT" or "WITH"))
        {
            return Text.T("Sources.Query.SelectOnly");
        }

        var bad = words.FirstOrDefault(w => Forbidden.Contains(w, StringComparer.Ordinal) || w.StartsWith("SP_", StringComparison.Ordinal) || w.StartsWith("XP_", StringComparison.Ordinal));
        return bad is null ? null : Text.T("Sources.Query.Forbidden", bad);
    }

    /// <summary>The query without comments, strings ('…'), quoted names ([…], "…"): what is left is the code.</summary>
    private static string StripLiterals(string query)
    {
        var sb = new StringBuilder(query.Length);
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            var next = i + 1 < query.Length ? query[i + 1] : '\0';
            if (c == '-' && next == '-')
            {
                while (i < query.Length && query[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                var end = query.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? query.Length : end + 2;
                sb.Append(' ');
            }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < query.Length)
                {
                    if (query[i] == close)
                    {
                        // a doubled closing character is the character itself
                        if (i + 1 < query.Length && query[i + 1] == close)
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                i++;
                sb.Append(c == '\'' ? " '' " : " x ");
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[A-Za-z_@#][A-Za-z0-9_@#$]*")]
    private static partial Regex WordRegex();
}
