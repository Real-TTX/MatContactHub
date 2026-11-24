using System.Net;
using System.Text;
using System.Web;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Sources;

namespace MatContactHub.Tests;

/// <summary>The REST connector against a fake server: sign-in headers, extra headers, POST, the path of the records, flattening, both paginations, the limits.</summary>
public sealed class SourceRestTests
{
    private static RestConnector Connector(SourceTestKit kit, RestSettings settings, SourceSecrets? secrets = null)
        => new(settings, secrets ?? new SourceSecrets(), kit.Env.Http(settings.IgnoreTlsErrors));

    [Fact]
    public async Task Basic_sign_in_sends_user_and_password()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("""[{"id":1}]"""));
        await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c", Auth = RestAuth.Basic, User = "reader" },
            new SourceSecrets { Password = "s3cret" }));
        var auth = kit.Handler.Requests.Single().Request.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal("reader:s3cret", Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
    }

    [Fact]
    public async Task Bearer_sign_in_sends_the_token()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("[]"));
        await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c", Auth = RestAuth.Bearer }, new SourceSecrets { Token = "tok-123" }));
        var auth = kit.Handler.Requests.Single().Request.Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("tok-123", auth.Parameter);
    }

    [Fact]
    public async Task An_api_key_goes_into_its_header_with_the_extra_headers()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("[]"));
        var settings = new RestSettings
        {
            Url = "https://api.example.com/c",
            Auth = RestAuth.ApiKey,
            ApiKeyHeader = "X-Api-Token",
            Headers = "Accept-Language: de\nX-Tenant:  42 \nno colon here",
        };
        await SourceTestKit.ReadAllAsync(Connector(kit, settings, new SourceSecrets { ApiKey = "key-9" }));
        var request = kit.Handler.Requests.Single().Request;
        Assert.Equal("key-9", request.Headers.GetValues("X-Api-Token").Single());
        Assert.Equal("de", request.Headers.GetValues("Accept-Language").Single());
        Assert.Equal("42", request.Headers.GetValues("X-Tenant").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Without_sign_in_no_secret_is_sent()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("[]"));
        await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c" }, new SourceSecrets { Token = "never", Password = "never" }));
        Assert.Null(kit.Handler.Requests.Single().Request.Headers.Authorization);
    }

    [Fact]
    public async Task A_post_sends_its_json_body()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("[]"));
        await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/q", Method = "POST", Body = """{"filter":"all"}""" }));
        var (request, body) = kit.Handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("""{"filter":"all"}""", body);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_records_are_found_at_the_items_path_and_flattened()
    {
        var json = """
            {"data":{"items":[
              {"id":7,"name":{"first":"Ada","last":"Lovelace"},"tags":["vip","math"],"active":true,"score":1.5,"none":null,
               "phones":[{"type":"work","number":"030 1234"},{"type":"mobile","number":"0170 1"}],"address":{"geo":{"lat":52.5}}}
            ]}}
            """;
        using var kit = new SourceTestKit(_ => FakeHandler.Json(json));
        var record = Assert.Single(await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c", ItemsPath = "Data.Items" })));
        Assert.Equal("7", record["id"]);
        Assert.Equal("Ada", record["name.first"]);
        Assert.Equal("Lovelace", record["NAME.LAST"]);
        Assert.Equal("vip; math", record["tags"]);
        Assert.Equal("true", record["active"]);
        Assert.Equal("1.5", record["score"]);
        Assert.Null(record["none"]);
        Assert.Equal("030 1234", record["phones.0.number"]);
        Assert.Equal("mobile", record["phones.1.type"]);
        Assert.Equal("52.5", record["address.geo.lat"]);
    }

    [Fact]
    public async Task An_answer_that_is_an_array_needs_no_path()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("""[{"id":1},{"id":2},"plain"]"""));
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c" }));
        Assert.Equal(3, records.Count);
        Assert.Equal("plain", records[2]["value"]);
    }

    [Fact]
    public async Task An_object_without_a_path_is_a_worded_error()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("""{"data":[]}"""));
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c" })));
    }

    [Fact]
    public async Task An_http_error_is_a_worded_error()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("""{"error":"no"}""", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c" })));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_json_is_a_worded_error()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("<html>"));
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c" })));
    }

    [Fact]
    public async Task A_page_parameter_counts_up_until_a_page_is_empty()
    {
        using var kit = new SourceTestKit(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.Parse(query["p"]!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("2", query["limit"]);
            Assert.Equal("x", query["keep"]);
            return FakeHandler.Json(page switch
            {
                0 => """[{"id":"a"},{"id":"b"}]""",
                1 => """[{"id":"c"},{"id":"d"}]""",
                2 => """[{"id":"e"}]""",
                _ => "[]",
            });
        });
        var settings = new RestSettings
        {
            Url = "https://api.example.com/c?keep=x&p=99",
            Pagination = RestPagination.Page,
            PageParameter = "p",
            PageStart = 0,
            PageSizeParameter = "limit",
            PageSize = 2,
        };
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, settings));
        Assert.Equal(["a", "b", "c", "d", "e"], records.Select(r => r["id"]));
        Assert.Equal(4, kit.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_next_link_in_the_body_is_followed_also_when_relative()
    {
        using var kit = new SourceTestKit(request => FakeHandler.Json(request.RequestUri!.PathAndQuery switch
        {
            "/v1/contacts" => """{"results":[{"id":1}],"links":{"next":"/v1/contacts?cursor=abc"}}""",
            "/v1/contacts?cursor=abc" => """{"results":[{"id":2}],"links":{"next":"https://api.example.com/v1/contacts?cursor=def"}}""",
            _ => """{"results":[{"id":3}],"links":{"next":null}}""",
        }));
        var settings = new RestSettings { Url = "https://api.example.com/v1/contacts", ItemsPath = "results", Pagination = RestPagination.NextLink, NextLinkPath = "links.next" };
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, settings));
        Assert.Equal(["1", "2", "3"], records.Select(r => r["id"]));
    }

    [Fact]
    public async Task A_next_link_in_the_link_header_is_followed()
    {
        using var kit = new SourceTestKit(request =>
        {
            var second = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            var response = FakeHandler.Json(second ? """[{"id":2}]""" : """[{"id":1}]""");
            if (!second)
            {
                response.Headers.TryAddWithoutValidation("Link", "<https://api.example.com/c?page=2>; rel=\"next\", <https://api.example.com/c?page=9>; rel=\"last\"");
            }

            return response;
        });
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c", Pagination = RestPagination.NextLink }));
        Assert.Equal(["1", "2"], records.Select(r => r["id"]));
    }

    [Theory]
    [InlineData("https://evil.example.net/c?page=2")]
    [InlineData("http://api.example.com/c?page=2")]
    [InlineData("https://api.example.com:8443/c?page=2")]
    public async Task A_next_link_to_another_server_is_refused_and_gets_no_secret(string link)
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json($$"""{"items":[{"id":1}],"next":"{{link}}"}"""));
        var connector = Connector(kit, new RestSettings
        {
            Url = "https://api.example.com/c", ItemsPath = "items", Pagination = RestPagination.NextLink, NextLinkPath = "next", Auth = RestAuth.Bearer,
        }, new SourceSecrets { Token = "tok-123" });
        var error = await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(connector));
        Assert.Contains(new Uri(link).GetLeftPart(UriPartial.Authority), error.Message, StringComparison.Ordinal);
        Assert.Equal("api.example.com", kit.Handler.Requests.Single().Request.RequestUri!.Host);
    }

    [Fact]
    public async Task A_redirect_on_the_same_server_keeps_the_sign_in()
    {
        using var kit = new SourceTestKit(request => request.RequestUri!.AbsolutePath == "/old"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("/new", UriKind.Relative) } }
            : FakeHandler.Json("""[{"id":1}]"""));
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "http://api.example.com/old", Auth = RestAuth.ApiKey, Headers = "X-Tenant: 42" },
            new SourceSecrets { ApiKey = "key-9" }));
        Assert.Single(records);
        Assert.Equal(2, kit.Handler.Requests.Count);
        var second = kit.Handler.Requests[1].Request;
        Assert.Equal("http://api.example.com/new", second.RequestUri!.AbsoluteUri);
        Assert.Equal("key-9", second.Headers.GetValues("X-API-Key").Single());
        Assert.Equal("42", second.Headers.GetValues("X-Tenant").Single());
    }

    [Fact]
    public async Task A_redirect_to_another_server_is_followed_without_the_secrets()
    {
        using var kit = new SourceTestKit(request => request.RequestUri!.Host == "api.example.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://files.example.net/export.json") } }
            : FakeHandler.Json("""[{"id":1}]"""));
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings
        {
            Url = "https://api.example.com/c", Auth = RestAuth.ApiKey, ApiKeyHeader = "X-Api-Token", Headers = "X-Tenant: 42",
        }, new SourceSecrets { ApiKey = "key-9" }));
        Assert.Single(records);
        Assert.Equal("key-9", kit.Handler.Requests[0].Request.Headers.GetValues("X-Api-Token").Single());
        var second = kit.Handler.Requests[1].Request;
        Assert.Equal("files.example.net", second.RequestUri!.Host);
        Assert.False(second.Headers.Contains("X-Api-Token"));
        Assert.False(second.Headers.Contains("X-Tenant"));
        Assert.Null(second.Headers.Authorization);
    }

    [Fact]
    public async Task A_redirect_that_never_ends_is_a_worded_error()
    {
        using var kit = new SourceTestKit(request => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = request.RequestUri } });
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "https://api.example.com/c" })));
        Assert.Equal(SourceHttp.MaxRedirects + 1, kit.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("https://api.example.com/a", "https://api.example.com/b?x=1", true)]
    [InlineData("https://api.example.com/a", "https://API.example.com/b", true)]
    [InlineData("http://api.example.com/a", "https://api.example.com/b", true)]
    [InlineData("https://api.example.com/a", "http://api.example.com/b", false)]
    [InlineData("https://api.example.com/a", "https://api.example.com:8443/b", false)]
    [InlineData("https://api.example.com/a", "https://other.example.com/b", false)]
    [InlineData("https://api.example.com/a", "https://api.example.com.evil.net/b", false)]
    public void Secrets_only_go_to_the_server_of_the_source(string origin, string address, bool trusted)
        => Assert.Equal(trusted, SourceHttp.IsTrusted(new Uri(origin), new Uri(address)));

    [Fact]
    public async Task A_next_link_that_points_back_does_not_loop()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("""{"items":[{"id":1}],"next":"https://api.example.com/c"}"""));
        var records = await SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings
        {
            Url = "https://api.example.com/c", ItemsPath = "items", Pagination = RestPagination.NextLink, NextLinkPath = "next",
        }));
        Assert.Single(records);
    }

    [Fact]
    public async Task The_safety_cap_stops_the_read_and_says_so()
    {
        using var kit = new SourceTestKit(_ => FakeHandler.Json("""[{"id":1},{"id":2},{"id":3}]"""));
        var connector = Connector(kit, new RestSettings { Url = "https://api.example.com/c", MaxRecords = 2 });
        Assert.Equal(2, (await SourceTestKit.ReadAllAsync(connector)).Count);
        Assert.True(connector.Truncated);
    }

    [Fact]
    public async Task An_answer_larger_than_the_cap_fails_before_it_is_read()
    {
        // the server says how long the answer is: it is not read at all
        using var kit = new SourceTestKit(_ => FakeHandler.Json("[" + string.Join(',', Enumerable.Range(1, 200).Select(i => $$"""{"id":{{i}}}""")) + "]"));
        var connector = new RestConnector(new RestSettings { Url = "https://api.example.com/c" }, new SourceSecrets(), kit.Env.Http(false), maxAnswerBytes: 1024);
        var ex = await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(connector));
        Assert.Equal(Text.T("Sources.Error.AnswerTooLarge", Ui.Bytes(1024)), ex.Message);
    }

    [Fact]
    public async Task An_answer_without_a_length_is_cut_off_at_the_cap()
    {
        // a body without Content-Length (chunked): the read stops at the cap, long before the end
        var stream = new EndlessJsonStream();
        using var kit = new SourceTestKit(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var connector = new RestConnector(new RestSettings { Url = "https://api.example.com/c" }, new SourceSecrets(), kit.Env.Http(false), maxAnswerBytes: 64 * 1024);
        var ex = await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(connector));
        Assert.Equal(Text.T("Sources.Error.AnswerTooLarge", Ui.Bytes(64 * 1024)), ex.Message);
        Assert.True(stream.Served < 1024 * 1024, $"{stream.Served} bytes read");
    }

    [Fact]
    public void The_cap_is_the_one_of_a_csv_file()
        => Assert.Equal(CsvConnector.MaxBytes, RestConnector.MaxAnswerBytes);

    /// <summary>A JSON array that never ends (<c>[{"pad":"xxxx…</c>) and has no length.</summary>
    private sealed class EndlessJsonStream : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var head = "[{\"pad\":\""u8;
            for (var i = 0; i < count; i++)
            {
                buffer[offset + i] = Served + i < head.Length ? head[(int)(Served + i)] : (byte)'x';
            }

            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_slow_server_runs_into_the_timeout()
    {
        var handler = new SlowHandler();
        var connector = new RestConnector(new RestSettings { Url = "https://api.example.com/c", TimeoutSeconds = 1 }, new SourceSecrets(), new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(connector));
        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_is_not_http_is_refused()
    {
        using var kit = new SourceTestKit();
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector(kit, new RestSettings { Url = "file:///etc/passwd" })));
        Assert.Empty(kit.Handler.Requests);
    }

    [Theory]
    [InlineData("<https://a/x?p=2>; rel=\"next\"", "https://a/x?p=2")]
    [InlineData("<https://a/1>; rel=\"prev\", <https://a/3>; rel=\"next\"", "https://a/3")]
    [InlineData("<https://a/1>; rel=prev", null)]
    public void The_link_header_is_read(string header, string? next)
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Link", header);
        Assert.Equal(next, RestConnector.NextOfLinkHeader(response));
    }

    /// <summary>A server that waits longer than the timeout (and ends when the request is cancelled).</summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return FakeHandler.Json("[]");
        }
    }
}
