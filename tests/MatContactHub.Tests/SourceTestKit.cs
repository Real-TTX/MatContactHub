using System.Net;
using System.Text;
using MatContactHub.Services.Sources;
using Microsoft.AspNetCore.DataProtection;

namespace MatContactHub.Tests;

/// <summary>An HTTP server in memory: answers every request with the function, and keeps the requests (with their body) for the asserts.</summary>
public sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return answer(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>What the tests of the sources share: an environment with a temporary data folder, an ephemeral protector and a fake HTTP server.</summary>
public sealed class SourceTestKit : IDisposable
{
    public SourceTestKit(Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        Handler = new FakeHandler(answer ?? (_ => FakeHandler.Json("[]")));
        Folder = Path.Combine(Path.GetTempPath(), "mch-sources-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Folder);
        Env = new SourceEnvironment(new EphemeralDataProtectionProvider(), Folder, _ => new HttpClient(Handler, disposeHandler: false));
    }

    public FakeHandler Handler { get; }

    public string Folder { get; }

    public SourceEnvironment Env { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // a file still open on a slow machine: the temporary folder is cleaned up by the system
        }
    }

    /// <summary>Every record of a connector.</summary>
    public static async Task<List<SourceRecord>> ReadAllAsync(ISourceConnector connector)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in connector.ReadAsync(CancellationToken.None))
        {
            records.Add(record);
        }

        return records;
    }
}
