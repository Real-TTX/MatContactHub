using MatContactHub.Data;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Tests;

/// <summary>THE telephone numbers of every module: E.164 from any notation, the formats of the LDAP server, the digits for substring matches.</summary>
public sealed class PhoneNormalizerTests
{
    [Theory]
    [InlineData("030 12345678", "DE", "+493012345678")]
    [InlineData("+49 (30) 1234-5678", "DE", "+493012345678")]
    [InlineData("0049 30 12345678", "DE", "+493012345678")]
    [InlineData("+49 30 12345678", "AT", "+493012345678")]
    [InlineData("0171/1234567", "DE", "+491711234567")]
    [InlineData("01 5123456", "AT", "+4315123456")]
    [InlineData("044 668 18 00", "CH", "+41446681800")]
    [InlineData("+44 20 7946 0958", "DE", "+442079460958")]
    [InlineData("030 12345678", null, "+493012345678")]
    [InlineData("030 12345678", "de", "+493012345678")]
    public void Normalize_answers_E164(string raw, string? country, string expected) => Assert.Equal(expected, PhoneNormalizer.Normalize(raw, country));

    [Fact]
    public void The_three_notations_of_a_caller_are_the_same_number()
    {
        var numbers = new[] { "+4930123456", "030123456", "004930123456" }.Select(n => PhoneNormalizer.Normalize(n, "DE")).Distinct().ToList();
        Assert.Equal(["+4930123456"], numbers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("+")]
    public void Normalize_answers_null_for_no_number(string? raw) => Assert.Null(PhoneNormalizer.Normalize(raw, "DE"));

    [Theory]
    [InlineData("+493012345678", NumberFormat.E164, "+493012345678")]
    [InlineData("+493012345678", NumberFormat.National, "030 12345678")]
    [InlineData("+493012345678", NumberFormat.InternationalDigits, "00493012345678")]
    [InlineData("+442079460958", NumberFormat.National, "+44 20 7946 0958")]
    [InlineData("030 / 123 456 78", NumberFormat.AsStored, "030 / 123 456 78")]
    [InlineData("030 12345678", NumberFormat.E164, "+493012345678")]
    [InlineData("intern 12", NumberFormat.E164, "intern 12")]
    public void Format_writes_the_number_as_asked(string number, NumberFormat format, string expected)
        => Assert.Equal(expected, PhoneNormalizer.Format(number, format, "DE"));

    [Fact]
    public void Format_keeps_empty_empty() => Assert.Null(PhoneNormalizer.Format("  ", NumberFormat.E164, "DE"));

    [Theory]
    [InlineData("+49 (30) 123-45", "493012345")]
    [InlineData("0049 30 12345", "00493012345")]
    [InlineData("abc", "")]
    [InlineData(null, "")]
    public void Digits_keeps_the_digits_only(string? raw, string expected) => Assert.Equal(expected, PhoneNormalizer.Digits(raw));

    [Theory]
    [InlineData("DE", true)]
    [InlineData("at", true)]
    [InlineData("XX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Known_countries(string? code, bool known) => Assert.Equal(known, PhoneNormalizer.IsKnownCountry(code));
}

/// <summary>The seam between the modules that change the directory and the LDAP snapshot.</summary>
public sealed class DirectoryChangesTests
{
    [Fact]
    public void Bump_raises_the_version_and_tells_the_readers()
    {
        long seen = 0;
        void Remember(long version) => seen = version;
        DirectoryChanges.Changed += Remember;
        try
        {
            var before = DirectoryChanges.Version;
            var after = DirectoryChanges.Bump();
            Assert.True(after > before);
            Assert.True(DirectoryChanges.Version >= after);
            Assert.True(seen >= after);
        }
        finally
        {
            DirectoryChanges.Changed -= Remember;
        }
    }

    [DbFact]
    public async Task Saving_a_contact_bumps_the_version()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = new AddressBook { Name = "Customers", LdapName = "customers" };
        t.Db.AddressBooks.Add(book);
        await t.Db.SaveChangesAsync();

        var before = DirectoryChanges.Version;
        t.Db.Contacts.Add(new Contact { AddressBookId = book.Id, DisplayName = "Ada Lovelace", PhoneBusiness = "030 123456" });
        await t.Db.SaveChangesAsync();
        Assert.True(DirectoryChanges.Version > before);
    }
}
