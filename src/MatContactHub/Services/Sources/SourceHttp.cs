using System.Net;
using MatContactHub.Localization;

namespace MatContactHub.Services.Sources;

/// <summary>
/// The requests of the connectors. The HTTP clients of the sources do not follow a redirect themselves (.NET would keep a header such as the API key on the way to
/// another server): <see cref="SendAsync"/> follows up to <see cref="MaxRedirects"/> of them and builds every request anew, so the connector decides per address
/// what it sends (<see cref="IsTrusted"/>: the secrets only go to the server of the configured address).
/// </summary>
public static class SourceHttp
{
    /// <summary>The most redirects one request follows.</summary>
    public const int MaxRedirects = 10;

    /// <summary>
    /// Sends the request that <paramref name="build"/> makes for an address and a method and follows the redirects (303, and 301 / 302 after a POST, go on with a
    /// GET, as a browser does). Answers the first answer that is no redirect and the address it came from.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, Uri Address)> SendAsync(HttpClient http, Uri address, HttpMethod method,
        Func<Uri, HttpMethod, HttpRequestMessage> build, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            var request = build(address, method);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return (response, address);
            }

            var status = response.StatusCode;
            response.Dispose();
            request.Dispose();
            if (hop >= MaxRedirects)
            {
                throw new SourceException(Text.T("Sources.Error.Redirects", MaxRedirects));
            }

            var next = location.IsAbsoluteUri ? location : new Uri(address, location);
            if (next.Scheme is not ("http" or "https"))
            {
                throw new SourceException(Text.T("Sources.Error.Url"));
            }

            if (status == HttpStatusCode.SeeOther || ((status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found) && method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
            }

            address = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// May the secrets of a source configured at <paramref name="origin"/> go to <paramref name="address"/>? Only on the same server: the same scheme, host and port,
    /// or the same host by https when the source was configured with http (an upgrade, never a downgrade).
    /// </summary>
    public static bool IsTrusted(Uri origin, Uri address)
    {
        if (!string.Equals(origin.IdnHost, address.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (origin.Scheme == address.Scheme)
        {
            return origin.Port == address.Port;
        }

        return origin.Scheme == Uri.UriSchemeHttp && address.Scheme == Uri.UriSchemeHttps && address.IsDefaultPort;
    }
}
