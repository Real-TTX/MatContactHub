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
        => new(c.AddressBookId, c.FirstName, c.LastName, c.DisplayName, company ?? c.Company, c.Email, c.PhoneBusiness, c.PhoneMobile, hidden);

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
}
