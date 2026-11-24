using System.Text;
using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>The import of CSV and vCard files: the columns by their names, quotes and delimiters, the encodings, the preview, duplicates left out, the permission.</summary>
public sealed class ContactImportTests
{
    private const string Csv = "﻿Vorname;Nachname;Firma;E-Mail-Adresse;Telefon geschäftlich;Mobiltelefon;Ort;Geburtstag;Notizen;Unbekannt\r\n" +
                               "Max;Mustermann;\"ACME; GmbH\";max@acme.example;030 1234567;;Berlin;17.05.1980;\"Zeile 1\nZeile \"\"2\"\"\";x\r\n" +
                               "Erika;Musterfrau;Beta AG;erika@beta.example;;0171 9876543;Hamburg;;;\r\n" +
                               ";;;;;;;;;\r\n" +
                               "Doppelt;Max;;MAX@acme.example;;;;;;\r\n" +
                               ";;;kein-name;;;;;;\r\n";

    [Fact]
    public void The_columns_of_a_CSV_file_are_found_by_their_names()
    {
        var (format, columns, records) = ContactImport.Read(Csv);
        Assert.Equal(ImportFormat.Csv, format);
        Assert.Equal(["FirstName", "LastName", "Company", "Email", "PhoneBusiness", "PhoneMobile", "City", "Birthday", "Notes", null], columns.Select(c => c.Field));
        Assert.Equal(4, records.Count);

        var max = records[0];
        Assert.Equal(("Max", "Mustermann", "ACME; GmbH"), (max.FirstName, max.LastName, max.Company));
        Assert.Equal(new DateOnly(1980, 5, 17), max.Birthday);
        Assert.Equal("Zeile 1\nZeile \"2\"", max.Notes);
        Assert.Equal("0171 9876543", records[1].PhoneMobile);
    }

    [Theory]
    [InlineData("First Name,Last Name,E-mail Address,Business Phone\nAnna,Smith,anna@example.com,+44 20 7946 0958\n")]
    [InlineData("first name\tlast name\temail\tphone\nAnna\tSmith\tanna@example.com\t+44 20 7946 0958\n")]
    public void The_delimiter_and_English_headers_are_recognised(string text)
    {
        var record = Assert.Single(ContactImport.Read(text).Records);
        Assert.Equal(("Anna", "Smith", "anna@example.com", "+44 20 7946 0958"), (record.FirstName, record.LastName, record.Email, record.PhoneBusiness));
    }

    [Fact]
    public void A_file_in_Windows_1252_is_read_too()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Name;Ort\nJürgen Weiß;Köln\n");
        var record = Assert.Single(ContactImport.Read(ContactImport.Decode(bytes)).Records);
        Assert.Equal(("Jürgen Weiß", "Köln"), (record.DisplayName, record.City));
        Assert.Equal("Jürgen", ContactImport.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Jürgen")).ToArray()));
    }

    [DbFact]
    public async Task A_CSV_file_is_imported_without_what_is_already_there()
    {
        await using var t = await TestDatabase.CreateAsync();
        var editor = await t.AddUserAsync("Ed");
        var viewer = await t.AddUserAsync("Vera");
        var book = new AddressBook { Name = "Customers", LdapName = "customers" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        t.Db.AddressBookAccess.AddRange(
            new AddressBookAccess { AddressBookId = book.Id, UserId = editor.Id, Role = AddressBookRole.Editor },
            new AddressBookAccess { AddressBookId = book.Id, UserId = viewer.Id, Role = AddressBookRole.Viewer });
        t.Db.Contacts.Add(new Contact { AddressBookId = book.Id, DisplayName = "Old Erika", PhoneMobile = "+49 171 9876543" });
        await t.Db.SaveChangesAsync();
        var ed = Actor.ForPerson(editor.Id, false, editor.Email);

        // a viewer may not import; the preview of an editor counts what is left out
        Assert.Null(await ContactImport.PreviewAsync(t.Db, Actor.ForPerson(viewer.Id, false, viewer.Email), book.Id, Csv, true));
        Assert.True((await ContactImport.ImportAsync(t.Db, Actor.ForPerson(viewer.Id, false, viewer.Email), book.Id, Csv, true)).NotAllowed);
        var preview = await ContactImport.PreviewAsync(t.Db, ed, book.Id, Csv, skipDuplicates: true);
        Assert.Equal((4, 1, 2), (preview!.Records, preview.Invalid, preview.Duplicates));
        Assert.Equal(["Max"], preview.Sample.Select(s => s.FirstName));

        var imported = await ContactImport.ImportAsync(t.Db, ed, book.Id, Csv, skipDuplicates: true);
        Assert.Equal(new ImportResult(1, 2, 1), imported.Value);
        t.Db.ChangeTracker.Clear();
        var max = await t.Db.Contacts.SingleAsync(c => c.FirstName == "Max");
        Assert.Equal(("Max Mustermann", (long?)null, editor.Id), (max.DisplayName, max.DataSourceId, max.CreateUserId!.Value));

        // without the check every record with a name comes in
        var again = await ContactImport.ImportAsync(t.Db, ed, book.Id, Csv, skipDuplicates: false);
        Assert.Equal(new ImportResult(3, 0, 1), again.Value);
        Assert.Equal(5, await t.Db.Contacts.CountAsync());
    }

    [DbFact]
    public async Task A_vCard_file_is_imported_as_manual_contacts()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = new AddressBook { Name = "Imported", LdapName = "imported" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();
        var source = new Contact { Token = Guid.NewGuid(), FirstName = "Ünal", LastName = "Öztürk", DisplayName = "Ünal Öztürk", Email = "u@example.com" };
        var text = VCard.Write([source, new Contact { Company = "Nur Firma" }], VCardVersion.V3);

        var result = await ContactImport.ImportAsync(t.Db, Actor.System("test"), book.Id, text, skipDuplicates: true);
        Assert.Equal(new ImportResult(2, 0, 0), result.Value);
        t.Db.ChangeTracker.Clear();
        Assert.Equal(["Nur Firma", "Ünal Öztürk"], await t.Db.Contacts.OrderBy(c => c.DisplayName).Select(c => c.DisplayName).ToListAsync());
        Assert.True(await t.Db.Contacts.AllAsync(c => c.DataSourceId == null && c.AddressBookId == book.Id));
    }

    [Fact]
    public void A_stashed_file_belongs_to_its_person()
    {
        var anna = Actor.ForPerson(1, false, "anna@example.com");
        var key = ContactImport.Stash(anna, "BEGIN:VCARD");
        Assert.Equal("BEGIN:VCARD", ContactImport.Peek(anna, key));
        Assert.Null(ContactImport.Peek(Actor.ForPerson(2, false, "ben@example.com"), key));
        Assert.Null(ContactImport.Peek(anna, "../../etc/passwd"));
        ContactImport.Drop(anna, key);
        Assert.Null(ContactImport.Peek(anna, key));
    }
}
