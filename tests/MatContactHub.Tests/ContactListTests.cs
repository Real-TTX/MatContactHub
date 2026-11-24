using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Tests;

/// <summary>The list of the contacts: the search (words in names, company, department, e-mail; numbers in any spelling), the filters and the sort.</summary>
public sealed class ContactListTests
{
    private static readonly Actor Admin = Actor.System("test");

    private sealed record World(AddressBook Customers, AddressBook Staff, DataSource Crm);

    private static async Task<World> SeedAsync(TestDatabase t)
    {
        var customers = new AddressBook { Name = "Customers", LdapName = "customers" };
        var staff = new AddressBook { Name = "Staff", LdapName = "staff" };
        t.Db.AddressBooks.AddRange(customers, staff);
        await t.Db.SaveChangesAsync();
        var crm = new DataSource { Name = "CRM", AddressBookId = customers.Id, KeyField = "id" };
        t.Db.DataSources.Add(crm);
        await t.Db.SaveChangesAsync();
        t.Db.Contacts.AddRange(
            new Contact { AddressBookId = customers.Id, FirstName = "Max", LastName = "Mustermann", DisplayName = "Max Mustermann", Company = "ACME GmbH", City = "Berlin", PhoneBusiness = "+49 30 1234567", Email = "max@acme.example" },
            new Contact { AddressBookId = customers.Id, FirstName = "Erika", LastName = "Musterfrau", DisplayName = "Erika Musterfrau", Company = "Beta AG", City = "Hamburg", PhoneMobile = "0171 9876543", Department = "Einkauf" },
            new Contact { AddressBookId = customers.Id, DataSourceId = crm.Id, ExternalKey = "7", DisplayName = "Jürgen Weiß", Company = "Gamma", City = "Köln", PhoneOther = "0049 (221) 55-66-77", Email2 = "JW@Gamma.example", Hidden = true },
            new Contact { AddressBookId = staff.Id, DisplayName = "Anna Admin", Company = "MatContactHub", City = "Aachen", Fax = "030 1234567", CustomerNumber = "K-100" });
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();
        return new World(customers, staff, crm);
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(TestDatabase t, string? search, string sort = "name", bool desc = false, long? book = null, string? origin = null, bool? hidden = null)
        => [.. (await ContactService.ListAsync(t.Db, Admin, new ContactCriteria(search, book, sort, desc, 1, 50, origin, hidden))).Items.Select(c => c.DisplayName)];

    [DbFact]
    public async Task Words_are_searched_in_names_company_department_and_mail()
    {
        await using var t = await TestDatabase.CreateAsync();
        await SeedAsync(t);

        Assert.Equal(["Erika Musterfrau", "Max Mustermann"], await NamesAsync(t, "muster"));
        Assert.Equal(["Max Mustermann"], await NamesAsync(t, "max acme"));
        Assert.Equal(["Erika Musterfrau"], await NamesAsync(t, "einkauf"));
        Assert.Equal(["Jürgen Weiß"], await NamesAsync(t, "jw@gamma"));
        Assert.Equal(["Anna Admin"], await NamesAsync(t, "K-100"));
        Assert.Empty(await NamesAsync(t, "nobody"));

        // % and _ are letters, not patterns
        Assert.Empty(await NamesAsync(t, "%"));
    }

    [DbFact]
    public async Task A_number_is_found_however_it_is_written()
    {
        await using var t = await TestDatabase.CreateAsync();
        await SeedAsync(t);

        // stored "+49 30 1234567" (business) and "030 1234567" (fax)
        Assert.Equal(["Anna Admin", "Max Mustermann"], await NamesAsync(t, "0301234567"));
        Assert.Equal(["Anna Admin", "Max Mustermann"], await NamesAsync(t, "+49 30 1234567"));
        Assert.Equal(["Anna Admin", "Max Mustermann"], await NamesAsync(t, "0049301234567"));
        Assert.Equal(["Anna Admin", "Max Mustermann"], await NamesAsync(t, "1234567"));

        // stored "0049 (221) 55-66-77"
        Assert.Equal(["Jürgen Weiß"], await NamesAsync(t, "+49 221 556677"));
        Assert.Equal(["Jürgen Weiß"], await NamesAsync(t, "0221/556677"));

        // stored "0171 9876543" (mobile)
        Assert.Equal(["Erika Musterfrau"], await NamesAsync(t, "+491719876543"));
        Assert.Empty(await NamesAsync(t, "+49 89 1111111"));
    }

    [DbFact]
    public async Task The_filters_and_the_sort_work_together()
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await SeedAsync(t);

        Assert.Equal(["Anna Admin"], await NamesAsync(t, null, book: w.Staff.Id));
        Assert.Equal(["Jürgen Weiß"], await NamesAsync(t, null, origin: ContactOrigin.Synced));
        Assert.Equal(["Jürgen Weiß"], await NamesAsync(t, null, origin: ContactOrigin.Source(w.Crm.Id)));
        Assert.Equal(3, (await NamesAsync(t, null, origin: ContactOrigin.Manual)).Count);
        Assert.Equal(["Jürgen Weiß"], await NamesAsync(t, null, hidden: true));
        Assert.Equal(3, (await NamesAsync(t, null, hidden: false)).Count);

        Assert.Equal(["Anna Admin", "Erika Musterfrau", "Jürgen Weiß", "Max Mustermann"], await NamesAsync(t, null));
        Assert.Equal(["Max Mustermann", "Jürgen Weiß", "Erika Musterfrau", "Anna Admin"], await NamesAsync(t, null, desc: true));
        Assert.Equal(["Aachen", "Berlin", "Hamburg", "Köln"],
            (await ContactService.ListAsync(t.Db, Admin, new ContactCriteria(null, null, "city", false, 1, 50))).Items.Select(c => c.City!).ToList());
        Assert.Equal("ACME GmbH", (await ContactService.ListAsync(t.Db, Admin, new ContactCriteria(null, null, "company", false, 1, 50))).Items[0].Company);

        var page = await ContactService.ListAsync(t.Db, Admin, new ContactCriteria(null, null, "name", false, 2, 3));
        Assert.Equal((4, 4, 1), (page.Total, page.Matching, page.Items.Count));
    }

