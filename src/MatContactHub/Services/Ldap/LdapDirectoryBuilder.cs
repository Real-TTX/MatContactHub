using System.Globalization;
using MatContactHub.Data;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Ldap;

/// <summary>An LDAP-enabled address book, as the snapshot needs it.</summary>
public sealed record LdapBookRow(long Id, string Name, string LdapName, string? Description);

/// <summary>A contact, as the snapshot needs it (the fields that become attributes).</summary>
public sealed record LdapContactRow(
    Guid Token, long AddressBookId, string DisplayName, string? FirstName, string? LastName, string? Company, string? Department, string? JobTitle,
    string? Email, string? Email2, string? Email3, string? PhoneBusiness, string? PhoneBusiness2, string? PhoneMobile, string? PhoneHome, string? PhoneOther,
    string? Fax, string? Street, string? PostalCode, string? City, string? State, string? Country, string? Website, string? Notes, DateTime CreateDate,
    DateTime UpdateDate);

/// <summary>
/// Makes the in-memory directory (<see cref="LdapDirectory"/>): <see cref="LoadAsync"/> reads the database (read-only, no tracking), <see cref="Build"/> turns rows
/// into entries (pure, also for the tests). A contact becomes an <c>inetOrgPerson</c>: cn, sn, givenName, displayName, mail, telephoneNumber, mobile, homePhone,
/// facsimileTelephoneNumber, otherTelephone, o, ou (department), title, street, postalCode, l, st, c / co, labeledURI, description (notes), uid and entryUUID
/// (the token). The numbers are written in <see cref="LdapSettings.NumberFormat"/> and compared as numbers.
/// </summary>
public static class LdapDirectoryBuilder
{
    /// <summary>An empty directory (before the first build): nothing but the root DSE and a base.</summary>
    public static LdapDirectory Empty { get; } = Build(0, new LdapSettings(false, false, false, LdapSettings.DefaultBaseDn, false, NumberFormat.AsStored,
        PhoneNormalizer.FallbackCountry, LdapSettings.DefaultSizeLimit, LdapCertificateSource.SelfSigned), [], [], []);

