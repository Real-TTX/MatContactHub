using MatContactHub.Data;
using MatContactHub.Services.Sources;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>The mapping: fields and templates, the transforms (with Phone), dates, the hash, auto-map with German and English names, the SQL check.</summary>
public sealed class SourceMappingTests
{
    private static SourceRecord Record(params (string Key, string? Value)[] fields)
    {
        var record = new SourceRecord();
        foreach (var (key, value) in fields)
        {
            record[key] = value;
        }

        return record;
    }

    [Fact]
    public void A_field_is_found_without_case_and_trimmed()
    {
        var record = Record(("First_Name", "  Ada "));
        Assert.Equal("Ada", FieldMapper.Evaluate("first_name", record));
        Assert.Null(FieldMapper.Evaluate("missing", record));
        Assert.Null(FieldMapper.Evaluate("", record));
    }

    [Theory]
    [InlineData("{first} {middle} {last}", "Ada Lovelace")]
    [InlineData("{title} {first} {last}", "Ada Lovelace")]
    [InlineData("{last}, {first}", "Lovelace, Ada")]
    [InlineData("{street}\n{city}", "Ada Lovelace")]
    public void A_template_fills_its_parts_and_collapses_the_spaces_of_empty_ones(string template, string expected)
    {
        var record = Record(("first", "Ada"), ("middle", ""), ("last", "Lovelace"), ("title", null));
        var value = FieldMapper.Evaluate(template, template.Contains("street", StringComparison.Ordinal) ? Record(("street", "Ada"), ("city", "Lovelace")) : record);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void A_template_of_empty_parts_is_empty()
        => Assert.Null(FieldMapper.Evaluate("{a} {b}", Record(("a", " "), ("b", null))));

    [Theory]
    [InlineData(FieldTransform.None, "  Hello  World ", "Hello  World")]
    [InlineData(FieldTransform.Trim, "  Hello  \t World ", "Hello World")]
    [InlineData(FieldTransform.Lower, "Ada@Example.COM", "ada@example.com")]
    [InlineData(FieldTransform.Upper, "de", "DE")]
    [InlineData(FieldTransform.Capitalize, "MÜLLER-LÜDENSCHEID GMBH", "Müller-Lüdenscheid Gmbh")]
    [InlineData(FieldTransform.Capitalize, "jean d'arc", "Jean D'arc")]
    public void The_transforms(FieldTransform transform, string input, string expected)
        => Assert.Equal(expected, FieldMapper.Apply(transform, input, "DE"));

    [Theory]
    [InlineData("030 1234567", "DE", "+49301234567")]
    [InlineData("+49 (30) 1234-567", "DE", "+49301234567")]
    [InlineData("0049 30 1234567", "DE", "+49301234567")]
    [InlineData("01 23 45 67 89", "FR", "+33123456789")]
    [InlineData("not a number", "DE", "not a number")]
    public void The_phone_transform_writes_e164_and_keeps_what_it_cannot_read(string input, string country, string expected)
        => Assert.Equal(expected, FieldMapper.Apply(FieldTransform.Phone, input, country));

    [Theory]
    [InlineData("1815-12-10", "1815-12-10")]
    [InlineData("10.12.1815", "1815-12-10")]
    [InlineData("12/10/1815", "1815-12-10")]
    [InlineData("1815-12-10T00:00:00Z", "1815-12-10")]
    [InlineData("someday", null)]
    public void Dates_are_read(string input, string? expected)
        => Assert.Equal(expected, FieldMapper.ParseDate(input)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void A_record_is_mapped_with_its_key_and_every_field()
    {
        var record = Record(("id", " 42 "), ("Vorname", "ada"), ("Nachname", "LOVELACE"), ("Tel", "030 1234567"), ("born", "10.12.1815"), ("Notiz", new string('x', 9000)));
        MappingRule[] rules =
        [
            new("FirstName", "Vorname", FieldTransform.Capitalize),
            new("LastName", "Nachname", FieldTransform.Capitalize),
            new("DisplayName", "{Vorname} {Nachname}", FieldTransform.Capitalize),
            new("PhoneBusiness", "Tel", FieldTransform.Phone),
            new("Birthday", "born", FieldTransform.None),
            new("Notes", "Notiz", FieldTransform.None),
            new("NotAField", "id", FieldTransform.None),
        ];
        var mapped = FieldMapper.Map(record, "id", rules, "DE");
        Assert.Equal("42", mapped.Key);
        Assert.Equal("Ada", mapped.Values["FirstName"]);
        Assert.Equal("Ada Lovelace", mapped.Values["DisplayName"]);
        Assert.Equal("+49301234567", mapped.Values["PhoneBusiness"]);
        Assert.Equal("1815-12-10", mapped.Values["Birthday"]);
        Assert.Equal(8000, mapped.Values["Notes"]!.Length);
        Assert.Null(mapped.Values["Company"]);
        Assert.False(mapped.Values.ContainsKey("NotAField"));

        var contact = new Contact { ExternalKey = mapped.Key };
        FieldMapper.Write(contact, mapped.Values);
        Assert.Equal("Ada Lovelace", contact.DisplayName);
        Assert.Equal(new DateOnly(1815, 12, 10), contact.Birthday);
    }

    [Fact]
    public void A_template_can_be_the_key()
    {
        var mapped = FieldMapper.Map(Record(("company", "ACME"), ("no", "7")), "{company}-{no}", [], "DE");
        Assert.Equal("ACME-7", mapped.Key);
        Assert.False(FieldMapper.HasAnyValue(mapped));
    }

    [Fact]
    public void Without_a_name_the_display_name_falls_back_to_the_company_then_the_mail()
    {
        var c = new Contact();
        FieldMapper.Write(c, new Dictionary<string, string?> { ["Company"] = "ACME" });
        Assert.Equal("ACME", c.DisplayName);
        FieldMapper.Write(c, new Dictionary<string, string?> { ["Email"] = "info@acme.test" });
        Assert.Equal("info@acme.test", c.DisplayName);
    }

    [Fact]
    public void The_hash_changes_with_a_value_and_with_the_address_book()
    {
        var a = FieldMapper.Map(Record(("n", "Ada")), "n", [new("FirstName", "n", FieldTransform.None)], "DE").Values;
        var b = FieldMapper.Map(Record(("n", "Ada ")), "n", [new("FirstName", "n", FieldTransform.None)], "DE").Values;
        var c = FieldMapper.Map(Record(("n", "Grace")), "n", [new("FirstName", "n", FieldTransform.None)], "DE").Values;
        Assert.Equal(FieldMapper.Hash(1, a), FieldMapper.Hash(1, b));
        Assert.NotEqual(FieldMapper.Hash(1, a), FieldMapper.Hash(1, c));
        Assert.NotEqual(FieldMapper.Hash(1, a), FieldMapper.Hash(2, a));
        Assert.Equal(64, FieldMapper.Hash(1, a).Length);
    }

    [Fact]
    public void Auto_map_knows_german_names()
    {
        string[] fields = ["KdNr", "Anrede", "Vorname", "Nachname", "Firma", "Abteilung", "Telefon", "Mobil", "Fax", "E-Mail", "Straße", "PLZ", "Ort", "Land", "Geburtstag", "Bemerkung"];
        var rules = FieldMapper.AutoMap(fields, []).ToDictionary(r => r.TargetField, r => r);
        Assert.Equal("Anrede", rules["Salutation"].SourceExpression);
        Assert.Equal("Vorname", rules["FirstName"].SourceExpression);
        Assert.Equal("Nachname", rules["LastName"].SourceExpression);
        Assert.Equal("Firma", rules["Company"].SourceExpression);
        Assert.Equal("Abteilung", rules["Department"].SourceExpression);
        Assert.Equal("Telefon", rules["PhoneBusiness"].SourceExpression);
        Assert.Equal(FieldTransform.Phone, rules["PhoneBusiness"].Transform);
        Assert.Equal("Mobil", rules["PhoneMobile"].SourceExpression);
        Assert.Equal("Fax", rules["Fax"].SourceExpression);
        Assert.Equal("E-Mail", rules["Email"].SourceExpression);
        Assert.Equal("Straße", rules["Street"].SourceExpression);
        Assert.Equal("PLZ", rules["PostalCode"].SourceExpression);
        Assert.Equal("Ort", rules["City"].SourceExpression);
        Assert.Equal("Land", rules["Country"].SourceExpression);
        Assert.Equal("Geburtstag", rules["Birthday"].SourceExpression);
        Assert.Equal("Bemerkung", rules["Notes"].SourceExpression);
        Assert.Equal("KdNr", rules["CustomerNumber"].SourceExpression);
        Assert.Equal(FieldTransform.None, rules["FirstName"].Transform);
    }

    [Fact]
    public void Auto_map_knows_english_and_nested_names()
    {
        string[] fields = ["id", "first_name", "lastName", "company.name", "company.department", "emailAddress", "phoneNumbers.mobile", "work_phone", "address.street", "address.zip", "address.city", "address.country", "website"];
        var rules = FieldMapper.AutoMap(fields, []).ToDictionary(r => r.TargetField, r => r.SourceExpression);
        Assert.Equal("first_name", rules["FirstName"]);
        Assert.Equal("lastName", rules["LastName"]);
        Assert.Equal("company.name", rules["Company"]);
        Assert.Equal("company.department", rules["Department"]);
        Assert.Equal("emailAddress", rules["Email"]);
        Assert.Equal("phoneNumbers.mobile", rules["PhoneMobile"]);
        Assert.Equal("work_phone", rules["PhoneBusiness"]);
        Assert.Equal("address.street", rules["Street"]);
        Assert.Equal("address.zip", rules["PostalCode"]);
        Assert.Equal("address.city", rules["City"]);
        Assert.Equal("address.country", rules["Country"]);
        Assert.Equal("website", rules["Website"]);
        Assert.False(rules.ContainsValue("id"));
        Assert.Equal("id", FieldMapper.SuggestKey(fields));
    }

    [Fact]
    public void Auto_map_keeps_what_is_mapped_and_uses_every_source_field_once()
    {
        string[] fields = ["Vorname", "Name", "Telefon"];
        var rules = FieldMapper.AutoMap(fields, [new("FirstName", "Name", FieldTransform.None), new("LastName", "", FieldTransform.None)]);
        Assert.DoesNotContain(rules, r => r.TargetField == "FirstName");
        Assert.DoesNotContain(rules, r => r.SourceExpression == "Name");
        Assert.Contains(rules, r => r.TargetField == "PhoneBusiness" && r.SourceExpression == "Telefon");
        Assert.Equal(rules.Count, rules.Select(r => r.SourceExpression).Distinct().Count());
    }

    [Fact]
    public void Every_target_has_the_length_of_its_column()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost").Options);
        var entity = db.Model.FindEntityType(typeof(Contact))!;
        foreach (var target in FieldMapper.Targets.Where(t => !t.IsDate))
        {
            var property = entity.FindProperty(target.Name);
            Assert.NotNull(property);
            Assert.Equal(property.GetMaxLength(), target.MaxLength);
        }

        Assert.Equal(entity.FindProperty(nameof(Contact.ExternalKey))!.GetMaxLength(), FieldMapper.MaxKeyLength);
    }

    [Theory]
    [InlineData("SELECT Id, Name FROM dbo.Contacts")]
    [InlineData("  select * from Contacts where Name like '%;%' ;  ")]
    [InlineData("WITH c AS (SELECT * FROM Contacts) SELECT * FROM c")]
    [InlineData("SELECT [Delete], \"Update\" FROM t -- ; DROP TABLE t")]
    [InlineData("SELECT * FROM t /* INSERT INTO x */ WHERE Enabled = 1")]
    [InlineData("SELECT * FROM t WHERE Name = 'O''Brien; DROP'")]
    public void A_select_is_taken(string query) => Assert.Null(SqlServerConnector.CheckQuery(query));

    [Theory]
    [InlineData("")]
    [InlineData("DELETE FROM Contacts")]
    [InlineData("SELECT 1; DROP TABLE Contacts")]
    [InlineData("SELECT * INTO Copy FROM Contacts")]
    [InlineData("SELECT 1 EXEC sp_configure")]
    [InlineData("WITH x AS (SELECT 1 AS a) UPDATE t SET a = 1")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'y')")]
    [InlineData("EXEC dbo.GetContacts")]
    [InlineData("SELECT 1 WAITFOR DELAY '00:01'")]
    [InlineData("SELECT xp_cmdshell")]
    public void A_query_that_may_change_something_is_refused(string query) => Assert.NotNull(SqlServerConnector.CheckQuery(query));

    [Fact]
    public void The_values_of_sql_server_become_text()
    {
        Assert.Equal("2026-11-24", SqlServerConnector.ValueOf(new DateTime(2026, 11, 24)));
        Assert.Equal("2026-11-24T08:30:00", SqlServerConnector.ValueOf(new DateTime(2026, 11, 24, 8, 30, 0)));
        Assert.Equal("1234.5", SqlServerConnector.ValueOf(1234.5m));
        Assert.Equal("true", SqlServerConnector.ValueOf(true));
        Assert.Null(SqlServerConnector.ValueOf(new byte[] { 1, 2 }));
    }
}
