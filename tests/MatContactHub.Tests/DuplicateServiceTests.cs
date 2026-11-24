using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>The duplicates: the groups (numbers in any spelling, e-mail without case, name and company, chains), "not a duplicate", the merge, who sees and changes what.</summary>
public sealed class DuplicateServiceTests
{
    private static readonly Actor Admin = Actor.System("test");

    private static async Task<AddressBook> BookAsync(TestDatabase t, string name, bool shared = false)
    {
        var book = new AddressBook { Name = name, LdapName = name.ToLowerInvariant(), IsShared = shared };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<Contact> AddAsync(TestDatabase t, Contact c)
    {
        t.Db.Contacts.Add(c);
        await t.Db.SaveChangesAsync();
        return c;
    }

    private static IReadOnlyList<long> Ids(DuplicateGroup g) => [.. g.Contacts.Select(c => c.Id).Order()];

    [DbFact]
    public async Task Contacts_are_grouped_by_number_mail_and_name_and_joined_into_chains()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = await BookAsync(t, "Customers");
        var a = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Max Mustermann", FirstName = "Max", LastName = "Mustermann", Company = "ACME", PhoneBusiness = "030 1234567" });
        var b = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "M. Mustermann", PhoneMobile = "+49 (30) 123-4567", Email = "Max@ACME.example" });
        var c = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Maximilian", Email2 = "max@acme.example" });
        var d = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Jürgen Weiß", FirstName = "Jürgen", LastName = "Weiß", Company = "Gamma GmbH" });
        var e = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Juergen", FirstName = "JÜRGEN", LastName = "weiss", Company = "gamma gmbh." });
        await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Alone", PhoneBusiness = "030 999999", Fax = "030 1234567" });

        var groups = await DuplicateService.FindAsync(t.Db, Admin);
        Assert.Equal(2, groups.Count);

        // a~b by the number, b~c by the address: one group of three, the fax of "Alone" links nobody
        var chain = groups[0];
        Assert.Equal([a.Id, b.Id, c.Id], Ids(chain));
        Assert.Equal([DuplicateReason.Phone, DuplicateReason.Email], chain.Reasons);
        Assert.Contains(chain.Matches, m => m.Value == "+49301234567");
        Assert.Contains(chain.Matches, m => m.Value == "max@acme.example");
        Assert.True(chain.CanChange);

        // the name without case, accents and punctuation (ß is ss)
        Assert.Equal([d.Id, e.Id], Ids(groups[1]));
        Assert.Equal([DuplicateReason.NameAndCompany], groups[1].Reasons);
    }

    [DbFact]
    public async Task Not_a_duplicate_takes_a_pair_out_and_a_shared_switchboard_links_nobody()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = await BookAsync(t, "Customers");
        var a = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "A", Email = "same@example.com" });
        var b = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "B", Email = "SAME@example.com" });
        for (var i = 0; i < DuplicateService.MaxShared + 1; i++)
        {
            t.Db.Contacts.Add(new Contact { AddressBookId = book.Id, DisplayName = "Employee " + i, PhoneBusiness = "+49 30 5000" });
        }

        await t.Db.SaveChangesAsync();
        var group = Assert.Single(await DuplicateService.FindAsync(t.Db, Admin));
        Assert.Equal([a.Id, b.Id], Ids(group));

        Assert.True((await DuplicateService.IgnoreAsync(t.Db, Admin, a.Id, b.Id)).Ok);
        Assert.Empty(await DuplicateService.FindAsync(t.Db, Admin));
        Assert.Equal(1, await t.Db.DuplicateIgnores.CountAsync(i => i.ContactIdLow == a.Id && i.ContactIdHigh == b.Id));

        // twice is once
        Assert.Equal(0, (await DuplicateService.IgnoreAsync(t.Db, Admin, b.Id, a.Id)).Value);
    }

    [DbFact]
    public async Task A_merge_fills_the_kept_contact_deletes_manual_ones_and_hides_synced_ones()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = await BookAsync(t, "Customers");
        var source = new DataSource { Name = "CRM", AddressBookId = book.Id, KeyField = "id" };
        t.Db.DataSources.Add(source);
        await t.Db.SaveChangesAsync();
        var keep = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Max Mustermann", LastName = "Mustermann", Email = "max@acme.example", PhoneBusiness = "030 1234567" });
        var manual = await AddAsync(t, new Contact { AddressBookId = book.Id, DisplayName = "Max M.", Email = "MAX@acme.example", Email2 = "private@max.example", City = "Berlin", PhoneMobile = "0171 1", Birthday = new DateOnly(1980, 5, 17) });
        var synced = await AddAsync(t, new Contact { AddressBookId = book.Id, DataSourceId = source.Id, ExternalKey = "1", DisplayName = "Mustermann, Max", PhoneBusiness = "+49 30 1234567", Company = "ACME", City = "Hamburg" });

        var merged = await DuplicateService.MergeAsync(t.Db, Admin, keep.Id, [keep.Id, manual.Id, synced.Id]);
        Assert.True(merged.Ok);
        t.Db.ChangeTracker.Clear();

        var kept = await t.Db.Contacts.SingleAsync(c => c.Id == keep.Id);
        Assert.Equal(("Max Mustermann", "Mustermann", "030 1234567"), (kept.DisplayName, kept.LastName, kept.PhoneBusiness));
        Assert.Equal(("Berlin", "0171 1", "ACME", new DateOnly(1980, 5, 17)), (kept.City, kept.PhoneMobile, kept.Company, kept.Birthday!.Value));
        Assert.Equal(("max@acme.example", "private@max.example", (string?)null), (kept.Email, kept.Email2, kept.Email3));

        Assert.False(await t.Db.Contacts.AnyAsync(c => c.Id == manual.Id));
        Assert.True(await t.Db.Contacts.IgnoreQueryFilters().AnyAsync(c => c.Id == manual.Id && c.UpdateState == SoftEntity.Deleted));
        var hidden = await t.Db.Contacts.SingleAsync(c => c.Id == synced.Id);
        Assert.True(hidden.Hidden);
        Assert.Equal("ACME", hidden.Company);

        // the synced one stays, hidden, but is no duplicate any more
        Assert.Empty(await DuplicateService.FindAsync(t.Db, Admin));
    }

    [DbFact]
    public async Task Only_what_the_actor_sees_is_grouped_and_only_an_editor_of_every_book_merges()
    {
        await using var t = await TestDatabase.CreateAsync();
        var viewer = await t.AddUserAsync("Vera");
        var editor = await t.AddUserAsync("Ed");
        var shared = await BookAsync(t, "Shared", shared: true);
        var secret = await BookAsync(t, "Secret");
        t.Db.AddressBookAccess.Add(new AddressBookAccess { AddressBookId = shared.Id, UserId = editor.Id, Role = AddressBookRole.Editor });
        await t.Db.SaveChangesAsync();
        var a = await AddAsync(t, new Contact { AddressBookId = shared.Id, DisplayName = "A", Email = "x@example.com" });
        var b = await AddAsync(t, new Contact { AddressBookId = shared.Id, DisplayName = "B", Email = "x@example.com" });
        var c = await AddAsync(t, new Contact { AddressBookId = secret.Id, DisplayName = "C", Email = "x@example.com" });
        var vera = Actor.ForPerson(viewer.Id, false, viewer.Email);
        var ed = Actor.ForPerson(editor.Id, false, editor.Email);

        var seen = Assert.Single(await DuplicateService.FindAsync(t.Db, vera));
        Assert.Equal([a.Id, b.Id], Ids(seen));
        Assert.False(seen.CanChange);
        Assert.True(Assert.Single(await DuplicateService.FindAsync(t.Db, ed)).CanChange);
        Assert.Equal([a.Id, b.Id, c.Id], Ids(Assert.Single(await DuplicateService.FindAsync(t.Db, Admin))));

        Assert.True((await DuplicateService.MergeAsync(t.Db, vera, a.Id, [b.Id])).NotAllowed);
        Assert.True((await DuplicateService.IgnoreAsync(t.Db, vera, a.Id, b.Id)).NotAllowed);
        Assert.True((await DuplicateService.MergeAsync(t.Db, ed, a.Id, [b.Id, c.Id])).NotAllowed);
        Assert.True((await DuplicateService.MergeAsync(t.Db, ed, a.Id, [b.Id])).Ok);
        t.Db.ChangeTracker.Clear();
        Assert.False(await t.Db.Contacts.AnyAsync(x => x.Id == b.Id));
        Assert.True(await t.Db.Contacts.AnyAsync(x => x.Id == c.Id));
    }

    [Theory]
    [InlineData("Dr. Jürgen  Müller", "dr jurgen muller")]
    [InlineData("WEISS, Jürgen", "weiss jurgen")]
    [InlineData("Weiß", "weiss")]
    [InlineData("  ", "")]
    public void A_name_is_compared_without_case_accents_and_punctuation(string name, string normalized)
        => Assert.Equal(normalized, DuplicateService.NormalizeName(name));
}