    /// <summary>Reads the directory from the database.</summary>
    public static async Task<LdapDirectory> LoadAsync(AppDbContext db, long version, CancellationToken ct)
    {
        var settings = await LdapSettingsService.LoadAsync(db, ct);
        var books = await db.AddressBooks.AsNoTracking().Where(b => b.LdapEnabled).OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new LdapBookRow(b.Id, b.Name, b.LdapName, b.Description)).ToListAsync(ct);
        var ids = books.Select(b => b.Id).ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => !c.Hidden && ids.Contains(c.AddressBookId))
            .Select(c => new LdapContactRow(c.Token, c.AddressBookId, c.DisplayName, c.FirstName, c.LastName, c.Company, c.Department, c.JobTitle, c.Email, c.Email2,
                c.Email3, c.PhoneBusiness, c.PhoneBusiness2, c.PhoneMobile, c.PhoneHome, c.PhoneOther, c.Fax, c.Street, c.PostalCode, c.City, c.State, c.Country,
                c.Website, c.Notes, c.CreateDate, c.UpdateDate))
            .ToListAsync(ct);
        var accounts = await db.LdapAccounts.AsNoTracking()
            .Select(a => new { a.Id, a.Name, a.PasswordHash, a.Enabled, a.AllAddressBooks, Books = a.AddressBooks.Select(x => x.AddressBookId).ToList() })
            .ToListAsync(ct);
        return Build(version, settings, books, contacts,
            accounts.Select(a => new LdapAccountInfo(a.Id, a.Name, a.PasswordHash, a.Enabled, a.AllAddressBooks, a.Books.ToHashSet())));
    }

    /// <summary>Makes the directory of rows.</summary>
    public static LdapDirectory Build(long version, LdapSettings settings, IEnumerable<LdapBookRow> books, IEnumerable<LdapContactRow> contacts,
        IEnumerable<LdapAccountInfo> accounts)
    {
        var baseDn = LdapSettingsService.IsBaseDn(settings.BaseDn) ? settings.BaseDn.Trim() : LdapSettings.DefaultBaseDn;
        var phone = new LdapPhone(settings.DefaultCountry);
        var bookList = books.ToList();
        var byBook = contacts.GroupBy(c => c.AddressBookId).ToDictionary(g => g.Key, g => g.ToList());

        var bookEntries = new List<LdapEntry>();
        var contactEntries = new Dictionary<long, LdapEntry[]>();
        foreach (var book in bookList)
        {
            var bookDn = $"ou={LdapDn.Escape(book.LdapName)},{baseDn}";
            var members = byBook.GetValueOrDefault(book.Id) ?? [];
            contactEntries[book.Id] = [.. members.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(c => ContactEntry(c, bookDn, settings, phone))];
            bookEntries.Add(new LdapEntry(LdapEntryKind.AddressBook, bookDn, book.Id,
            [
                V(LdapAttributes.ObjectClass, "top", "organizationalUnit"),
                V(LdapAttributes.Ou, book.LdapName),
                V(LdapAttributes.Description, string.IsNullOrWhiteSpace(book.Description) ? book.Name : $"{book.Name}: {book.Description}"),
                V(LdapAttributes.EntryDn, bookDn),
                V(LdapAttributes.StructuralObjectClass, "organizationalUnit"),
                V(LdapAttributes.HasSubordinates, members.Count > 0 ? "TRUE" : "FALSE"),
                V(LdapAttributes.SubschemaSubentry, SubschemaDn),
            ]));
        }

        var accountMap = new Dictionary<string, LdapAccountInfo>(StringComparer.Ordinal);
        foreach (var a in accounts)
        {
            accountMap[a.Name.ToLowerInvariant()] = a;
        }

        return new LdapDirectory(version, settings with { BaseDn = baseDn }, RootDse(baseDn, settings), Subschema(), BaseEntry(baseDn, bookEntries.Count > 0), bookEntries,
            contactEntries, accountMap);
    }

    public const string SubschemaDn = "cn=Subschema";

    private static LdapEntry ContactEntry(LdapContactRow c, string bookDn, LdapSettings settings, LdapPhone phone)
    {
        var token = c.Token.ToString("D");
        var dn = $"uid={token},{bookDn}";
        var display = !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName.Trim()
            : Join(c.FirstName, c.LastName) is { Length: > 0 } names ? names
            : c.Company?.Trim() is { Length: > 0 } company ? company : token;
        var surname = Clean(c.LastName) ?? (string.IsNullOrWhiteSpace(c.FirstName) ? Clean(c.Company) : null) ?? display;
        var country = Clean(c.Country);
        var isCode = country is { Length: 2 } && country.All(char.IsAsciiLetter);
        return new LdapEntry(LdapEntryKind.Contact, dn, c.AddressBookId,
        [
            V(LdapAttributes.ObjectClass, "top", "person", "organizationalPerson", "inetOrgPerson"),
            V(LdapAttributes.Uid, token),
            V(LdapAttributes.Cn, display),
            V(LdapAttributes.Sn, surname),
            V(LdapAttributes.GivenName, c.FirstName),
            V(LdapAttributes.DisplayName, display),
            V(LdapAttributes.Mail, c.Email, c.Email2, c.Email3),
            Phones(LdapAttributes.TelephoneNumber, settings, phone, c.PhoneBusiness, c.PhoneBusiness2),
            Phones(LdapAttributes.Mobile, settings, phone, c.PhoneMobile),
            Phones(LdapAttributes.HomePhone, settings, phone, c.PhoneHome),
            Phones(LdapAttributes.Fax, settings, phone, c.Fax),
            Phones(LdapAttributes.OtherTelephone, settings, phone, c.PhoneOther),
            V(LdapAttributes.O, c.Company),
            V(LdapAttributes.Ou, c.Department),
            V(LdapAttributes.Title, c.JobTitle),
            V(LdapAttributes.Street, c.Street),
            V(LdapAttributes.PostalCode, c.PostalCode),
            V(LdapAttributes.L, c.City),
            V(LdapAttributes.St, c.State),
            V(LdapAttributes.C, isCode ? country!.ToUpperInvariant() : null),
            V(LdapAttributes.Co, isCode ? null : country),
            V(LdapAttributes.LabeledUri, c.Website),
            V(LdapAttributes.Description, c.Notes),
            V(LdapAttributes.EntryUuid, token),
            V(LdapAttributes.EntryDn, dn),
            V(LdapAttributes.StructuralObjectClass, "inetOrgPerson"),
            V(LdapAttributes.HasSubordinates, "FALSE"),
            V(LdapAttributes.SubschemaSubentry, SubschemaDn),
            V(LdapAttributes.CreateTimestamp, GeneralizedTime(c.CreateDate)),
            V(LdapAttributes.ModifyTimestamp, GeneralizedTime(c.UpdateDate)),
        ]);
    }

    private static LdapEntry BaseEntry(string baseDn, bool hasChildren)
    {
        var rdn = LdapDn.Parse(baseDn)?.FirstOrDefault() ?? new LdapRdn("dc", "matcontacthub");
        var (objectClass, attribute) = rdn.Attribute switch
        {
            "dc" => ("domain", LdapAttributes.Dc),
            "o" => ("organization", LdapAttributes.O),
            "ou" => ("organizationalUnit", LdapAttributes.Ou),
            "c" => ("country", LdapAttributes.C),
            "l" => ("locality", LdapAttributes.L),
            _ => ("extensibleObject", LdapAttributes.Canonical(rdn.Attribute)),
        };
        return new LdapEntry(LdapEntryKind.Base, baseDn, 0,
        [
            V(LdapAttributes.ObjectClass, "top", objectClass),
            V(attribute, rdn.Value),
            V(LdapAttributes.EntryDn, baseDn),
            V(LdapAttributes.StructuralObjectClass, objectClass),
            V(LdapAttributes.HasSubordinates, hasChildren ? "TRUE" : "FALSE"),
            V(LdapAttributes.SubschemaSubentry, SubschemaDn),
        ]);
    }

    private static LdapEntry RootDse(string baseDn, LdapSettings settings)
    {
        List<string> extensions = [LdapOids.WhoAmI];
        if (settings.StartTlsEnabled)
        {
            extensions.Insert(0, LdapOids.StartTls);
        }

        return new LdapEntry(LdapEntryKind.RootDse, string.Empty, 0,
        [
            V(LdapAttributes.ObjectClass, "top"),
            V(LdapAttributes.NamingContexts, baseDn),
            V(LdapAttributes.SupportedLdapVersion, "3"),
            V(LdapAttributes.SupportedControl, LdapOids.PagedResults, LdapOids.ManageDsaIt),
            new LdapValues(LdapAttributes.SupportedExtension, [.. extensions]),
            V(LdapAttributes.SupportedFeatures, LdapOids.AllOperationalAttributes),
            V(LdapAttributes.VendorName, Ui.AppName),
            V(LdapAttributes.VendorVersion, AppInfo.Version),
            V(LdapAttributes.SubschemaSubentry, SubschemaDn),
        ]);
    }

    /// <summary>The subschema: the attribute types and object classes of the entries (minimal, enough for a client that reads it).</summary>
    private static LdapEntry Subschema() => new(LdapEntryKind.Subschema, SubschemaDn, 0,
    [
        V(LdapAttributes.ObjectClass, "top", "subentry", "subschema"),
        V(LdapAttributes.Cn, "Subschema"),
        new LdapValues(LdapAttributes.AttributeTypes,
        [
            "( 2.5.4.0 NAME 'objectClass' EQUALITY objectIdentifierMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.41 NAME 'name' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.3 NAME ( 'cn' 'commonName' ) SUP name )",
            "( 2.5.4.4 NAME ( 'sn' 'surname' ) SUP name )",
            "( 2.5.4.42 NAME ( 'givenName' 'gn' ) SUP name )",
            "( 2.16.840.1.113730.3.1.241 NAME 'displayName' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )",
            "( 0.9.2342.19200300.100.1.1 NAME ( 'uid' 'userid' ) EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 0.9.2342.19200300.100.1.3 NAME ( 'mail' 'rfc822Mailbox' ) EQUALITY caseIgnoreIA5Match SUBSTR caseIgnoreIA5SubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.26 )",
            "( 2.5.4.20 NAME 'telephoneNumber' EQUALITY telephoneNumberMatch SUBSTR telephoneNumberSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.50 )",
            "( 0.9.2342.19200300.100.1.41 NAME ( 'mobile' 'mobileTelephoneNumber' ) EQUALITY telephoneNumberMatch SUBSTR telephoneNumberSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.50 )",
            "( 0.9.2342.19200300.100.1.20 NAME ( 'homePhone' 'homeTelephoneNumber' ) EQUALITY telephoneNumberMatch SUBSTR telephoneNumberSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.50 )",
            "( 2.5.4.23 NAME 'facsimileTelephoneNumber' SYNTAX 1.3.6.1.4.1.1466.115.121.1.22 )",
            "( 1.2.840.113556.1.4.18 NAME 'otherTelephone' EQUALITY telephoneNumberMatch SUBSTR telephoneNumberSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.50 )",
            "( 2.5.4.10 NAME ( 'o' 'organizationName' ) SUP name )",
            "( 2.5.4.11 NAME ( 'ou' 'organizationalUnitName' ) SUP name )",
            "( 2.5.4.12 NAME 'title' SUP name )",
            "( 2.5.4.9 NAME ( 'street' 'streetAddress' ) EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.17 NAME 'postalCode' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.7 NAME ( 'l' 'localityName' ) SUP name )",
            "( 2.5.4.8 NAME ( 'st' 'stateOrProvinceName' ) SUP name )",
            "( 2.5.4.6 NAME ( 'c' 'countryName' ) SUP name SINGLE-VALUE )",
            "( 0.9.2342.19200300.100.1.43 NAME ( 'co' 'friendlyCountryName' ) EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 1.3.6.1.4.1.250.1.57 NAME 'labeledURI' EQUALITY caseExactMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.13 NAME 'description' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 1.3.6.1.1.16.4 NAME 'entryUUID' EQUALITY UUIDMatch SYNTAX 1.3.6.1.1.16.1 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        ]),
        new LdapValues(LdapAttributes.ObjectClasses,
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) MAY ( telephoneNumber $ description ) )",
            "( 2.5.6.7 NAME 'organizationalPerson' SUP person STRUCTURAL MAY ( title $ ou $ street $ postalCode $ st $ l $ facsimileTelephoneNumber ) )",
            "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' SUP organizationalPerson STRUCTURAL MAY ( displayName $ givenName $ homePhone $ labeledURI $ mail $ mobile $ o $ uid ) )",
            "( 2.5.6.5 NAME 'organizationalUnit' SUP top STRUCTURAL MUST ou MAY description )",
        ]),
        V(LdapAttributes.EntryDn, SubschemaDn),
        V(LdapAttributes.StructuralObjectClass, "subentry"),
    ]);

    /// <summary>An attribute of the non-empty values (trimmed); an attribute without one is left out of the entry.</summary>
    private static LdapValues V(string name, params string?[] values) => new(name, [.. values.Select(Clean).OfType<string>()]);

    private static LdapValues Phones(string name, LdapSettings settings, LdapPhone phone, params string?[] raw)
    {
        var numbers = raw.Select(Clean).OfType<string>().ToList();
        var shown = numbers.Select(n => PhoneNormalizer.Format(n, settings.NumberFormat, phone.Country) ?? n).ToArray();
        return new LdapValues(name, shown, [.. numbers.Select(phone.Prepare)]);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Join(string? first, string? last) => string.Join(' ', new[] { first, last }.Select(Clean).OfType<string>());

    private static string GeneralizedTime(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).ToString("yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture);
}
