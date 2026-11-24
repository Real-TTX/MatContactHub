using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatContactHub.Tests;

/// <summary>
/// The directory API of <c>contacts.directory</c> in the running app: the key of a binding (401 without), the caller-ID lookup in every spelling of a number, the
/// search with its limit; only address books shared with apps, never a hidden contact.
/// </summary>
public sealed class DirectoryApiTests : IAsyncLifetime
{
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private string _key = string.Empty;
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
        });

        var db = _database.Db;
        var apps = new AddressBook { Name = "Customers", LdapName = "customers", AppsEnabled = true, SortOrder = 1 };
        var staff = new AddressBook { Name = "Staff", LdapName = "staff", AppsEnabled = true, SortOrder = 0 };
        var secret = new AddressBook { Name = "Secret", LdapName = "secret", AppsEnabled = false };
        db.AddressBooks.AddRange(apps, staff, secret);
        await db.SaveChangesAsync();
        db.Contacts.AddRange(
            new Contact { AddressBookId = apps.Id, DisplayName = "Max Mustermann", FirstName = "Max", LastName = "Mustermann", Company = "ACME", PhoneMobile = "0171 1234567", PhoneBusiness = "+49 30 1234567", Email = "max@acme.example" },
            new Contact { AddressBookId = staff.Id, DisplayName = "Switchboard", Company = "MatContactHub", PhoneOther = "030 1234567" },
            new Contact { AddressBookId = apps.Id, DisplayName = "Hidden Person", PhoneBusiness = "040 555555", Hidden = true },
            new Contact { AddressBookId = secret.Id, DisplayName = "Secret Person", PhoneBusiness = "089 777777", Company = "ACME" });
        for (var i = 0; i < 120; i++)
        {
            db.Contacts.Add(new Contact { AddressBookId = apps.Id, DisplayName = $"Bulk {i:000}", Company = "Bulk Ltd" });
        }

        await db.SaveChangesAsync();
        var (_, key) = await _factory.Services.GetRequiredService<MatBindingStore>()
            .UpsertProvidedAsync(DirectoryApi.Capability, new MatBindingParty("matphone", "MatPhone"), "test", renew: true);
        _key = key!;
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

    private HttpClient Client(string? key)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    [DbFact]
    public async Task Without_the_key_of_a_binding_every_call_answers_401()
    {
        foreach (var key in new[] { null, "mat_wrong", "" })
        {
            using var client = Client(key);
            foreach (var path in new[] { "/api/directory/lookup?number=0301234567", "/api/directory/search?q=max", "/api/provides/contacts.directory/search?q=max" })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            }
        }
    }

    [ContactDbTheory]
    [InlineData("+491711234567", "Max Mustermann", "mobile")]
    [InlineData("0171 1234567", "Max Mustermann", "mobile")]
    [InlineData("00491711234567", "Max Mustermann", "mobile")]
    [InlineData("0301234567", "Max Mustermann", "business")]
    public async Task A_number_finds_its_contact_however_it_is_written(string number, string name, string type)
    {
        using var client = Client(_key);
        foreach (var prefix in new[] { "/api/directory", "/api/provides/contacts.directory" })
        {
            var hit = await client.GetFromJsonAsync<JsonElement>($"{prefix}/lookup?number={Uri.EscapeDataString(number)}");
            Assert.Equal(name, hit.GetProperty("displayName").GetString());
            Assert.Equal(type, hit.GetProperty("numberType").GetString());
            Assert.Equal("Customers", hit.GetProperty("addressBook").GetString());
            Assert.Equal("ACME", hit.GetProperty("company").GetString());
            Assert.True(Guid.TryParse(hit.GetProperty("id").GetString(), out _));
        }
    }

    [DbFact]
    public async Task Hidden_contacts_and_address_books_not_shared_with_apps_are_never_found()
    {
        using var client = Client(_key);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/directory/lookup?number=040555555")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/directory/lookup?number=089777777")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/directory/lookup?number=0899999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/directory/lookup")).StatusCode);

        var items = (await client.GetFromJsonAsync<JsonElement>("/api/directory/search?q=person")).GetProperty("items");
        Assert.Equal(0, items.GetArrayLength());
        var acme = (await client.GetFromJsonAsync<JsonElement>("/api/directory/search?q=acme")).GetProperty("items");
        Assert.Equal(["Max Mustermann"], acme.EnumerateArray().Select(i => i.GetProperty("displayName").GetString()));
    }

    [DbFact]
    public async Task The_search_finds_names_and_numbers_and_keeps_to_its_limit()
    {
        using var client = Client(_key);
        var byNumber = (await client.GetFromJsonAsync<JsonElement>("/api/directory/search?q=030%201234567")).GetProperty("items");
        Assert.Equal(["Max Mustermann", "Switchboard"], byNumber.EnumerateArray().Select(i => i.GetProperty("displayName").GetString()));
        var phones = byNumber[0].GetProperty("phones").EnumerateArray().Select(p => (p.GetProperty("type").GetString(), p.GetProperty("e164").GetString())).ToList();
        Assert.Equal([("business", "+49301234567"), ("mobile", "+491711234567")], phones);

        Assert.Equal(DirectoryApi.DefaultLimit, (await client.GetFromJsonAsync<JsonElement>("/api/directory/search?q=bulk")).GetProperty("items").GetArrayLength());
        Assert.Equal(5, (await client.GetFromJsonAsync<JsonElement>("/api/directory/search?q=bulk&limit=5")).GetProperty("items").GetArrayLength());
        Assert.Equal(DirectoryApi.MaxLimit, (await client.GetFromJsonAsync<JsonElement>("/api/directory/search?q=bulk&limit=1000")).GetProperty("items").GetArrayLength());
    }

    [DbFact]
    public async Task With_two_hits_the_business_number_wins_over_another_one()
    {
        // the business number of Max and the "other" number of the switchboard are the same: the business one wins, whatever the order of the address books
        var hit = await DirectoryApi.FindAsync(_database!.Db, Actor.ForConsumer("matphone"), "+49 30 1234567");
        Assert.Equal(("Max Mustermann", "business"), (hit!.DisplayName, hit.NumberType));
    }
}
