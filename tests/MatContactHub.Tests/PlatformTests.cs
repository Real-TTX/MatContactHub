using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatContactHub.Data;
using MatContactHub.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatContactHub.Tests;

/// <summary>The description of MatContactHub for the platform (contract 2): valid, what it consumes, its launcher, quick actions and widgets, the public address fixed by MAT_PUBLIC_URL.</summary>
public sealed class PlatformTests
{
    private static readonly string Web = Path.Combine(Root(), "src", "MatContactHub", "wwwroot");

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid_with_the_icons_of_the_MatUi_sprite(string language)
    {
        var sprite = MatPlatformValidator.ReadSprite(Path.Combine(Web, "matui", "matui-icons.svg"));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatContactHubPlatform.Describe, language), sprite));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(o => MatContactHubPlatform.Describe(o, "https://contacts.example.com"), language), sprite));
    }

    [Fact]
    public void MatContactHub_provides_the_directory_consumes_sign_in_and_mail_and_contributes_a_launcher_and_widgets()
    {
        var definition = MatPlatformBuilder.Build(MatContactHubPlatform.Describe, "en");
        Assert.Equal(MatApiDefinition.ContractVersion, definition.Contract);
        Assert.Equal([Services.Contacts.DirectoryApi.Capability], definition.Provides.Select(p => p.Capability));
        Assert.Equal("contacts.directory", Services.Contacts.DirectoryApi.Capability);
        Assert.Equal([MatCapabilities.IdentityOidc, MatCapabilities.MailSmtp], definition.Consumes.Select(c => c.Capability));
        Assert.All(definition.Consumes, c => Assert.True(c.Optional));

        var contributes = definition.Contributes!;
        var launcher = Assert.Single(contributes.Launchers);
        Assert.Equal(("contacts", "/Contacts"), (launcher.Id, launcher.Url));
        Assert.True(File.Exists(Path.Combine(Web, launcher.Icon.TrimStart('/'))), launcher.Icon);
        Assert.Equal(["/Contacts/Edit", "/AddressBooks", "/Duplicates", null], contributes.QuickActions.Select(q => q.Url));
        Assert.Equal("syncAll", contributes.QuickActions[^1].Action);
        Assert.All(contributes.QuickActions, q => Assert.Equal("contacts", q.Launcher));

        // widgets only from existing values, only for administrators
        Assert.Equal(["directory", "syncRuns"], contributes.Widgets.Select(w => w.Id));
        Assert.All(contributes.Widgets, w => Assert.Equal(MatAudience.Admin, w.Audience));
        Assert.Contains(definition.Actions, a => a.Id == "openConnections" && a.Url == "/Admin/Connections");
        Assert.Contains(definition.Actions, a => a.Id == "syncAll");
    }

    [Fact]
    public void MAT_PUBLIC_URL_fixes_the_public_address()
    {
        Assert.Null(MatPlatformBuilder.Build(MatContactHubPlatform.Describe, "en").Configure.Single(f => f.Key == "publicUrl").Fixed);
        var field = MatPlatformBuilder.Build(o => MatContactHubPlatform.Describe(o, "https://contacts.example.com"), "en").Configure.Single(f => f.Key == "publicUrl");
        Assert.True(field.Fixed);
        Assert.Contains("MAT_PUBLIC_URL", field.Hint, StringComparison.Ordinal);
    }

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatContactHub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatContactHub.slnx not found.");
    }
}

/// <summary>MatContactHub in memory with the variables of a broker: the platform endpoints, the fixed public address, the health.</summary>
public sealed class PlatformAppTests : IAsyncLifetime
{
    private const string ApiKey = "test-api-key-0123456789";
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "matcontacthub-tests", Guid.NewGuid().ToString("N")[..12]);

    public async Task InitializeAsync()
    {
        if (TestDatabase.Admin is null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        Directory.CreateDirectory(_data);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:Default", _database.Db.Database.GetConnectionString());
            web.UseSetting("DataDirectory", _data);
            web.UseSetting("Logging:LogLevel:Default", "Warning");
            web.UseSetting("MAT_API_KEY", ApiKey);
            web.UseSetting("MAT_PUBLIC_URL", "https://contacts.example.com/");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private HttpClient Api()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    [DbFact]
    public async Task The_variables_of_a_broker_fix_the_public_address()
    {
        using var api = Api();
        var definition = await api.GetFromJsonAsync<JsonElement>("/api");
        Assert.Equal(2, definition.GetProperty("contract").GetInt32());
        Assert.Equal("contacts", definition.GetProperty("contributes").GetProperty("launchers")[0].GetProperty("id").GetString());
        var publicUrl = definition.GetProperty("configure").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "publicUrl");
        Assert.True(publicUrl.GetProperty("fixed").GetBoolean());

        var values = (await api.GetFromJsonAsync<JsonElement>("/api/configure")).GetProperty("values");
        Assert.Equal("https://contacts.example.com", values.GetProperty("publicUrl").GetString());
        var refused = await api.PutAsync("/api/configure", new StringContent("""{"values":{"publicUrl":"https://other.example.com"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var changed = await api.PutAsync("/api/configure", new StringContent("""{"values":{"defaultCountry":"AT"}}""", Encoding.UTF8, "application/json"));
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        Assert.Equal("AT", (await api.GetFromJsonAsync<JsonElement>("/api/configure")).GetProperty("values").GetProperty("defaultCountry").GetString());
    }

    [DbFact]
    public async Task The_directory_API_wants_the_key_of_a_binding()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/api/directory/search?q=a", "/api/provides/contacts.directory/lookup?number=030123" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "mat_wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
    }

    [DbFact]
    public async Task The_health_and_the_state_answer()
    {
        using var api = Api();
        var health = await api.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Contains(health.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "sync");
        var state = await api.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Equal(JsonValueKind.Object, state.ValueKind);
    }
}
