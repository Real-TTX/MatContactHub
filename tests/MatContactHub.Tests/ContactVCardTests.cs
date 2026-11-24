using System.Text;
using MatContactHub.Data;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Tests;

/// <summary>vCards: written as 3.0 and 4.0 (escaped, folded at 75 octets, UTF-8, no photo) and read back to the same fields; other writers' cards (2.1, groups, types).</summary>
public sealed class ContactVCardTests
{
    private static Contact Sample() => new()
    {
        Token = Guid.Parse("0b9a7c52-6c5e-4e43-9f39-0a7f0e1d2c3b"),
        Salutation = "Frau",
        Title = "Dr.",
        FirstName = "Jürgen-Änne",
        MiddleName = "Ö.",
        LastName = "Müller; Weiß, jun.",
        DisplayName = "Dr. Jürgen-Änne Müller; Weiß, jun.",
        Company = "Bäckerei Groß, Söhne & Co; KG",
        Department = "Einkauf\\Vertrieb",
        JobTitle = "Leiterin, Einkauf",
        Email = "jaenne@baeckerei.example",
        Email2 = "info@baeckerei.example",
        PhoneBusiness = "+49 30 1234567",
        PhoneBusiness2 = "030 7654321",
        PhoneMobile = "0171 9876543",
        PhoneHome = "+49 (0)89 111",
        PhoneOther = "+44 20 7946 0958",
        Fax = "030 1234568",
        Street = "Straße des 17. Juni 1; Hinterhaus",
        PostalCode = "10623",
        City = "Berlin",
        State = "Berlin",
        Country = "Deutschland",
        Website = "https://baeckerei.example/a,b;c",
        Birthday = new DateOnly(1980, 5, 17),
        CustomerNumber = "K-4711",
        Notes = "Erste Zeile\nZweite Zeile mit Komma, Semikolon; und \\ Backslash\nDritte: ☕ € – ein sehr langer Text, der sicher über fünfundsiebzig Oktette hinausgeht und gefaltet werden muss.",
        Custom1 = "Eins",
        Custom5 = "Fünf",
        UpdateDate = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc),
    };

    [Theory]
    [InlineData(VCardVersion.V3)]
    [InlineData(VCardVersion.V4)]
    public void A_contact_comes_back_as_it_went_out(VCardVersion version)
    {
        var c = Sample();
        var text = VCard.Write([c], version);
        var back = Assert.Single(VCard.Read(text));

        Assert.Equal(c.Salutation, back.Salutation);
        Assert.Equal(c.Title, back.Title);
        Assert.Equal(c.FirstName, back.FirstName);
        Assert.Equal(c.MiddleName, back.MiddleName);
        Assert.Equal(c.LastName, back.LastName);
        Assert.Equal(c.DisplayName, back.DisplayName);
        Assert.Equal(c.Company, back.Company);
        Assert.Equal(c.Department, back.Department);
        Assert.Equal(c.JobTitle, back.JobTitle);
        Assert.Equal((c.Email, c.Email2, (string?)null), (back.Email, back.Email2, back.Email3));
        Assert.Equal((c.PhoneBusiness, c.PhoneBusiness2, c.PhoneMobile, c.PhoneHome, c.PhoneOther, c.Fax),
            (back.PhoneBusiness, back.PhoneBusiness2, back.PhoneMobile, back.PhoneHome, back.PhoneOther, back.Fax));
        Assert.Equal((c.Street, c.PostalCode, c.City, c.State, c.Country), (back.Street, back.PostalCode, back.City, back.State, back.Country));
        Assert.Equal(c.Website, back.Website);
        Assert.Equal(c.Birthday, back.Birthday);
        Assert.Equal(c.CustomerNumber, back.CustomerNumber);
        Assert.Equal(c.Notes, back.Notes);
        Assert.Equal((c.Custom1, c.Custom5), (back.Custom1, back.Custom5));
    }

    [Theory]
    [InlineData(VCardVersion.V3, "VERSION:3.0", "UID:0b9a7c52-6c5e-4e43-9f39-0a7f0e1d2c3b", "BDAY:1980-05-17")]
    [InlineData(VCardVersion.V4, "VERSION:4.0", "UID:urn:uuid:0b9a7c52-6c5e-4e43-9f39-0a7f0e1d2c3b", "BDAY:19800517")]
    public void The_text_is_escaped_folded_and_without_a_photo(VCardVersion version, string versionLine, string uid, string birthday)
    {
        var text = VCard.Write([Sample()], version);
        var lines = text.Split("\r\n");

        Assert.StartsWith("BEGIN:VCARD\r\n" + versionLine + "\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("END:VCARD\r\n", text, StringComparison.Ordinal);
        Assert.Contains(uid, lines);
        Assert.Contains(birthday, lines);
        Assert.DoesNotContain("PHOTO", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // every line is at most 75 octets of UTF-8, a continued one starts with a space, and no character is cut in two
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, l));
        Assert.Contains(lines, l => l.StartsWith(' '));
        var unfolded = text.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains(@"N:Müller\; Weiß\, jun.;Jürgen-Änne;Ö.;Frau,Dr.;", unfolded, StringComparison.Ordinal);
        Assert.Contains(@"ORG:Bäckerei Groß\, Söhne & Co\; KG;Einkauf\\Vertrieb", unfolded, StringComparison.Ordinal);
        Assert.Contains(@"NOTE:Erste Zeile\nZweite Zeile mit Komma\, Semikolon\; und \\ Backslash\nDritte: ☕ €", unfolded, StringComparison.Ordinal);
    }

    [Fact]
    public void Cards_of_other_writers_are_read()
    {
        const string text = "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;J=C3=BCrgen;;;\r\nFN:J\r\n" +
                            "TEL;WORK;VOICE:+49 30 1\r\nTEL;CELL:0171 2\r\nTEL;HOME;FAX:030 3\r\nEMAIL;INTERNET:a@b.example\r\nEND:VCARD\r\n" +
                            "BEGIN:VCARD\nVERSION:3.0\nFN:Lang\n NAME\nitem1.TEL;type=pref:+1 555 0100\nitem1.X-ABLabel:Other\nitem2.EMAIL;TYPE=INTERNET:x@y.example\n" +
                            "EMAIL;TYPE=INTERNET,PREF:pref@y.example\nADR;TYPE=HOME:;Apt 1;Main St 5;Springfield;IL;62701;USA\nBDAY:--0517\nEND:VCARD\n" +
                            "BEGIN:VCARD\nVERSION:4.0\nEND:VCARD\n";
        var cards = VCard.Read(text);
        Assert.Equal(2, cards.Count);

        var first = cards[0];
        Assert.Equal(("Müller", "Jürgen"), (first.LastName, first.FirstName));
        Assert.Equal(("+49 30 1", "0171 2", "030 3"), (first.PhoneBusiness, first.PhoneMobile, first.Fax));
        Assert.Equal("a@b.example", first.Email);

        var second = cards[1];
        Assert.Equal("LangNAME", second.DisplayName);
        Assert.Equal("+1 555 0100", second.PhoneOther);
        Assert.Equal(("pref@y.example", "x@y.example"), (second.Email, second.Email2));
        Assert.Equal(("Apt 1, Main St 5", "Springfield", "IL", "62701", "USA"), (second.Street, second.City, second.State, second.PostalCode, second.Country));
        Assert.Null(second.Birthday);
    }

    [Theory]
    [InlineData("a,b;c\\d\ne", @"a\,b\;c\\d\ne")]
    [InlineData("zeile\r\nzwei", @"zeile\nzwei")]
    [InlineData("", "")]
    public void Text_is_escaped_and_unescaped(string value, string escaped)
    {
        Assert.Equal(escaped, VCard.Escape(value));
        Assert.Equal(value.Replace("\r\n", "\n", StringComparison.Ordinal), VCard.Unescape(escaped));
    }
}
