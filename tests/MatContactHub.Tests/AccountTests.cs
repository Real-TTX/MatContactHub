using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Accounts;
using MatContactHub.Services.Auth;
using MatPlatform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MatContactHub.Tests;

/// <summary>
/// The business layer of the accounts and the installation: only administrators manage accounts, the settings and the housekeeping, the last administrator
/// stays, a visitor registers only while the registration is open, a person exports, erases and signs out only themselves, the own changes fill the audit columns.
/// </summary>
public sealed class AccountTests
{
    private static readonly PasswordService Passwords = new();

    private static readonly MatPlatformSettings Platform = MatPlatformSettings.From(new ConfigurationBuilder().Build(), "matcontacthub");

    private static Actor Person(User user) => Actor.ForPerson(user.Id, user.Role == UserRole.Admin, user.Email);

    private static readonly UserCriteria All = new(null, null, null, "name", false, 1, 25);

    [DbFact]
    public async Task OnlyAnAdministratorListsAndReadsAccounts()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");

        Assert.Null(await UserAdmin.ListAsync(t.Db, bob.Id, All));
        Assert.Null(await UserAdmin.GetAsync(t.Db, bob.Id, admin.Id));

        var list = await UserAdmin.ListAsync(t.Db, admin.Id, All);
        Assert.NotNull(list);
        Assert.Equal(2, list.Total);
        Assert.Equal("Ada", (await UserAdmin.GetAsync(t.Db, admin.Id, admin.Id))!.DisplayName);
    }

    [DbFact]
    public async Task APersonCannotChangeOrDeleteOtherAccounts()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");

        var saved = await UserAdmin.UpdateAsync(t.Db, new PasswordService(), bob.Id, admin.Id, new UserInput("Hacked", "ada@example.com", UserRole.User, false, string.Empty));
        Assert.True(saved.Denied);
        Assert.Equal(UserDeleted.Denied, await UserAdmin.DeleteAsync(t.Db, bob.Id, admin.Id));

        t.Db.ChangeTracker.Clear();
        Assert.Equal("Ada", (await UserAdmin.GetAsync(t.Db, admin.Id, admin.Id))!.DisplayName);
    }

    [DbFact]
    public async Task TheLastAdministratorKeepsTheRole()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);

        var saved = await UserAdmin.UpdateAsync(t.Db, new PasswordService(), admin.Id, admin.Id, new UserInput("Ada", "ada@example.com", UserRole.User, true, string.Empty));
        Assert.Contains(saved.Errors, e => e.Field == nameof(UserInput.Role));
        Assert.Equal(UserDeleted.CannotDelete, await UserAdmin.DeleteAsync(t.Db, admin.Id, admin.Id));
    }

    [DbFact]
    public async Task TheProfileIsChecked()
    {
        await using var t = await TestDatabase.CreateAsync();
        var bob = await t.AddUserAsync("Bob");

        var (none, errors) = await Profiles.UpdateAsync(t.Db, bob.Id, new ProfileInput(" ", string.Empty, "Nowhere/City", "standard"), mailConfigured: false);
        Assert.Null(none);
        Assert.Equal(2, errors.Count);

        var (saved, _) = await Profiles.UpdateAsync(t.Db, bob.Id, new ProfileInput(" Robert ", "de", "Europe/Berlin", "oled"), mailConfigured: false);
        Assert.Equal("Robert", saved!.DisplayName);
        Assert.Equal("oled", saved.Scheme);
        Assert.False(await Profiles.SetThemeAsync(t.Db, bob.Id, "pink"));
    }

    [DbFact]
    public async Task A_visitor_registers_only_while_the_registration_is_open_and_never_chooses_a_role()
    {
        await using var t = await TestDatabase.CreateAsync();
        var first = await AccountService.RegisterAsync(t.Db, Actor.Anonymous, Passwords, new NewAccount("ada@example.com", "Ada", "a long password 1"));
        Assert.Equal(UserRole.Admin, first.User!.Role);

        // closed (the default after the first account): a visitor gets nothing, an administrator creates any account
        Assert.False(await AccountService.RegistrationOpenAsync(t.Db));
        Assert.Null((await AccountService.RegisterAsync(t.Db, Actor.Anonymous, Passwords, new NewAccount("bob@example.com", "Bob", "a long password 2"))).User);
        Assert.NotNull((await AccountService.RegisterAsync(t.Db, Person(first.User), Passwords,
            new NewAccount("bob@example.com", "Bob", "a long password 2", Role: UserRole.Admin))).User);

        (await SettingsStore.LoadAsync(t.Db)).RegistrationOpen = true;
        await t.Db.SaveChangesAsync();
        Assert.Null((await AccountService.RegisterAsync(t.Db, Actor.Anonymous, Passwords,
            new NewAccount("eve@example.com", "Eve", "a long password 3", Role: UserRole.Admin))).User);
        var eve = await AccountService.RegisterAsync(t.Db, Actor.Anonymous, Passwords, new NewAccount("eve@example.com", "Eve", "a long password 3"));
        Assert.Equal(UserRole.User, eve.User!.Role);
    }

    [DbFact]
    public async Task A_person_exports_and_erases_only_their_own_account_and_the_last_administrator_stays()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");
        var eve = await t.AddUserAsync("Eve");

        Assert.Null(await AccountDataService.ExportAsync(t.Db, Person(eve), bob.Id));
        Assert.NotNull(await AccountDataService.ExportAsync(t.Db, Person(bob), bob.Id));
        Assert.True((await AccountDataService.EraseAsync(t.Db, Person(eve), bob.Id)).NotAllowed);
        Assert.Equal(Text.T("Data.Delete.LastAdmin"), (await AccountDataService.EraseAsync(t.Db, Person(admin), admin.Id)).Error);

        Assert.True((await AccountDataService.EraseAsync(t.Db, Person(bob), bob.Id)).Ok);
        Assert.True((await AccountDataService.EraseAsync(t.Db, Person(admin), eve.Id)).Ok);
        t.Db.ChangeTracker.Clear();
        Assert.Equal(["Ada"], await t.Db.Users.IgnoreQueryFilters().Select(u => u.DisplayName).ToListAsync());
    }

    [DbFact]
    public async Task Only_the_person_or_an_administrator_ends_the_sessions_of_an_account()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");
        var eve = await t.AddUserAsync("Eve");
        t.Db.Sessions.Add(new UserSession { UserId = bob.Id, Token = Guid.NewGuid(), ExpiresDate = DateTime.UtcNow.AddDays(1), LastSeenDate = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        Assert.False(await UserSessions.RevokeAllAsync(t.Db, Person(eve), bob.Id));
        Assert.False(await t.Db.Sessions.AnyAsync(s => s.IsRevoked));
        Assert.True(await UserSessions.RevokeAllAsync(t.Db, Person(admin), bob.Id));
        Assert.True(await t.Db.Sessions.AllAsync(s => s.IsRevoked));
    }

    [DbFact]
    public async Task Only_an_administrator_reads_and_changes_the_installation_and_every_change_is_in_the_log()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");

        Assert.Null(await InstanceService.SnapshotAsync(t.Db, Person(bob)));
        Assert.Null(await InstanceService.SettingsAsync(t.Db, Person(bob), Platform));
        Assert.Null(await InstanceService.CleanUpAsync(t.Db, Person(bob)));
        Assert.Equal(string.Empty, (await InstanceService.SaveSettingsAsync(t.Db, Person(bob), Platform, new SettingsChange(RegistrationOpen: true)))!.Field);
        Assert.False((await SettingsStore.LoadAsync(t.Db)).RegistrationOpen);

        // a change through the platform API is in the name of its caller; one entry per setting that changed (the country stays as it is: no entry)
        var mattiverse = Actor.System("mattiverse");
        Assert.Null(await InstanceService.SaveSettingsAsync(t.Db, mattiverse, Platform, new SettingsChange(RegistrationOpen: true, DefaultCountry: "de", SyncRunDays: 30)));
        Assert.Null(await InstanceService.SaveSettingsAsync(t.Db, Person(admin), Platform, new SettingsChange(PublicUrl: "https://contacts.example.com/")));
        var settings = (await InstanceService.SettingsAsync(t.Db, Person(admin), Platform))!;
        Assert.Equal((true, "https://contacts.example.com", 30), (settings.RegistrationOpen, settings.PublicUrl, settings.SyncRunDays));

        // worded in the language of the reader
        System.Globalization.CultureInfo.CurrentUICulture = Languages.UiCulture("en");
        var rows = (await ActivityLog.AllAsync(t.Db, admin.Id, null, "setting", 1, 25))!.Value.Rows;
        Assert.Equal(
            [("Ada", "Public address", "https://contacts.example.com"), ("mattiverse", "Keep the history of the syncs (days)", "30"), ("mattiverse", "Registration open", "on")],
            rows.Select(r => (r.Who, r.Target, r.Detail)).OrderBy(r => r.Who).ThenBy(r => r.Target));
        Assert.NotNull(await InstanceService.SnapshotAsync(t.Db, mattiverse));
        Assert.NotNull(await InstanceService.CleanUpAsync(t.Db, Actor.System("maintenance")));
    }

    [DbFact]
    public async Task The_theme_and_the_language_of_a_person_fill_the_audit_columns()
    {
        await using var t = await TestDatabase.CreateAsync();
        var bob = await t.AddUserAsync("Bob");
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.True(await Profiles.SetThemeAsync(t.Db, bob.Id, "dark"));
        Assert.True(await Profiles.SetLanguageAsync(t.Db, bob.Id, "de"));
        Assert.False(await Profiles.SetLanguageAsync(t.Db, 99_999, "de"));
        t.Db.ChangeTracker.Clear();
        var user = await t.Db.Users.SingleAsync(u => u.Id == bob.Id);
        Assert.Equal(("dark", "de", (long?)bob.Id), (user.Theme, user.Language, user.UpdateUserId));
        Assert.True(user.UpdateDate >= before);
    }
}
