using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using MatContactHub.Localization;

namespace MatContactHub.Services.Sources;

/// <summary>
/// The reader of CSV text (RFC 4180 and what spreadsheets write): fields between a delimiter, a field in double quotes may hold the delimiter, line breaks and
/// doubled quotes (<c>""</c>); lines end with CRLF, LF or CR; empty lines are left out. A quote that is never closed is an error (<see cref="SourceException"/>):
/// read as one field, it would swallow every record after it, and a sync would take those for deleted.
/// </summary>
public static class CsvParser
{
    /// <summary>The candidates of the detection, in the order a tie is decided.</summary>
    private static readonly char[] Candidates = [';', ',', '\t', '|'];

    public static char DelimiterOf(CsvDelimiter delimiter, string sample) => delimiter switch
    {
        CsvDelimiter.Semicolon => ';',
        CsvDelimiter.Comma => ',',
        CsvDelimiter.Tab => '\t',
        CsvDelimiter.Pipe => '|',
        _ => Detect(sample),
    };

    /// <summary>The delimiter of the first line (outside quotes): the most frequent of <c>;</c>, <c>,</c>, tab and <c>|</c>; a comma when there is none.</summary>
    public static char Detect(string sample)
    {
        var counts = new int[Candidates.Length];
        var quoted = false;
        foreach (var c in sample)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\n' or '\r')
            {
                break;
            }
            else if (!quoted)
            {
                var index = Array.IndexOf(Candidates, c);
                if (index >= 0)
                {
                    counts[index]++;
                }
            }
        }

