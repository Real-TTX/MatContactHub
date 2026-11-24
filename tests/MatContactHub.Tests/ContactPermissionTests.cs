using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>
/// The permission matrix of the contacts in the business layer: a viewer (also every person of a shared address book) only looks, an editor changes manual
/// contacts and the flag Hidden of synced ones, an administrator may do everything.
/// </summary>
public sealed class ContactPermissionTests
{
    private sealed record World(TestDatabase T, AddressBook Book, Contact Synced, Contact Manual, User Viewer, User Editor, User Admin, User Stranger);

    private static async Task<World> CreateAsync(TestDatabase t, bool shared)
    {
        var viewer = await t.AddUserAsync("Vera");
        var editor = await t.AddUserAsync("Ed");
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var stranger = await t.AddUserAsync("Stan");
        var book = new AddressBook { Name = "Customers", LdapName = "customers", IsShared = shared };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        var source = new DataSource { Name = "CRM", AddressBookId = book.Id, KeyField = "id" };
        t.Db.DataSources.Add(source);
        t.Db.AddressBookAccess.Add(new AddressBookAccess { AddressBookId = book.Id, UserId = editor.Id, Role = AddressBookRole.Editor });
        if (!shared)
        {
            t.Db.AddressBookAccess.Add(new AddressBookAccess { AddressBookId = book.Id, UserId = viewer.Id, Role = AddressBookRole.Viewer });
        }

        await t.Db.SaveChangesAsync();
        var synced = new Contact { AddressBookId = book.Id, DataSourceId = source.Id, ExternalKey = "42", DisplayName = "Synced" };
        var manual = new Contact { AddressBookId = book.Id, DisplayName = "Manual" };
        t.Db.Contacts.AddRange(synced, manual);
        await t.Db.SaveChangesAsync();
        t.Db.ChangeTracker.Clear();
        return new World(t, book, synced, manual, viewer, editor, admin, stranger);
    }

    private static Actor As(User user) => Actor.ForPerson(user.Id, user.Role == UserRole.Admin, user.Email);

    private static ContactForm Form(Contact c, bool hidden, string? company = null)
    {
        var form = ContactForm.Of(c);
        form.Hidden = hidden;
        form.Company = company ?? c.Company;
        return form;
    }

    private static async Task<bool> HiddenAsync(TestDatabase t, long id)
    {
        t.Db.ChangeTracker.Clear();
        return await t.Db.Contacts.Where(c => c.Id == id).Select(c => c.Hidden).SingleAsync();
    }

    [DbFact]
    public Task A_viewer_of_a_shared_address_book_cannot_hide_a_synced_contact() => ViewerCannotHideAsync(shared: true);

    [DbFact]
    public Task A_viewer_by_access_cannot_hide_a_synced_contact() => ViewerCannotHideAsync(shared: false);

    private static async Task ViewerCannotHideAsync(bool shared)
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await CreateAsync(t, shared);

        var detail = await ContactService.GetAsync(t.Db, As(w.Viewer), w.Synced.Id);
        Assert.Equal(AddressBookRole.Viewer, detail!.Role);

