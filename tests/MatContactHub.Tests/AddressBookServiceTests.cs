using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>The save of an address book: the LDAP name without case (LDAP compares ou values without case) and the colour kept by a caller without it.</summary>
public sealed class AddressBookServiceTests
{
    private static readonly Actor Admin = Actor.System("test");

    private static AddressBookForm Form(string name, string? ldapName = null, string? color = null) => new(name, null, ldapName ?? string.Empty, true, false, false, color, 0);

    [DbFact]
    public async Task The_LDAP_name_is_kept_in_small_letters_and_unique_without_case()
    {
        await using var t = await TestDatabase.CreateAsync();
        var first = await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Kunden", "Kunden"));
        Assert.True(first.Ok);
        Assert.Equal("kunden", (await AddressBookService.GetAsync(t.Db, Admin, first.Value))!.LdapName);

        var second = await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Other customers", "KUNDEN"));
        Assert.False(second.Ok);
        Assert.False(second.NotAllowed);
        Assert.Equal(1, await t.Db.AddressBooks.CountAsync());
    }

    [DbFact]
    public async Task The_database_refuses_LDAP_names_with_capital_letters()
    {
        await using var t = await TestDatabase.CreateAsync();
        t.Db.AddressBooks.Add(new AddressBook { Name = "Staff", LdapName = "Staff" });
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());

        t.Db.ChangeTracker.Clear();
        t.Db.LdapAccounts.Add(new LdapAccount { Name = "STARFACE", PasswordHash = "x" });
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [DbFact]
    public async Task A_save_without_a_colour_keeps_it_and_an_empty_one_clears_it()
    {
        await using var t = await TestDatabase.CreateAsync();
        var id = (await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Staff", color: "#0F766E"))).Value;
        Assert.Equal("#0f766e", (await AddressBookService.GetAsync(t.Db, Admin, id))!.Color);

        Assert.True((await AddressBookService.SaveAsync(t.Db, Admin, id, Form("Staff", color: null))).Ok);
        Assert.Equal("#0f766e", (await AddressBookService.GetAsync(t.Db, Admin, id))!.Color);

        Assert.True((await AddressBookService.SaveAsync(t.Db, Admin, id, Form("Staff", color: string.Empty))).Ok);
        Assert.Null((await AddressBookService.GetAsync(t.Db, Admin, id))!.Color);
    }

    [DbFact]
    public async Task The_roles_of_the_tab_Access_replace_the_old_ones_and_give_the_rights()
    {
        await using var t = await TestDatabase.CreateAsync();
        var vera = await t.AddUserAsync("Vera");
        var ed = await t.AddUserAsync("Ed");
        var stan = await t.AddUserAsync("Stan");
        var id = (await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Customers") with
        {
            Access = [new(vera.Id, AddressBookRole.Viewer), new(ed.Id, AddressBookRole.Editor)],
        })).Value;

        Assert.Equal(AddressBookRole.Viewer, await AddressBookService.RoleAsync(t.Db, Person(vera), id));
        Assert.Equal(AddressBookRole.Editor, await AddressBookService.RoleAsync(t.Db, Person(ed), id));
        Assert.Equal(AddressBookRole.None, await AddressBookService.RoleAsync(t.Db, Person(stan), id));
        Assert.Equal(2, (await AddressBookService.FactsAsync(t.Db, Admin, id))!.People.Count);

        // a save without the tab keeps the roles; a list replaces them; shared makes everybody a viewer
        Assert.True((await AddressBookService.SaveAsync(t.Db, Admin, id, Form("Customers"))).Ok);
        Assert.Equal(2, (await AddressBookService.GetAsync(t.Db, Admin, id))!.Access!.Count);
        Assert.True((await AddressBookService.SaveAsync(t.Db, Admin, id, Form("Customers") with { IsShared = true, Access = [new(vera.Id, AddressBookRole.Editor)] })).Ok);
        Assert.Equal(AddressBookRole.Editor, await AddressBookService.RoleAsync(t.Db, Person(vera), id));
        Assert.Equal(AddressBookRole.Viewer, await AddressBookService.RoleAsync(t.Db, Person(ed), id));
        Assert.Equal(AddressBookRole.Viewer, await AddressBookService.RoleAsync(t.Db, Person(stan), id));

        // nobody but an administrator changes an address book; an unknown person is refused
        Assert.True((await AddressBookService.SaveAsync(t.Db, Person(vera), id, Form("Mine"))).NotAllowed);
        Assert.Null(await AddressBookService.GetAsync(t.Db, Person(vera), id));
        Assert.Empty(await AddressBookService.PeopleAsync(t.Db, Person(vera)));
        Assert.Equal(3, (await AddressBookService.PeopleAsync(t.Db, Admin)).Count);
        var unknown = await AddressBookService.SaveAsync(t.Db, Admin, id, Form("Customers") with { Access = [new(999999, AddressBookRole.Viewer)] });
        Assert.False(unknown.Ok);
    }

