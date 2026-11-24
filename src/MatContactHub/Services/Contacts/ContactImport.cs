using System.Globalization;
using System.Text;
using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

public enum ImportFormat
{
    VCard = 1,
    Csv = 2,
}

/// <summary>A column of a CSV file and the field of a contact it fills (null: left out).</summary>
public sealed record ImportColumn(string Header, string? Field);

/// <summary>What an import would do: the format, the columns of a CSV file, the first records, how many there are, are wrong or are already there.</summary>
public sealed record ImportPreview(ImportFormat Format, IReadOnlyList<ImportColumn> Columns, IReadOnlyList<ContactForm> Sample, int Records, int Invalid, int Duplicates);

/// <summary>What an import did.</summary>
public sealed record ImportResult(int Created, int Duplicates, int Invalid);

/// <summary>
/// The import of contacts from a vCard file (2.1, 3.0, 4.0) or a simple CSV file (a header row; the delimiter <c>;</c>, <c>,</c> or tab is found; UTF-8 or Windows-1252)
/// into an address book where the actor is an editor: the records become manual contacts. First a preview, then the import. Optionally a record whose e-mail address
/// or telephone number is already in the address book (or earlier in the file) is left out. The file waits between the two steps in a temporary folder, under a name
/// that only its person knows.
/// </summary>
public static class ContactImport
{
    /// <summary>The largest file taken.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The most records of one import.</summary>
    public const int MaxRecords = 20000;