        var saved = await ContactService.SaveAsync(t.Db, As(w.Viewer), w.Synced.Id, Form(w.Synced, hidden: true));
        Assert.True(saved.NotAllowed);
        Assert.False(await HiddenAsync(t, w.Synced.Id));
    }

    [DbFact]
    public async Task A_viewer_changes_no_manual_contact_and_a_stranger_sees_nothing()
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await CreateAsync(t, shared: false);

        Assert.True((await ContactService.SaveAsync(t.Db, As(w.Viewer), w.Manual.Id, Form(w.Manual, hidden: true, company: "Changed"))).NotAllowed);
        Assert.False(await HiddenAsync(t, w.Manual.Id));
        Assert.Null(await ContactService.GetAsync(t.Db, As(w.Stranger), w.Synced.Id));
        Assert.True((await ContactService.SaveAsync(t.Db, As(w.Stranger), w.Synced.Id, Form(w.Synced, hidden: true))).NotAllowed);
        Assert.True((await ContactService.DeleteAsync(t.Db, As(w.Viewer), w.Manual.Id)).NotAllowed);
    }

    [DbFact]
    public async Task An_editor_hides_a_synced_contact_but_changes_nothing_else_of_it()
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await CreateAsync(t, shared: true);

        var saved = await ContactService.SaveAsync(t.Db, As(w.Editor), w.Synced.Id, Form(w.Synced, hidden: true, company: "Changed"));
        Assert.True(saved.Ok);
        Assert.True(await HiddenAsync(t, w.Synced.Id));
        Assert.Null(await t.Db.Contacts.Where(c => c.Id == w.Synced.Id).Select(c => c.Company).SingleAsync());
        Assert.True((await ContactService.DeleteAsync(t.Db, As(w.Editor), w.Synced.Id)).NotAllowed);

        Assert.True((await ContactService.SaveAsync(t.Db, As(w.Admin), w.Synced.Id, Form(w.Synced, hidden: false))).Ok);
        Assert.False(await HiddenAsync(t, w.Synced.Id));
    }

    [DbFact]
    public async Task An_editor_changes_a_manual_contact()
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await CreateAsync(t, shared: false);

        Assert.True((await ContactService.SaveAsync(t.Db, As(w.Editor), w.Manual.Id, Form(w.Manual, hidden: true, company: "Changed"))).Ok);
        t.Db.ChangeTracker.Clear();
        var manual = await t.Db.Contacts.SingleAsync(c => c.Id == w.Manual.Id);
        Assert.True(manual.Hidden);
        Assert.Equal("Changed", manual.Company);
    }

    /// <summary>
    /// The whole matrix of a private and a shared address book: who sees the contacts (list, detail), who creates one there, who hides one of a selection and who
    /// deletes one. A stranger of a private address book sees nothing; of a shared one it is a viewer.
    /// </summary>
    [ContactDbTheory]
    [InlineData("viewer", false, true, false)]
    [InlineData("editor", false, true, true)]
    [InlineData("admin", false, true, true)]
    [InlineData("stranger", false, false, false)]
    [InlineData("viewer", true, true, false)]
    [InlineData("editor", true, true, true)]
    [InlineData("admin", true, true, true)]
    [InlineData("stranger", true, true, false)]
    public async Task The_permission_matrix_holds_for_every_call(string who, bool shared, bool sees, bool changes)
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await CreateAsync(t, shared);
        var actor = As(who switch { "viewer" => w.Viewer, "editor" => w.Editor, "admin" => w.Admin, _ => w.Stranger });

        var list = await ContactService.ListAsync(t.Db, actor, new ContactCriteria(null, null, "name", false, 1, 50));
        Assert.Equal(sees ? 2 : 0, list.Total);
        Assert.Equal(sees, await ContactService.GetAsync(t.Db, actor, w.Manual.Id) is not null);
        Assert.Equal(sees ? 2 : 0, (await ContactService.ExportAsync(t.Db, actor, [w.Manual.Id, w.Synced.Id], 3)).Count);

        var created = await ContactService.SaveAsync(t.Db, actor, null, new ContactForm { AddressBookId = w.Book.Id, LastName = "New" });
        Assert.Equal(changes, created.Ok);
        Assert.Equal(changes ? 1 : 0, await ContactService.SetHiddenAsync(t.Db, actor, [w.Synced.Id], true));
        Assert.Equal(changes, await HiddenAsync(t, w.Synced.Id));

        // a synced contact is never deleted here, a manual one only by an editor
        Assert.Equal(changes ? 1 : 0, await ContactService.DeleteManyAsync(t.Db, actor, [w.Manual.Id, w.Synced.Id]));
        t.Db.ChangeTracker.Clear();
        Assert.True(await t.Db.Contacts.AnyAsync(c => c.Id == w.Synced.Id));
        Assert.Equal(!changes, await t.Db.Contacts.AnyAsync(c => c.Id == w.Manual.Id));
    }

    [DbFact]
    public async Task A_new_contact_goes_into_the_address_book_used_last()
    {
        await using var t = await TestDatabase.CreateAsync();
        var w = await CreateAsync(t, shared: false);
        var other = new AddressBook { Name = "Suppliers", LdapName = "suppliers", SortOrder = -1 };
        t.Db.AddressBooks.Add(other);
        await t.Db.SaveChangesAsync();
        t.Db.AddressBookAccess.Add(new AddressBookAccess { AddressBookId = other.Id, UserId = w.Editor.Id, Role = AddressBookRole.Editor });
        await t.Db.SaveChangesAsync();
        var editor = As(w.Editor);

        // the first in order without a history, then the one of the last contact the person created, and an asked one where it is an editor
        Assert.Equal(other.Id, await ContactService.DefaultAddressBookAsync(t.Db, editor, null));
        Assert.True((await ContactService.SaveAsync(t.Db, editor, null, new ContactForm { AddressBookId = w.Book.Id, LastName = "Last" })).Ok);
        Assert.Equal(w.Book.Id, await ContactService.DefaultAddressBookAsync(t.Db, editor, null));
        Assert.Equal(other.Id, await ContactService.DefaultAddressBookAsync(t.Db, editor, other.Id));

        // a viewer is an editor nowhere: no address book for a new contact
        Assert.Null(await ContactService.DefaultAddressBookAsync(t.Db, As(w.Viewer), w.Book.Id));
    }

    [Fact]
    public void A_form_is_checked_before_it_is_saved()
    {
        Assert.Contains(ContactService.Validate(new ContactForm()), e => e.Field == "LastName");
        Assert.Empty(ContactService.Validate(new ContactForm { Company = "ACME" }));
        Assert.Contains(ContactService.Validate(new ContactForm { LastName = "X", Email2 = "no address" }), e => e.Field == "Email2");
        Assert.Contains(ContactService.Validate(new ContactForm { LastName = "X", PostalCode = new string('1', 21) }), e => e.Field == "PostalCode");
        Assert.Contains(ContactService.Validate(new ContactForm { LastName = "X", Birthday = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)) }), e => e.Field == "Birthday");
        Assert.Empty(ContactService.Validate(new ContactForm { LastName = "X", Email = "anna.mueller@example.de", Birthday = new DateOnly(1980, 5, 17) }));
    }
}

/// <summary>A theory that needs PostgreSQL (like <see cref="DbFactAttribute"/>): skipped without <c>MATCONTACTHUB_TEST_DB</c>.</summary>
public sealed class ContactDbTheoryAttribute : TheoryAttribute
{
    public ContactDbTheoryAttribute()
    {
        if (TestDatabase.Admin is null)
        {
            Skip = "MATCONTACTHUB_TEST_DB is not set (a PostgreSQL connection string with the right to create databases).";
        }
    }
}
