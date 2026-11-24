using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MatContactHub.Data;
using MatContactHub.Services.Contacts;

namespace MatContactHub.Services.Sources;

/// <summary>A field of the contact a source can fill: its name (the property of <see cref="Contact"/>), its group on the page and its longest value.</summary>
public sealed record TargetField(string Name, string Group, int MaxLength, bool IsPhone = false, bool IsDate = false);

/// <summary>One line of the mapping: the field of the contact, where its value comes from and what is done to it.</summary>
public sealed record MappingRule(string TargetField, string SourceExpression, FieldTransform Transform);

/// <summary>A record of the source made a contact: its key and the value of every field of the contact (null = empty).</summary>
public sealed record MappedRecord(string? Key, IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// The mapping of a record of a source onto a contact. A source expression is a field of the source (<c>first_name</c>, <c>address.city</c>; without case) or a
/// template (<c>{first} {last}</c>: every <c>{field}</c> is replaced, the spaces of empty parts collapse); the transform cleans the value (<see cref="FieldTransform"/>;
/// <c>Phone</c> writes E.164 with the default country, a number that cannot be read stays as it was). Every value is trimmed and cut to the length of its column.
/// <see cref="AutoMap"/> suggests the source field of every field of the contact by its name (English and German).
/// </summary>
public static partial class FieldMapper
{
    /// <summary>The groups of the fields on the page, in order.</summary>
    public static readonly string[] Groups = ["Name", "Organisation", "Communication", "Address", "More"];

    /// <summary>Every field of the contact a source can fill (the lengths are those of the columns, <c>AppDbContext</c>).</summary>
    public static readonly IReadOnlyList<TargetField> Targets =
    [
        new("Salutation", "Name", 50),
        new("Title", "Name", 50),
        new("FirstName", "Name", 100),
        new("MiddleName", "Name", 100),
        new("LastName", "Name", 100),
        new("DisplayName", "Name", 255),
        new("Company", "Organisation", 255),
        new("Department", "Organisation", 255),
        new("JobTitle", "Organisation", 255),
        new("CustomerNumber", "Organisation", 100),
        new("Email", "Communication", 254),
        new("Email2", "Communication", 254),
        new("Email3", "Communication", 254),
        new("PhoneBusiness", "Communication", 64, IsPhone: true),
        new("PhoneBusiness2", "Communication", 64, IsPhone: true),
        new("PhoneMobile", "Communication", 64, IsPhone: true),
        new("PhoneHome", "Communication", 64, IsPhone: true),
        new("PhoneOther", "Communication", 64, IsPhone: true),
        new("Fax", "Communication", 64, IsPhone: true),
        new("Website", "Communication", 500),
        new("Street", "Address", 255),
        new("PostalCode", "Address", 20),
        new("City", "Address", 100),
        new("State", "Address", 100),
        new("Country", "Address", 100),
        new("Birthday", "More", 10, IsDate: true),
        new("Notes", "More", 8000),
        new("Custom1", "More", 500),
        new("Custom2", "More", 500),
        new("Custom3", "More", 500),
        new("Custom4", "More", 500),
        new("Custom5", "More", 500),
    ];

    private static readonly Dictionary<string, TargetField> ByName = Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static TargetField? Target(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The longest key of a contact (<see cref="Contact.ExternalKey"/>).</summary>
    public const int MaxKeyLength = 255;

    /// <summary>Is the expression a template (<c>{a} {b}</c>) rather than the name of a field?</summary>
    public static bool IsTemplate(string? expression) => expression is not null && PlaceholderRegex().IsMatch(expression);

    /// <summary>The fields an expression reads: the field itself, or the fields of the placeholders of a template.</summary>
    public static IEnumerable<string> FieldsOf(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [];
        }

        return IsTemplate(expression) ? PlaceholderRegex().Matches(expression).Select(m => m.Groups["name"].Value.Trim()) : [expression.Trim()];
    }

    /// <summary>The value of an expression for a record (trimmed; null when it is empty).</summary>
    public static string? Evaluate(string? expression, IReadOnlyDictionary<string, string?> record)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        string value;
        if (IsTemplate(expression))
        {
            var filled = PlaceholderRegex().Replace(expression, m => record.TryGetValue(m.Groups["name"].Value.Trim(), out var v) ? v ?? string.Empty : string.Empty);

            // the parts that were empty leave no double spaces behind
            value = WhitespaceRegex().Replace(filled, " ");
        }
        else
        {
            value = record.TryGetValue(expression.Trim(), out var v) ? v ?? string.Empty : string.Empty;
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>A transform applied to a value (null stays null).</summary>
    public static string? Apply(FieldTransform transform, string? value, string? defaultCountry)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return transform switch
        {
            FieldTransform.Trim => WhitespaceRegex().Replace(value, " "),
            FieldTransform.Phone => PhoneNormalizer.Normalize(value, defaultCountry) ?? value,
            FieldTransform.Lower => value.ToLower(CultureInfo.InvariantCulture),
            FieldTransform.Upper => value.ToUpper(CultureInfo.InvariantCulture),
            FieldTransform.Capitalize => Capitalize(value),
            _ => value,
        };
    }

    /// <summary>Every word with a capital first letter and the rest small (<c>MÜLLER-LÜDENSCHEID</c> → <c>Müller-Lüdenscheid</c>).</summary>
    public static string Capitalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var start = true;
        foreach (var c in value)
        {
            if (char.IsLetter(c))
            {
                sb.Append(start ? char.ToUpper(c, CultureInfo.InvariantCulture) : char.ToLower(c, CultureInfo.InvariantCulture));
                start = false;
            }
            else
            {
                sb.Append(c);
                start = !char.IsDigit(c) && c != '\'';
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A record made a contact: the key (the key expression, often one field) and every field of the contact (those without a rule are null). A date is written
    /// <c>yyyy-MM-dd</c> (null when it cannot be read); every value is cut to the length of its column.
    /// </summary>
    public static MappedRecord Map(IReadOnlyDictionary<string, string?> record, string keyExpression, IEnumerable<MappingRule> rules, string? defaultCountry)
    {
        var values = Targets.ToDictionary(t => t.Name, _ => (string?)null, StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (Target(rule.TargetField) is not { } target)
            {
                continue;
            }

            var value = Apply(rule.Transform, Evaluate(rule.SourceExpression, record), defaultCountry);
            if (target.IsDate)
            {
                value = ParseDate(value)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            values[target.Name] = Cut(value, target.MaxLength);
        }

        return new MappedRecord(Evaluate(keyExpression, record), values);
    }

    /// <summary>A date as sources write it: ISO (<c>2026-11-24</c>, also with a time), German (<c>24.11.2026</c>) or American (<c>11/24/2026</c>).</summary>
    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "dd.MM.yyyy", "d.M.yyyy", "MM/dd/yyyy", "M/d/yyyy", "yyyyMMdd", "yyyy/MM/dd"];
        var text = value.Trim();
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp)
            ? DateOnly.FromDateTime(stamp)
            : null;
    }

    /// <summary>The hash of the mapped values and the address book (<see cref="Contact.SyncHash"/>): the same hash, nothing to write.</summary>
    public static string Hash(long addressBookId, IReadOnlyDictionary<string, string?> values)
    {
        var sb = new StringBuilder();
        sb.Append("ab=").Append(addressBookId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var target in Targets)
        {
            sb.Append(target.Name).Append('=').Append(values.GetValueOrDefault(target.Name)?.Replace("\n", "\\n", StringComparison.Ordinal)).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Writes the mapped values into a contact (every field a source fills; <see cref="Contact.Hidden"/> and the rest stay as they are).</summary>
    public static void Write(Contact c, IReadOnlyDictionary<string, string?> values)
    {
        string? V(string name) => values.GetValueOrDefault(name);
        c.Salutation = V("Salutation");
        c.Title = V("Title");
        c.FirstName = V("FirstName");
        c.MiddleName = V("MiddleName");
        c.LastName = V("LastName");
        c.Company = V("Company");
        c.Department = V("Department");
        c.JobTitle = V("JobTitle");
        c.CustomerNumber = V("CustomerNumber");
        c.Email = V("Email");
        c.Email2 = V("Email2");
        c.Email3 = V("Email3");
        c.PhoneBusiness = V("PhoneBusiness");
        c.PhoneBusiness2 = V("PhoneBusiness2");
        c.PhoneMobile = V("PhoneMobile");
        c.PhoneHome = V("PhoneHome");
        c.PhoneOther = V("PhoneOther");
        c.Fax = V("Fax");
        c.Website = V("Website");
        c.Street = V("Street");
        c.PostalCode = V("PostalCode");
        c.City = V("City");
        c.State = V("State");
        c.Country = V("Country");
        c.Birthday = ParseDate(V("Birthday"));
        c.Notes = V("Notes");
        c.Custom1 = V("Custom1");
        c.Custom2 = V("Custom2");
        c.Custom3 = V("Custom3");
        c.Custom4 = V("Custom4");
        c.Custom5 = V("Custom5");
        var name = ContactService.DisplayNameOf(V("DisplayName"), c.FirstName, c.LastName, c.Company);
        c.DisplayName = Cut(name.Length > 0 ? name : c.Email ?? c.PhoneBusiness ?? c.PhoneMobile ?? c.ExternalKey ?? string.Empty, 255)!;
    }

    /// <summary>Is anything of the contact filled?</summary>
    public static bool HasAnyValue(MappedRecord mapped) => mapped.Values.Values.Any(v => v is not null);

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    // ---------------------------------------------------------------------------------------------------- auto-map

    /// <summary>The names a field of the source may have for each field of the contact (English and German; compared folded, see <see cref="Fold"/>).</summary>
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["Salutation"] = ["salutation", "anrede", "honorificprefix", "prefix", "greeting", "briefanrede"],
        ["Title"] = ["titel", "academictitle", "akademischertitel", "degree", "grad", "title"],
        ["FirstName"] = ["firstname", "first", "givenname", "given", "vorname", "forename", "fname", "rufname"],
        ["MiddleName"] = ["middlename", "middle", "zweitername", "weiterevornamen", "additionalname"],
        ["LastName"] = ["lastname", "last", "surname", "familyname", "family", "nachname", "familienname", "lname", "sn", "zuname"],
        ["DisplayName"] = ["displayname", "fullname", "name", "anzeigename", "vollername", "cn", "commonname", "kontakt", "contact", "contactname"],
        ["Company"] = ["company", "firma", "companyname", "organization", "organisation", "org", "unternehmen", "firmenname", "employer", "accountname", "account", "o", "firma1", "name1"],
        ["Department"] = ["department", "abteilung", "dept", "division", "bereich", "ou", "team"],
        ["JobTitle"] = ["jobtitle", "position", "funktion", "function", "role", "rolle", "stellung", "beruf", "jobposition", "stelle"],
        ["CustomerNumber"] = ["customernumber", "customerno", "customernr", "kundennummer", "kundennr", "kdnr", "customerid", "accountnumber", "debitor", "debitornummer", "debitorennummer", "kunde"],
        ["Email"] = ["email", "mail", "emailaddress", "emailadresse", "email1", "mail1", "businessemail", "workemail", "emailwork", "emailgeschaeftlich", "mailadresse"],
        ["Email2"] = ["email2", "mail2", "privateemail", "emailprivate", "emailprivat", "homeemail"],
        ["Email3"] = ["email3", "mail3", "otheremail"],
        ["PhoneBusiness"] = ["phone", "telephone", "telefon", "tel", "phonenumber", "telefonnummer", "telnr", "businessphone", "workphone", "phonework", "phonebusiness", "officephone", "telefongeschaeftlich", "telephonenumber", "festnetz", "telefon1", "phone1", "durchwahl"],
        ["PhoneBusiness2"] = ["phone2", "telefon2", "tel2", "businessphone2", "telefongeschaeftlich2", "phonebusiness2"],
        ["PhoneMobile"] = ["mobile", "mobil", "cell", "cellphone", "mobilephone", "handy", "mobiltelefon", "mobilnummer", "handynummer", "phonemobile", "mobilephonenumber", "mobilenumber", "mobiletelephonenumber"],
        ["PhoneHome"] = ["homephone", "phonehome", "telefonprivat", "privattelefon", "privat", "home", "privatephone", "hometelephonenumber"],
        ["PhoneOther"] = ["otherphone", "phoneother", "telefonsonstige", "sonstigetelefon", "weiteretelefon", "telefonweitere"],
        ["Fax"] = ["fax", "telefax", "faxnumber", "faxnummer", "facsimile", "facsimiletelephonenumber", "faxnr"],
        ["Website"] = ["website", "web", "url", "homepage", "www", "internet", "webseite", "internetseite", "site"],
        ["Street"] = ["street", "strasse", "str", "address", "adresse", "streetaddress", "addressline1", "address1", "anschrift", "strassehausnummer", "street1", "line1"],
        ["PostalCode"] = ["postalcode", "zip", "zipcode", "plz", "postcode", "postleitzahl", "postal"],
        ["City"] = ["city", "ort", "town", "stadt", "locality", "wohnort", "l", "place"],
        ["State"] = ["state", "region", "bundesland", "province", "county", "st", "kanton"],
        ["Country"] = ["country", "land", "countrycode", "countryname", "staat", "c", "co", "nation"],
        ["Birthday"] = ["birthday", "birthdate", "dateofbirth", "geburtstag", "geburtsdatum", "dob", "born"],
        ["Notes"] = ["notes", "note", "notiz", "notizen", "bemerkung", "bemerkungen", "comment", "comments", "kommentar", "description", "beschreibung", "info", "remarks"],
        ["Custom1"] = ["custom1", "benutzerdefiniert1", "frei1", "extra1", "zusatz1"],
        ["Custom2"] = ["custom2", "benutzerdefiniert2", "frei2", "extra2", "zusatz2"],
        ["Custom3"] = ["custom3", "benutzerdefiniert3", "frei3", "extra3", "zusatz3"],
        ["Custom4"] = ["custom4", "benutzerdefiniert4", "frei4", "extra4", "zusatz4"],
        ["Custom5"] = ["custom5", "benutzerdefiniert5", "frei5", "extra5", "zusatz5"],
    };

    /// <summary>The names a key field usually has.</summary>
    private static readonly string[] KeyNames = ["id", "uuid", "guid", "key", "externalid", "recordid", "contactid", "personid", "kontaktid", "nummer", "nr", "number", "objectid", "uid"];

    /// <summary>A name folded for comparing: small letters, umlauts written out (ä → ae, ß → ss), only letters and digits.</summary>
    public static string Fold(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            switch (c)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'ß': sb.Append("ss"); break;
                case >= 'a' and <= 'z' or >= '0' and <= '9': sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>How well a field of the source fits a field of the contact: 0 = not at all.</summary>
    public static int Score(string targetField, string sourceField)
    {
        if (!Synonyms.TryGetValue(targetField, out var names))
        {
            return 0;
        }

        var full = Fold(sourceField);
        var dot = sourceField.LastIndexOf('.');
        var last = dot >= 0 ? Fold(sourceField[(dot + 1)..]) : full;
        var best = 0;
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];

            // the earlier a name is in the list, the better it fits
            var bonus = names.Length - i;
            if (full == name || full == Fold(targetField))
            {
                best = Math.Max(best, 1000 + bonus);
            }
            else if (last == name)
            {
                best = Math.Max(best, 800 + bonus);
            }
            else if (name.Length >= 5 && full.Contains(name, StringComparison.Ordinal))
            {
                best = Math.Max(best, 300 + name.Length * 10 - (full.Length - name.Length));
            }
        }

        return best;
    }

    /// <summary>
    /// Suggests a rule for every field of the contact that has none yet (<paramref name="taken"/>), from the fields of the source: the best pairs first, every
    /// field of the source used once. A telephone field gets the transform Phone.
    /// </summary>
    public static IReadOnlyList<MappingRule> AutoMap(IEnumerable<string> sourceFields, IEnumerable<MappingRule> taken)
    {
        var used = taken.Where(r => !string.IsNullOrWhiteSpace(r.SourceExpression)).ToList();
        var busyTargets = used.Select(r => r.TargetField).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var busySources = used.SelectMany(r => FieldsOf(r.SourceExpression)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fields = sourceFields.Where(f => !busySources.Contains(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pairs = (from target in Targets
                     where !busyTargets.Contains(target.Name)
                     from field in fields
                     let score = Score(target.Name, field)
                     where score > 0
                     orderby score descending
                     select (Target: target, Field: field, Score: score)).ToList();
        var result = new List<MappingRule>();
        foreach (var (target, field, _) in pairs)
        {
            if (busyTargets.Contains(target.Name) || busySources.Contains(field))
            {
                continue;
            }

            busyTargets.Add(target.Name);
            busySources.Add(field);
            result.Add(new MappingRule(target.Name, field, target.IsPhone ? FieldTransform.Phone : FieldTransform.None));
        }

        return result;
    }

    /// <summary>The field that is most likely the key of the records (<c>id</c>, <c>uuid</c>, <c>Kundennummer</c> …), or null.</summary>
    public static string? SuggestKey(IEnumerable<string> sourceFields)
    {
        var fields = sourceFields.ToList();
        foreach (var name in KeyNames)
        {
            var exact = fields.FirstOrDefault(f => Fold(f) == name);
            if (exact is not null)
            {
                return exact;
            }
        }

        return fields.FirstOrDefault(f => !f.Contains('.', StringComparison.Ordinal) && Fold(f).EndsWith("id", StringComparison.Ordinal))
            ?? fields.FirstOrDefault(f => Score("CustomerNumber", f) >= 800);
    }

    [GeneratedRegex(@"\{(?<name>[^{}]+)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
