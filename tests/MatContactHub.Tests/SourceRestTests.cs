using System.Net;
using System.Text;
using System.Web;
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
