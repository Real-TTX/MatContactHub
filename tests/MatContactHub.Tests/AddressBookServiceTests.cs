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
}