        var best = 0;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > counts[best])
            {
                best = i;
            }
        }

        return counts[best] == 0 ? ',' : Candidates[best];
    }

    /// <summary>The records of the text, each a list of its fields. Throws a <see cref="SourceException"/> when the text ends inside a quoted field.</summary>
    public static IEnumerable<List<string>> Parse(TextReader reader, char delimiter)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        // the line of the text (1 = the first) and the one where the open quote began, for the message of a quote that is never closed
        var line = 1;
        var quoteLine = 0;
        var previous = '\0';
        var afterQuote = false;
        var anything = false;
        var buffer = new char[16 * 1024];
        int read;
        var pendingCr = false;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\r' || (c == '\n' && previous != '\r'))
                {
                    line++;
                }

                previous = c;
                if (pendingCr)
                {
                    pendingCr = false;
                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (quoted)
                {
                    if (c == '"')
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (afterQuote)
                    {
                        // "" inside a quoted field: one quote
                        field.Append('"');
                        quoted = true;
                        afterQuote = false;
                    }
                    else if (field.Length == 0)
                    {
                        quoted = true;
                        quoteLine = line;
                    }
                    else
                    {
                        field.Append(c);
                    }

                    anything = true;
                    continue;
                }

                afterQuote = false;
                if (c == delimiter)
                {
                    record.Add(field.ToString());
                    field.Clear();
                    anything = true;
                }
                else if (c is '\r' or '\n')
                {
                    pendingCr = c == '\r';
                    if (anything || field.Length > 0)
                    {
                        record.Add(field.ToString());
                        yield return record;
                        record = [];
                    }

                    field.Clear();
                    anything = false;
                }
                else
                {
                    field.Append(c);
                    anything = true;
                }
            }
        }

        if (quoted)
        {
            throw new SourceException(Text.T("Sources.Error.UnclosedQuote", quoteLine));
        }

        if (anything || field.Length > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    /// <summary>The names of the fields of a header line: trimmed, an empty one <c>ColumnN</c>, a repeated one with <c>_2</c>.</summary>
    public static List<string> Header(IReadOnlyList<string> line)
    {
        var names = new List<string>(line.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < line.Count; i++)
        {
            var name = line[i].Trim().Trim('﻿').Trim();
            if (name.Length == 0)
            {
                name = Column(i);
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

    /// <summary>The name of a field without a header: <c>Column1</c> for the first.</summary>
    public static string Column(int index) => "Column" + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The encoding of <see cref="CsvEncoding"/> (a byte order mark wins over it).</summary>
    public static Encoding EncodingOf(CsvEncoding encoding) => encoding switch
    {
        CsvEncoding.Windows1252 => CodePagesEncodingProvider.Instance.GetEncoding(1252)!,
        CsvEncoding.Latin1 => Encoding.Latin1,
        _ => new UTF8Encoding(false),
    };

    /// <summary>The records of a CSV stream as fields by name.</summary>
    public static IEnumerable<SourceRecord> Records(Stream stream, CsvSettings settings)
    {
        using var reader = new StreamReader(stream, EncodingOf(settings.Encoding), detectEncodingFromByteOrderMarks: true);

        // the delimiter is found in the first part of the text; the reader then starts again with that part
        var head = new char[64 * 1024];
        var length = reader.ReadBlock(head, 0, head.Length);
        var sample = new string(head, 0, length);
        var delimiter = DelimiterOf(settings.Delimiter, sample);
        using var replay = new PrefixedReader(sample, reader);
        List<string>? names = null;
        foreach (var line in Parse(replay, delimiter))
        {
            if (names is null && settings.HasHeader)
            {
                names = Header(line);
                continue;
            }

            names ??= [];
            var record = new SourceRecord();
            for (var i = 0; i < line.Count; i++)
            {
                if (i >= names.Count)
                {
                    names.Add(Column(i));
                }

                record[names[i]] = line[i];
            }

            for (var i = line.Count; i < names.Count; i++)
            {
                record[names[i]] = null;
            }

            yield return record;
        }
    }

    /// <summary>A reader that answers a text first and then the rest of another reader.</summary>
    private sealed class PrefixedReader(string prefix, TextReader rest) : TextReader
    {
        private int _position;

        public override int Read(char[] buffer, int index, int count)
        {
            if (_position < prefix.Length)
            {
                var n = Math.Min(count, prefix.Length - _position);
                prefix.CopyTo(_position, buffer, index, n);
                _position += n;
                return n;
            }

            return rest.Read(buffer, index, count);
        }

        public override int Peek() => _position < prefix.Length ? prefix[_position] : rest.Peek();

        public override int Read() => _position < prefix.Length ? prefix[_position++] : rest.Read();
    }
}

/// <summary>
/// Reads a CSV file: the uploaded one (<c>/data/sources/&lt;Token&gt;.csv</c>) or one fetched from an address at every read. The header line names the fields
/// (else <c>Column1</c> .. <c>ColumnN</c>).
/// </summary>
public sealed class CsvConnector(CsvSettings settings, Func<CancellationToken, Task<Stream>> open) : ISourceConnector
{
    /// <summary>The largest file fetched from an address (the same as an upload).</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    public bool Truncated => false;

    public static CsvConnector For(CsvSettings settings, Guid token, SourceEnvironment env) => new(settings, settings.Origin == CsvOrigin.Url
        ? ct => DownloadAsync(settings.Url, env.Http(false), ct)
        : _ =>
        {
            var path = env.CsvPath(token);
            if (!File.Exists(path))
            {
                throw new SourceException(Text.T("Sources.Error.NoFile"));
            }

            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024));
        });

    public async IAsyncEnumerable<SourceRecord> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await open(ct);
        foreach (var record in CsvParser.Records(stream, settings))
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    /// <summary>The file at an address, read into memory (at most <see cref="MaxBytes"/>).</summary>
    private static async Task<Stream> DownloadAsync(string? url, HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            throw new SourceException(Text.T("Sources.Error.Url"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            // the file sends no secret: a redirect is followed wherever it leads
            var (answer, answered) = await SourceHttp.SendAsync(http, address, HttpMethod.Get, (a, m) => new HttpRequestMessage(m, a), timeout.Token);
            using var response = answer;
            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException(Text.T("Sources.Error.Http", (int)response.StatusCode, response.ReasonPhrase ?? string.Empty, answered.GetLeftPart(UriPartial.Path)));
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var copy = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (copy.Length + n > MaxBytes)
                {
                    throw new SourceException(Text.T("Sources.Error.TooLarge", MaxBytes / 1024 / 1024));
                }

                copy.Write(buffer, 0, n);
            }

            copy.Position = 0;
            return copy;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SourceException(Text.T("Sources.Error.Timeout", 300));
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(Text.T("Sources.Error.Connect", ex.Message), ex);
        }
    }
}
