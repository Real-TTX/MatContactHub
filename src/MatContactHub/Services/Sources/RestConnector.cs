using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatContactHub.Localization;

namespace MatContactHub.Services.Sources;

/// <summary>
/// Reads a REST API that answers JSON. The records are the array at <see cref="RestSettings.ItemsPath"/> (a dot path; empty: the answer is the array); a nested
/// object becomes fields with dots (<c>address.city</c>), an array of plain values one field joined with "; ", an array of objects fields with the index
/// (<c>phones.0.number</c>). Pagination: none, a page number in the query until a page is empty, or the address of the next page (a field of the body or the
/// <c>Link</c> header). Every request has its timeout; <see cref="RestSettings.MaxRecords"/> caps one read. The sign-in and the extra headers only go to the server
/// of the configured address (<see cref="SourceHttp.IsTrusted"/>): a next page on another server is refused, a redirect to another server is followed without them.
/// </summary>
public sealed partial class RestConnector(RestSettings settings, SourceSecrets secrets, HttpClient http, long maxAnswerBytes = RestConnector.MaxAnswerBytes)
    : ISourceConnector
{
    /// <summary>The most pages one read follows (a guard against an API that never ends).</summary>
    private const int MaxPages = 100_000;

    /// <summary>The largest answer of one request (the same as a CSV file): a larger one fails the read instead of filling the memory.</summary>
    public const long MaxAnswerBytes = CsvConnector.MaxBytes;

    /// <summary>The configured address: its server is the one that gets the secrets.</summary>
    private Uri _origin = null!;

    public bool Truncated { get; private set; }

    public async IAsyncEnumerable<SourceRecord> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Uri.TryCreate(settings.Url.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new SourceException(Text.T("Sources.Error.Url"));
        }

        _origin = url;
        var max = settings.MaxRecords > 0 ? settings.MaxRecords : int.MaxValue;
        var count = 0;
        var page = settings.PageStart;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var pages = 0; pages < MaxPages; pages++)
        {
            var address = settings.Pagination == RestPagination.Page ? WithPage(url, page) : url;
            visited.Add(address.AbsoluteUri);
            var (records, next) = await FetchAsync(address, ct);
            foreach (var record in records)
            {
                if (count >= max)
                {
                    Truncated = true;
                    yield break;
                }

                count++;
                yield return record;
            }

            switch (settings.Pagination)
            {
                case RestPagination.Page when records.Count > 0:
                    page++;
                    continue;
                case RestPagination.NextLink when next is not null && !visited.Contains(next.AbsoluteUri):
                    url = next;
                    continue;
            }

            yield break;
        }

        // the guard against an endless API ended the read
        Truncated = true;
    }

    /// <summary>One request: its records and the address of the next page (for <see cref="RestPagination.NextLink"/>).</summary>
    private async Task<(List<SourceRecord> Records, Uri? Next)> FetchAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 3600)));
        try
        {
            var method = string.Equals(settings.Method, "POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get;
            var (answer, answered) = await SourceHttp.SendAsync(http, address, method, BuildRequest, timeout.Token);
            using var response = answer;
            if (!response.IsSuccessStatusCode)
            {
                throw new SourceException(Text.T("Sources.Error.Http", (int)response.StatusCode, response.ReasonPhrase ?? string.Empty, answered.GetLeftPart(UriPartial.Path)));
            }

            // an answer larger than a CSV file may be fails before it is read (Content-Length) or while it is read
            if (response.Content.Headers.ContentLength > maxAnswerBytes)
            {
                throw TooLarge();
            }

            await using var stream = new LimitedStream(await response.Content.ReadAsStreamAsync(timeout.Token), maxAnswerBytes, TooLarge);
            using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip },
                timeout.Token);
            var records = Items(json.RootElement);
            Uri? next = null;
            if (settings.Pagination == RestPagination.NextLink)
            {
                var link = string.IsNullOrWhiteSpace(settings.NextLinkPath) ? NextOfLinkHeader(response) : ValueOf(Navigate(json.RootElement, settings.NextLinkPath));
                if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(answered, link.Trim(), out var resolved) && resolved.Scheme is "http" or "https")
                {
                    if (!SourceHttp.IsTrusted(_origin, resolved))
                    {
                        // the next page would get the sign-in of the source: only from its own server
                        throw new SourceException(Text.T("Sources.Error.NextLinkOrigin", resolved.GetLeftPart(UriPartial.Authority), _origin.GetLeftPart(UriPartial.Authority)));
                    }

                    next = resolved;
                }
            }

            return (records, next);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SourceException(Text.T("Sources.Error.Timeout", settings.TimeoutSeconds));
        }
        catch (JsonException ex)
        {
            throw new SourceException(Text.T("Sources.Error.Json", ex.Message), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(Text.T("Sources.Error.Connect", ex.Message), ex);
        }
    }

    private SourceException TooLarge() => new(Text.T("Sources.Error.AnswerTooLarge", Ui.Bytes(maxAnswerBytes)));

    /// <summary>A request to an address (also after a redirect): the extra headers and the sign-in only when the address is on the server of the source.</summary>
    private HttpRequestMessage BuildRequest(Uri address, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent(string.IsNullOrWhiteSpace(settings.Body) ? "{}" : settings.Body, Encoding.UTF8, "application/json");
        }

        if (!SourceHttp.IsTrusted(_origin, address))
        {
            return request;
        }

        foreach (var (name, value) in ParseHeaders(settings.Headers))
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.Remove(name);
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        switch (settings.Auth)
        {
            case RestAuth.Basic:
                var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.User}:{secrets.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", pair);
                break;
            case RestAuth.Bearer when !string.IsNullOrEmpty(secrets.Token):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Token);
                break;
            case RestAuth.ApiKey when !string.IsNullOrEmpty(secrets.ApiKey):
                var header = string.IsNullOrWhiteSpace(settings.ApiKeyHeader) ? "X-API-Key" : settings.ApiKeyHeader.Trim();
                request.Headers.Remove(header);
                request.Headers.TryAddWithoutValidation(header, secrets.ApiKey);
                break;
        }

        return request;
    }

    /// <summary>The extra headers: one per line, <c>Name: value</c>; lines without a colon are left out.</summary>
    public static IEnumerable<(string Name, string Value)> ParseHeaders(string? text)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            if (HeaderNameRegex().IsMatch(name))
            {
                yield return (name, line[(colon + 1)..].Trim());
            }
        }
    }

    /// <summary>The address of a page: the page parameter (and the size) set in the query, the rest of the query kept.</summary>
    private Uri WithPage(Uri url, int page)
    {
        var pairs = (url.Query.Length > 1 ? url.Query[1..] : string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsParameter(p, settings.PageParameter) && !IsParameter(p, settings.PageSizeParameter))
            .ToList();
        pairs.Add($"{Uri.EscapeDataString(settings.PageParameter)}={page.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(settings.PageSizeParameter))
        {
            pairs.Add($"{Uri.EscapeDataString(settings.PageSizeParameter.Trim())}={settings.PageSize.ToString(CultureInfo.InvariantCulture)}");
        }

        return new UriBuilder(url) { Query = string.Join('&', pairs) }.Uri;
    }

    private static bool IsParameter(string pair, string? name)
        => !string.IsNullOrWhiteSpace(name) && Uri.UnescapeDataString(pair.Split('=')[0]).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The records of an answer: the array at the items path (an array of plain values gives records with the one field <c>value</c>).</summary>
    private List<SourceRecord> Items(JsonElement root)
    {
        var path = settings.ItemsPath?.Trim();
        var items = string.IsNullOrEmpty(path) ? root : Navigate(root, path);
        if (items is not { ValueKind: JsonValueKind.Array } array)
        {
            throw new SourceException(string.IsNullOrEmpty(path) ? Text.T("Sources.Error.NotAList") : Text.T("Sources.Error.ItemsPath", path));
        }

        var records = new List<SourceRecord>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            var record = new SourceRecord();
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                Flatten(item, string.Empty, record);
            }
            else
            {
                record["value"] = ValueOf(item);
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Writes the fields of <paramref name="element"/> into <paramref name="into"/>: an object's fields with dots, an array of plain values as one value joined
    /// with "; ", an array with objects with the index.
    /// </summary>
    public static void Flatten(JsonElement element, string prefix, SourceRecord into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, into);
                }

                break;
            case JsonValueKind.Array:
                if (element.EnumerateArray().All(e => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                {
                    var values = element.EnumerateArray().Select(ValueOf).Where(v => !string.IsNullOrEmpty(v)).ToList();
                    into[prefix] = values.Count == 0 ? null : string.Join("; ", values);
                    break;
                }

                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, prefix + "." + i.ToString(CultureInfo.InvariantCulture), into);
                    i++;
                }

                break;
            default:
                into[prefix.Length == 0 ? "value" : prefix] = ValueOf(element);
                break;
        }
    }

    /// <summary>The element at a dot path (<c>data.items</c>, <c>results.0.id</c>); the names are found without case when the exact one is not there.</summary>
    public static JsonElement? Navigate(JsonElement root, string? path)
    {
        var current = root;
        foreach (var part in (path ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty(part, out var exact))
                {
                    current = exact;
                    continue;
                }

                var found = current.EnumerateObject().FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (found.Value.ValueKind == JsonValueKind.Undefined)
                {
                    return null;
                }

                current = found.Value;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>A plain value as text (a number as written, true / false); null for null, an object or an array.</summary>
    private static string? ValueOf(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.String => element.Value.GetString(),
        JsonValueKind.Number => element.Value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string? ValueOf(JsonElement element) => ValueOf((JsonElement?)element);

    /// <summary>The address of <c>rel="next"</c> in the <c>Link</c> header (RFC 8288).</summary>
    public static string? NextOfLinkHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (Match m in LinkRegex().Matches(string.Join(',', values)))
        {
            var rels = m.Groups["params"].Value;
            if (RelNextRegex().IsMatch(rels))
            {
                return m.Groups["url"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
    private static partial Regex HeaderNameRegex();

    [GeneratedRegex("<(?<url>[^>]*)>(?<params>[^,<]*)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("""rel\s*=\s*"?[^";]*\bnext\b""", RegexOptions.IgnoreCase)]
    private static partial Regex RelNextRegex();
}
