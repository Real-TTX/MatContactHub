using MatContactHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>The rules of the database that every module relies on: the unique keys, the soft delete, the audit columns.</summary>
public sealed class DataModelTests
{
    [DbFact]
    public async Task A_record_of_a_source_is_one_contact_but_manual_contacts_need_no_key()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = new AddressBook { Name = "Customers", LdapName = "customers" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        var source = new DataSource { Name = "CRM", AddressBookId = book.Id, KeyField = "id" };
        t.Db.DataSources.Add(source);
        await t.Db.SaveChangesAsync();

        t.Db.Contacts.AddRange(new Contact { AddressBookId = book.Id, DisplayName = "Manual 1" }, new Contact { AddressBookId = book.Id, DisplayName = "Manual 2" });
        t.Db.Contacts.Add(new Contact { AddressBookId = book.Id, DataSourceId = source.Id, ExternalKey = "42", DisplayName = "Synced" });
        await t.Db.SaveChangesAsync();

        t.Db.Contacts.Add(new Contact { AddressBookId = book.Id, DataSourceId = source.Id, ExternalKey = "42", DisplayName = "Twice" });
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }

    [DbFact]
    public async Task The_LDAP_name_of_an_address_book_is_free_again_after_a_delete()
    {
        await using var t = await TestDatabase.CreateAsync();
        var first = new AddressBook { Name = "Staff", LdapName = "staff" };
        t.Db.AddressBooks.Add(first);
        await t.Db.SaveChangesAsync();
        Assert.Equal(SoftEntity.Created, first.UpdateState);
        Assert.NotEqual(default, first.CreateDate);

        t.Db.AddressBooks.Remove(first);
        await t.Db.SaveChangesAsync();
        Assert.Equal(SoftEntity.Deleted, first.UpdateState);
        Assert.False(await t.Db.AddressBooks.AnyAsync());
        Assert.True(await t.Db.AddressBooks.IgnoreQueryFilters().AnyAsync());

        t.Db.AddressBooks.Add(new AddressBook { Name = "Staff", LdapName = "staff" });
        await t.Db.SaveChangesAsync();
        t.Db.AddressBooks.Add(new AddressBook { Name = "Staff again", LdapName = "staff" });
        await Assert.ThrowsAsync<DbUpdateException>(() => t.Db.SaveChangesAsync());
    }
}
