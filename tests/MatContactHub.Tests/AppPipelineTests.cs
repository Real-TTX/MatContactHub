using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MatContactHub.Tests;

/// <summary>
/// The pipeline of the app: the address of a visitor comes from <c>X-Forwarded-For</c> only through a trusted proxy (it keys the rate limits and the sessions),
/// and the text files say that they are UTF-8.
/// </summary>
public sealed class AppPipelineTests
{
    private static async Task<IPAddress?> RemoteAsync(string? setting, string socket, string forwardedFor)
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxies.Parse(setting).Apply(options);
        IPAddress? seen = null;
        var middleware = new ForwardedHeadersMiddleware(http => { seen = http.Connection.RemoteIpAddress; return Task.CompletedTask; }, NullLoggerFactory.Instance,
            Options.Create(options));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(socket);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        await middleware.Invoke(context);
        return seen;
    }

    [Theory]
    [InlineData(null, "198.51.100.20", "203.0.113.7", "198.51.100.20")] // a client on the internet chooses nothing
    [InlineData(null, "10.253.87.9", "203.0.113.7", "10.253.87.9")] // nor one in a private network that is not named
    [InlineData(null, "127.0.0.1", "203.0.113.7", "203.0.113.7")] // a proxy on the same host
    [InlineData("10.253.87.0/24", "10.253.87.9", "203.0.113.7", "203.0.113.7")] // the network of the proxies
    [InlineData("10.253.87.0/24", "10.253.88.9", "203.0.113.7", "10.253.88.9")] // outside it
    [InlineData("172.18.0.5, 172.18.0.6", "172.18.0.6", "203.0.113.7", "203.0.113.7")] // a proxy by its address
    [InlineData("10.253.87.0/24", "10.253.87.9", "198.51.100.1, 203.0.113.7", "203.0.113.7")] // only what the proxy added counts, not what the client sent
    public async Task Only_a_trusted_proxy_sets_the_address_of_the_visitor(string? setting, string socket, string forwardedFor, string expected)
        => Assert.Equal(IPAddress.Parse(expected), await RemoteAsync(setting, socket, forwardedFor));

    [Fact]
    public void The_setting_takes_addresses_and_networks_and_names_what_it_cannot_read()
    {
        var proxies = TrustedProxies.Parse(" 10.0.0.0/8;172.18.0.5 fd00::/8, proxy.local\n::1 ");
        Assert.Equal([IPAddress.Parse("172.18.0.5"), IPAddress.IPv6Loopback], proxies.Addresses);
        Assert.Equal([System.Net.IPNetwork.Parse("10.0.0.0/8"), System.Net.IPNetwork.Parse("fd00::/8")], proxies.Networks);
        Assert.Equal(["proxy.local"], proxies.Invalid);
        Assert.Empty(TrustedProxies.Parse(null).Invalid);
    }

    [DbFact]
    public async Task The_text_files_are_utf8()
    {
        await using var database = await TestDatabase.CreateAsync();
        var data = Path.Combine(Path.GetTempPath(), "matcontacthub-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(data);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Development");
                web.UseSetting("ConnectionStrings:Default", database.Db.Database.GetConnectionString());
                web.UseSetting("DataDirectory", data);
                web.UseSetting("Logging:LogLevel:Default", "Warning");
            });
            var client = factory.CreateClient();
            foreach (var path in new[] { "/llms.txt", "/robots.txt" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            }
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