    [DbFact]
    public async Task The_whole_list_and_a_selection_are_exported_as_vCards()
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await SeedAsync(t);

        var all = await ContactService.ExportAsync(t.Db, Admin, new ContactCriteria("muster", null, "name", false, 1, 50), 3);
        Assert.Equal(2, all.Count);
        Assert.Equal(2, VCard.Read(all.Text).Count);

        var one = await ContactService.ExportAsync(t.Db, Admin, [(await ContactService.ListAsync(t.Db, Admin, new ContactCriteria("Anna", w.Staff.Id, "name", false, 1, 1))).Items[0].Id], 4);
        Assert.Equal("Anna Admin", one.SingleName);
        Assert.Contains("VERSION:4.0", one.Text, StringComparison.Ordinal);
    }

    [DbFact]
    public async Task The_search_of_the_header_bar_suggests_contacts()
    {
        await using var t = await TestDatabase.CreateAsync();
        await SeedAsync(t);

        Assert.Equal(["Erika Musterfrau", "Max Mustermann"], (await ContactService.SuggestAsync(t.Db, Admin, "Muster", 8)).Select(r => r.DisplayName));
        Assert.Single(await ContactService.SuggestAsync(t.Db, Admin, "Muster", 1));
        Assert.Empty(await ContactService.SuggestAsync(t.Db, Admin, " ", 8));
        Assert.Empty(await ContactService.SuggestAsync(t.Db, Actor.Anonymous, "Muster", 8));
    }

    [Fact]
    public void The_patterns_of_a_number_hold_its_digits_and_its_national_number()
    {
        Assert.Empty(ContactSearch.PhonePatterns("Max", "DE"));
        Assert.Empty(ContactSearch.PhonePatterns("12", "DE"));
        Assert.Equal([@"0\D*3\D*0\D*1\D*2\D*3\D*4\D*5\D*6\D*7", @"3\D*0\D*1\D*2\D*3\D*4\D*5\D*6\D*7"], ContactSearch.PhonePatterns("030 1234567", "DE"));
        Assert.Equal("301234567", ContactSearch.NationalNumber("+49 (30) 1234567", "DE"));
        Assert.Equal("301234567", ContactSearch.NationalNumber("0049 30 1234567", "AT"));
    }
}