    [DbFact]
    public async Task An_address_book_with_a_data_source_is_not_deleted_one_without_takes_its_contacts_along()
    {
        await using var t = await TestDatabase.CreateAsync();
        var synced = (await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Synced"))).Value;
        var manual = (await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Manual"))).Value;
        t.Db.DataSources.Add(new DataSource { Name = "CRM", AddressBookId = synced, KeyField = "id" });
        t.Db.Contacts.AddRange(
            new Contact { AddressBookId = synced, DisplayName = "A" },
            new Contact { AddressBookId = manual, DisplayName = "B" },
            new Contact { AddressBookId = manual, DisplayName = "C" });
        await t.Db.SaveChangesAsync();

        var refused = await AddressBookService.DeleteAsync(t.Db, Admin, synced);
        Assert.False(refused.Ok);
        Assert.False(refused.NotAllowed);
        Assert.True((await AddressBookService.DeleteAsync(t.Db, Actor.ForPerson(1, false, "x"), manual)).NotAllowed);

        var deleted = await AddressBookService.DeleteAsync(t.Db, Admin, manual);
        Assert.Equal(2, deleted.Value);
        t.Db.ChangeTracker.Clear();
        Assert.Equal(["A"], await t.Db.Contacts.Select(c => c.DisplayName).ToListAsync());
        Assert.Equal(2, await t.Db.Contacts.IgnoreQueryFilters().CountAsync(c => c.AddressBookId == manual && c.UpdateState == SoftEntity.Deleted));
        Assert.False(await t.Db.AddressBooks.AnyAsync(b => b.Id == manual));
    }

    [DbFact]
    public async Task The_list_counts_contacts_sources_and_people()
    {
        await using var t = await TestDatabase.CreateAsync();
        var ed = await t.AddUserAsync("Ed");
        var id = (await AddressBookService.SaveAsync(t.Db, Admin, null, Form("Customers") with { Access = [new(ed.Id, AddressBookRole.Editor)] })).Value;
        t.Db.DataSources.Add(new DataSource { Name = "CRM", AddressBookId = id, KeyField = "id" });
        t.Db.Contacts.Add(new Contact { AddressBookId = id, DisplayName = "A" });
        await t.Db.SaveChangesAsync();

        var row = Assert.Single(await AddressBookService.ListAsync(t.Db, Admin));
        Assert.Equal((1, 1, 1, AddressBookRole.Editor), (row.Contacts, row.Sources, row.People, row.Role));
        Assert.Equal(AddressBookRole.Editor, Assert.Single(await AddressBookService.ListAsync(t.Db, Person(ed))).Role);
    }

    [Fact]
    public void Every_field_of_a_contact_has_its_label_in_both_languages()
    {
        foreach (var name in ContactFields.All.Select(f => f.Name).Append("Birthday"))
        {
            Assert.True(Localization.TextCatalog.Entries.ContainsKey("Contacts.Field." + name), name);
        }
    }

    private static Actor Person(User user) => Actor.ForPerson(user.Id, user.Role == UserRole.Admin, user.Email);
}