    public const int SampleSize = 20;

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "matcontacthub-import");

    /// <summary>The names a CSV header may have for each field (small letters, only letters and digits, umlauts written out), English, German and Outlook's.</summary>
    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    static ContactImport()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>The text of a file: UTF-8 (with or without a BOM), else Windows-1252.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>vCard when the text has a <c>BEGIN:VCARD</c>, else CSV.</summary>
    public static ImportFormat Detect(string text) => text.Contains("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) ? ImportFormat.VCard : ImportFormat.Csv;

    /// <summary>The records of a file as forms, and the columns of a CSV file.</summary>
    public static (ImportFormat Format, IReadOnlyList<ImportColumn> Columns, IReadOnlyList<ContactForm> Records) Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Detect(text) == ImportFormat.VCard)
        {
            return (ImportFormat.VCard, [], [.. VCard.Read(text).Take(MaxRecords)]);
        }

        var rows = ParseCsv(text);
        if (rows.Count == 0)
        {
            return (ImportFormat.Csv, [], []);
        }

        var columns = rows[0].Select(h => new ImportColumn(h.Trim(), FieldOf(h))).ToList();
        var records = new List<ContactForm>();
        foreach (var row in rows.Skip(1).Take(MaxRecords))
        {
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var contact = new Contact();
            DateOnly? birthday = null;
            for (var i = 0; i < columns.Count && i < row.Count; i++)
            {
                var value = ContactFields.Clean(row[i]);
                if (value is null || columns[i].Field is not { } name)
                {
                    continue;
                }

                if (name == "Birthday")
                {
                    birthday ??= ParseDate(value);
                }
                else if (ContactFields.Find(name) is { } field && field.Get(contact) is null or "")
                {
                    field.Set(contact, value);
                }
            }

            var form = ContactForm.Of(contact);
            form.DisplayName = ContactFields.Clean(contact.DisplayName);
            form.Birthday = birthday;
            records.Add(form);
        }

        return (ImportFormat.Csv, columns, records);
    }

    /// <summary>What the import of <paramref name="text"/> into the address book would do (null when the actor is no editor of it).</summary>
    public static async Task<ImportPreview?> PreviewAsync(AppDbContext db, Actor actor, long addressBookId, string text, bool skipDuplicates, CancellationToken ct = default)
    {
        if (await AddressBookService.RoleAsync(db, actor, addressBookId, ct) < AddressBookRole.Editor)
        {
            return null;
        }

        var (format, columns, records) = Read(text);
        var plan = await PlanAsync(db, addressBookId, records, skipDuplicates, ct);
        return new ImportPreview(format, columns, [.. plan.Take.Take(SampleSize)], records.Count, plan.Invalid, plan.Duplicates);
    }

    /// <summary>Imports <paramref name="text"/> into the address book as manual contacts (the actor must be an editor of it).</summary>
    public static async Task<Outcome<ImportResult>> ImportAsync(AppDbContext db, Actor actor, long addressBookId, string text, bool skipDuplicates, CancellationToken ct = default)
    {
        if (await AddressBookService.RoleAsync(db, actor, addressBookId, ct) < AddressBookRole.Editor)
        {
            return Outcome<ImportResult>.Denied();
        }

        var (_, _, records) = Read(text);
        if (records.Count == 0)
        {
            return Outcome<ImportResult>.Fail(Text.T("Contacts.Import.Empty"));
        }

        var plan = await PlanAsync(db, addressBookId, records, skipDuplicates, ct);
        foreach (var form in plan.Take)
        {
            var contact = new Contact { AddressBookId = addressBookId, CreateUserId = actor.UserId };
            form.CopyTo(contact);
            contact.DisplayName = ContactService.DisplayNameOf(form.DisplayName, contact.FirstName, contact.LastName, contact.Company);
            db.Contacts.Add(contact);
        }

        await db.SaveChangesAsync(ct);
        var book = await db.AddressBooks.AsNoTracking().Where(b => b.Id == addressBookId).Select(b => b.Name).FirstOrDefaultAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", book, ActivityLog.Text("Contacts.Import.Done", plan.Take.Count, plan.Duplicates, plan.Invalid), null, ct);
        return Outcome<ImportResult>.Success(new ImportResult(plan.Take.Count, plan.Duplicates, plan.Invalid));
    }

    /// <summary>Keeps the text of an uploaded file until the import; answers its key (only this person can take it again).</summary>
    public static string Stash(Actor actor, string text)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Directory.CreateDirectory(Folder);
        foreach (var old in Directory.EnumerateFiles(Folder).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-2)))
        {
            TryDelete(old);
        }

        var key = Guid.NewGuid().ToString("N");
        File.WriteAllText(PathOf(actor, key), text, Encoding.UTF8);
        return key;
    }

    /// <summary>The text kept under <paramref name="key"/> for this person, or null (gone, or someone else's).</summary>
    public static string? Peek(Actor actor, string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 32 || !key.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        var path = PathOf(actor, key);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    /// <summary>Forgets the text kept under <paramref name="key"/>.</summary>
    public static void Drop(Actor actor, string? key)
    {
        if (Peek(actor, key) is not null)
        {
            TryDelete(PathOf(actor, key!));
        }
    }

    /// <summary>The field of a CSV header (<c>Vorname</c>, <c>First Name</c>, <c>E-mail Address</c> …), or null.</summary>
    public static string? FieldOf(string header)
    {
        var key = Key(header);
        if (key.Length == 0)
        {
            return null;
        }

        if (Aliases.TryGetValue(key, out var field))
        {
            return field;
        }

        return key == "birthday" ? "Birthday" : ContactFields.All.FirstOrDefault(f => Key(f.Name) == key)?.Name;
    }

    /// <summary>The rows of a CSV text (RFC 4180: quotes, doubled quotes, line breaks in quotes); the delimiter is the one of <c>;</c>, <c>,</c> and tab the header has most of.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ';', ',', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
            }
            else if (ch == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (ch == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        if (rows.Count > 0 && rows[0].Count > 0 && rows[0][0].Length > 0 && rows[0][0][0] == '﻿')
        {
            rows[0][0] = rows[0][0][1..];
        }

        return rows.Where(r => r.Count > 1 || (r.Count == 1 && r[0].Length > 0)).ToList();
    }

    private sealed record Plan(List<ContactForm> Take, int Invalid, int Duplicates);

    /// <summary>Which records are taken: the valid ones, without those whose e-mail address or number is already in the address book or earlier in the file.</summary>
    private static async Task<Plan> PlanAsync(AppDbContext db, long addressBookId, IReadOnlyList<ContactForm> records, bool skipDuplicates, CancellationToken ct)
    {
        var country = await ContactService.DefaultCountryAsync(db, ct);
        var mails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phones = new HashSet<string>(StringComparer.Ordinal);
        if (skipDuplicates)
        {
            var existing = await db.Contacts.AsNoTracking().Where(c => c.AddressBookId == addressBookId)
                .Select(c => new { c.Email, c.Email2, c.Email3, c.PhoneBusiness, c.PhoneBusiness2, c.PhoneMobile, c.PhoneHome, c.PhoneOther })
                .ToListAsync(ct);
            foreach (var c in existing)
            {
                Remember(mails, phones, [c.Email, c.Email2, c.Email3], [c.PhoneBusiness, c.PhoneBusiness2, c.PhoneMobile, c.PhoneHome, c.PhoneOther], country);
            }
        }

        var take = new List<ContactForm>();
        var invalid = 0;
        var duplicates = 0;
        foreach (var form in records)
        {
            form.AddressBookId = addressBookId;
            if (ContactService.Validate(form).Count > 0)
            {
                invalid++;
                continue;
            }

            string?[] formMails = [form.Email, form.Email2, form.Email3];
            string?[] formPhones = [form.PhoneBusiness, form.PhoneBusiness2, form.PhoneMobile, form.PhoneHome, form.PhoneOther];
            if (skipDuplicates && (formMails.Any(m => m is not null && mails.Contains(m.Trim()))
                                   || formPhones.Any(p => PhoneNormalizer.Normalize(p, country) is { } n && phones.Contains(n))))
            {
                duplicates++;
                continue;
            }

            if (skipDuplicates)
            {
                Remember(mails, phones, formMails, formPhones, country);
            }

            take.Add(form);
        }

        return new Plan(take, invalid, duplicates);
    }

    private static void Remember(HashSet<string> mails, HashSet<string> phones, string?[] newMails, string?[] newPhones, string country)
    {
        foreach (var m in newMails.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            mails.Add(m!.Trim());
        }

        foreach (var p in newPhones)
        {
            if (PhoneNormalizer.Normalize(p, country) is { } n)
            {
                phones.Add(n);
            }
        }
    }

    private static DateOnly? ParseDate(string value)
        => DateOnly.TryParseExact(value.Trim(), ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyyMMdd", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateOnly.FromDateTime(dt) : null;

    private static string Key(string header)
    {
        var lower = header.Trim().ToLowerInvariant()
            .Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal);
        return new string([.. lower.Where(char.IsAsciiLetterOrDigit)]);
    }

    private static string PathOf(Actor actor, string key) => Path.Combine(Folder, $"{actor.UserId?.ToString(CultureInfo.InvariantCulture) ?? "x"}-{key}.txt");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // another request has it open; the next clean-up takes it
        }
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string[]>
        {
            ["Salutation"] = ["salutation", "anrede", "prefix", "namensprefix"],
            ["Title"] = ["title", "titel", "academictitle", "akademischertitel"],
            ["FirstName"] = ["firstname", "first", "givenname", "vorname"],
            ["MiddleName"] = ["middlename", "additionalname", "weiterevornamen", "zweitervorname"],
            ["LastName"] = ["lastname", "last", "surname", "familyname", "nachname", "familienname"],
            ["DisplayName"] = ["displayname", "name", "fullname", "anzeigename", "fn", "vollstaendigername"],
            ["Company"] = ["company", "organization", "organisation", "firma", "unternehmen", "org", "companyname", "firmenname"],
            ["Department"] = ["department", "abteilung"],
            ["JobTitle"] = ["jobtitle", "position", "funktion", "berufsbezeichnung", "role", "rolle"],
            ["Email"] = ["email", "mail", "emailaddress", "email1", "emailadresse", "email1address", "primaryemail"],
            ["Email2"] = ["email2", "email2address", "email2adresse"],
            ["Email3"] = ["email3", "email3address", "email3adresse"],
            ["PhoneBusiness"] = ["phone", "telephone", "telefon", "tel", "telefonnummer", "businessphone", "workphone", "phonebusiness", "telefongeschaeftlich", "geschaeftlich", "phonenumber"],
            ["PhoneBusiness2"] = ["businessphone2", "workphone2", "phonebusiness2", "telefongeschaeftlich2", "telefon2"],
            ["PhoneMobile"] = ["mobile", "mobilephone", "cell", "cellphone", "handy", "mobil", "mobiltelefon", "mobilnummer", "phonemobile"],
            ["PhoneHome"] = ["homephone", "phonehome", "privatephone", "telefonprivat", "privat"],
            ["PhoneOther"] = ["otherphone", "phoneother", "weiterestelefon", "telefonandere", "andere"],
            ["Fax"] = ["fax", "telefax", "businessfax", "faxgeschaeftlich", "faxnumber"],
            ["Street"] = ["street", "strasse", "address", "adresse", "businessstreet", "strassegeschaeftlich", "streetaddress"],
            ["PostalCode"] = ["postalcode", "zip", "zipcode", "plz", "postleitzahl", "businesspostalcode", "postleitzahlgeschaeftlich"],
            ["City"] = ["city", "ort", "stadt", "town", "businesscity", "ortgeschaeftlich"],
            ["State"] = ["state", "region", "bundesland", "province", "businessstate"],
            ["Country"] = ["country", "land", "businesscountry", "businesscountryregion", "landregiongeschaeftlich", "countryregion"],
            ["Website"] = ["website", "web", "url", "homepage", "webseite", "webpage", "internet"],
            ["Birthday"] = ["birthday", "geburtstag", "bday", "dateofbirth", "geburtsdatum"],
            ["CustomerNumber"] = ["customernumber", "customerno", "customerid", "kundennummer", "kundennr"],
            ["Notes"] = ["notes", "note", "notizen", "notiz", "bemerkung", "bemerkungen", "comment", "comments", "kommentar"],
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, names) in map)
        {
            foreach (var name in names)
            {
                result[name] = field;
            }
        }

        return result;
    }
}
