using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using MatContactHub.Services.Ldap;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Novell.Directory.Ldap;
using LdapClient = Novell.Directory.Ldap.LdapConnection;
using LdapEntry = Novell.Directory.Ldap.LdapEntry;

namespace MatContactHub.Tests;

/// <summary>
/// The LDAP server of the app end to end: the hosted service on a database of its own and on ports the system chose, a real client. What an account may see,
/// hidden and deleted contacts, a change that arrives through the snapshot, the self-signed certificate made at the first start, the settings that switch it off.
/// </summary>
public sealed class LdapHostTests
{
    private static readonly Actor Admin = Actor.System("test");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(TestDatabase database, ServiceProvider services, LdapServerHost host, string folder)
        {
            Database = database;
            _services = services;
            Host = host;
            DataFolder = folder;
        }

        public TestDatabase Database { get; }

        public AppDbContext Db => Database.Db;

        public LdapServerHost Host { get; }

        public string DataFolder { get; }

        public IDataProtectionProvider Protection => _services.GetRequiredService<IDataProtectionProvider>();

        public static async Task<Fixture> StartAsync(Func<AppDbContext, Task> seed)
        {
            var database = await TestDatabase.CreateAsync();
            await seed(database.Db);
            var folder = Path.Combine(Path.GetTempPath(), "mch-ldap-" + Guid.NewGuid().ToString("N")[..8]);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataDirectory"] = folder,
                ["Ldap:Port"] = "0",
                ["Ldap:SecurePort"] = "0",
                ["Ldap:Address"] = "127.0.0.1",
                ["Ldap:BindFailureDelaySeconds"] = "0",
            }).Build();
            var connection = database.Db.Database.GetConnectionString()!;
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(configuration)
                .AddDbContext<AppDbContext>(o => o.UseNpgsql(connection))
                .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
                .AddSingleton<PasswordService>()
                .AddSingleton<LdapQueryLog>()
                .AddSingleton<LdapServerHost>()
                .BuildServiceProvider();
            var host = services.GetRequiredService<LdapServerHost>();
            await host.StartAsync(CancellationToken.None);
            var fixture = new Fixture(database, services, host, folder);
            await fixture.SettledAsync();
            return fixture;
        }

        /// <summary>Waits until the server has a snapshot of the current version and its listeners are as the settings say.</summary>
        public async Task SettledAsync()
        {
            Assert.True(await Host.WaitForVersionAsync(DirectoryChanges.Version, TimeSpan.FromSeconds(20)), "the snapshot did not follow the change");
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Host.Directory.Settings.Enabled && (Host.Port is null || Host.SecurePort is null) && DateTime.UtcNow < until)
            {
                await Task.Delay(20);
            }
        }

        public async Task<LdapClient> ConnectAsync(string? name = "starface", string password = LdapTestData.StarfacePassword)
        {
            var client = new LdapClient();
            await client.ConnectAsync("127.0.0.1", Host.Port!.Value);
            if (name is not null)
            {
                await client.BindAsync($"cn={name},{LdapTestData.Base}", password);
            }

            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
            await Database.DisposeAsync();
            try
            {
                Directory.Delete(DataFolder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task SeedAsync(AppDbContext db)
    {
        var staff = new AddressBook { Name = "Staff", LdapName = "staff", LdapEnabled = true };
        var customers = new AddressBook { Name = "Customers", LdapName = "customers", LdapEnabled = true };
        var secret = new AddressBook { Name = "Private", LdapName = "private", LdapEnabled = false };
        db.AddressBooks.AddRange(staff, customers, secret);
        await db.SaveChangesAsync();
        db.Contacts.AddRange(
            new Contact { Token = LdapTestData.Ann, AddressBookId = staff.Id, FirstName = "Ann", LastName = "Example", DisplayName = "Ann Example", PhoneBusiness = "+49 30 1234567" },
            new Contact { Token = LdapTestData.Bob, AddressBookId = customers.Id, FirstName = "Bob", LastName = "Builder", DisplayName = "Bob Builder", PhoneMobile = "0171 7654321" },
            new Contact { AddressBookId = customers.Id, FirstName = "Hank", LastName = "Hidden", DisplayName = "Hank Hidden", Hidden = true, PhoneBusiness = "030 5555555" },
            new Contact { AddressBookId = customers.Id, FirstName = "Dora", LastName = "Deleted", DisplayName = "Dora Deleted", UpdateState = SoftEntity.Deleted },
            new Contact { AddressBookId = secret.Id, FirstName = "Pete", LastName = "Private", DisplayName = "Pete Private" });
        await db.SaveChangesAsync();
        var passwords = LdapTestData.Passwords;
        Assert.True((await LdapAccountService.SaveAsync(db, Admin, passwords, null, new LdapAccountForm("STARFACE", null, true, true, []), LdapTestData.StarfacePassword)).Ok);
        Assert.True((await LdapAccountService.SaveAsync(db, Admin, passwords, null, new LdapAccountForm("phone", null, true, false, [customers.Id]), LdapTestData.PhonePassword)).Ok);
    }

    private static async Task<List<string>> NamesAsync(LdapClient client, string filter = "(objectClass=person)")
    {
        var results = await client.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, filter, ["cn"], false);
        var names = new List<string>();
        while (await results.HasMoreAsync())
        {
            LdapEntry entry = await results.NextAsync();
            names.Add(entry.Get("cn").StringValue);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    [DbFact]
    public async Task Accounts_see_their_address_books_and_never_hidden_deleted_or_unshared_contacts()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        using (var starface = await f.ConnectAsync())
        {
            Assert.Equal(["Ann Example", "Bob Builder"], await NamesAsync(starface));
            Assert.Empty(await NamesAsync(starface, "(telephoneNumber=*5555555)"));
        }

        using (var phone = await f.ConnectAsync("phone", LdapTestData.PhonePassword))
        {
            Assert.Equal(["Bob Builder"], await NamesAsync(phone));
            Assert.Equal(["Bob Builder"], await NamesAsync(phone, "(mobile=+491717654321)"));
        }
    }

    [DbFact]
    public async Task A_change_of_the_contacts_reaches_the_server()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        var bob = await f.Db.Contacts.FirstAsync(c => c.Token == LdapTestData.Bob);
        bob.Hidden = true;
        await f.Db.SaveChangesAsync();
        await f.SettledAsync();
        using (var client = await f.ConnectAsync())
        {
            Assert.Equal(["Ann Example"], await NamesAsync(client));
        }

        // an address book that is switched on for LDAP appears
        var secret = await f.Db.AddressBooks.FirstAsync(b => b.LdapName == "private");
        secret.LdapEnabled = true;
        await f.Db.SaveChangesAsync();
        await f.SettledAsync();
        using (var client = await f.ConnectAsync())
        {
            Assert.Equal(["Ann Example", "Pete Private"], await NamesAsync(client));
        }
    }

    [DbFact]
    public async Task Anonymous_searches_follow_the_setting()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        using (var client = await f.ConnectAsync(name: null))
        {
            var e = await Assert.ThrowsAsync<LdapException>(() => NamesAsync(client));
            Assert.Equal(LdapException.InsufficientAccessRights, e.ResultCode);
        }

        var settings = await LdapSettingsService.LoadAsync(f.Db);
        Assert.True((await LdapSettingsService.SaveAsync(f.Db, Admin, settings with { AllowAnonymous = true })).Ok);
        await f.SettledAsync();
        using (var client = await f.ConnectAsync(name: null))
        {
            Assert.Equal(["Ann Example", "Bob Builder"], await NamesAsync(client));
        }
    }

    [DbFact]
    public async Task Binds_are_noted_and_failed_ones_reach_the_activity()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        using (await f.ConnectAsync())
        {
        }

        using (var bad = await f.ConnectAsync(name: null))
        {
            await Assert.ThrowsAsync<LdapException>(() => bad.BindAsync("cn=starface," + LdapTestData.Base, "wrong password"));
        }

        var until = DateTime.UtcNow.AddSeconds(10);
        bool done;
        do
        {
            await Task.Delay(50);
            f.Db.ChangeTracker.Clear();
            done = await f.Db.LdapAccounts.AnyAsync(a => a.Name == "starface" && a.LastBindDate != null)
                   && await f.Db.Activities.AnyAsync(a => a.Action == "ldap" && a.Detail!.StartsWith("failed bind"));
        }
        while (!done && DateTime.UtcNow < until);

        Assert.True(done);
    }

    [DbFact]
    public async Task StartTls_and_Ldaps_use_the_self_signed_certificate_made_at_the_first_start()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        Assert.True(File.Exists(Path.Combine(f.DataFolder, LdapSettings.Folder, LdapCertificateService.SelfSignedFile)));
        var thumbprint = (await SettingsStore.LoadAsync(f.Db)).LdapCertificateThumbprint;
        Assert.Equal(f.Host.Certificate!.Thumbprint, thumbprint);

        string? seen = null;
        using (var tls = new LdapClient(new LdapConnectionOptions().ConfigureRemoteCertificateValidationCallback((_, cert, _, _) =>
               {
                   seen = cert?.GetCertHashString();
                   return true;
               })))
        {
            await tls.ConnectAsync("127.0.0.1", f.Host.Port!.Value);
            await tls.StartTlsAsync();
            await tls.BindAsync("starface", LdapTestData.StarfacePassword);
            Assert.Equal(["Ann Example", "Bob Builder"], await NamesAsync(tls));
        }

        Assert.Equal(thumbprint, seen);
        using (var ldaps = new LdapClient(new LdapConnectionOptions().UseSsl().ConfigureRemoteCertificateValidationCallback((_, cert, _, _) => cert?.GetCertHashString() == thumbprint)))
        {
            await ldaps.ConnectAsync("127.0.0.1", f.Host.SecurePort!.Value);
            await ldaps.BindAsync("starface", LdapTestData.StarfacePassword);
            Assert.Equal(["Bob Builder"], await NamesAsync(ldaps, "(mobile=*1717654321)"));
        }

        // a new certificate is taken without a restart
        var folder = Path.Combine(f.DataFolder, LdapSettings.Folder);
        var renewed = await LdapCertificateService.RegenerateAsync(f.Db, Admin, f.Protection, folder);
        Assert.True(renewed.Ok);
        Assert.NotEqual(thumbprint, renewed.Value!.Thumbprint);
        await f.SettledAsync();
        Assert.Equal(renewed.Value.Thumbprint, f.Host.Certificate!.Thumbprint);
    }

    [DbFact]
    public async Task Switching_the_server_off_closes_the_ports()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        var port = f.Host.Port!.Value;
        var settings = await LdapSettingsService.LoadAsync(f.Db);
        Assert.True((await LdapSettingsService.SaveAsync(f.Db, Admin, settings with { LdapsEnabled = false })).Ok);
        await f.SettledAsync();
        Assert.Null(f.Host.SecurePort);
        Assert.Equal(port, f.Host.Port);

        Assert.True((await LdapSettingsService.SaveAsync(f.Db, Admin, settings with { Enabled = false })).Ok);
        await f.SettledAsync();
        Assert.Null(f.Host.Port);
        using var client = new LdapClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync("127.0.0.1", port));
    }

    [DbFact]
    public async Task The_state_and_the_log_are_for_administrators()
    {
        await using var f = await Fixture.StartAsync(SeedAsync);
        Assert.Null(f.Host.Status(Actor.ForPerson(1, false, "x")));
        var status = f.Host.Status(Admin)!;
        Assert.Equal(2, status.AddressBooks);
        Assert.Equal(2, status.Contacts);
        Assert.Equal(2, status.Accounts);
        Assert.True(status.HasCertificate);
    }
}
