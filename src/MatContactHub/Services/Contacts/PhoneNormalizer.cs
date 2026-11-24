using MatContactHub.Data;
using PhoneNumbers;
using NumberFormat = MatContactHub.Data.NumberFormat;

namespace MatContactHub.Services.Contacts;

/// <summary>
/// THE telephone numbers of MatContactHub (shared by every module: the sync transform <c>Phone</c>, the duplicates, the LDAP server and its filters, the directory API),
/// on libphonenumber (the package <c>libphonenumber-csharp</c>). A number is compared in E.164 (<c>+493012345678</c>): <c>030 12345678</c>, <c>+49 (30) 1234-5678</c>,
/// <c>0049 30 12345678</c> and <c>+49 30 12345678</c> are the same number with the default country DE. Thread-safe and without state of its own.
/// </summary>
public static class PhoneNormalizer
{
    /// <summary>The default country when a setting has none.</summary>
    public const string FallbackCountry = "DE";

    /// <summary>The fewest digits of a number after its country code: shorter ones are extensions (<c>12</c>), not numbers (libphonenumber takes two digits as possible in DE).</summary>
    public const int MinimumDigits = 4;

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>
    /// The number in E.164 (<c>+493012345678</c>), or null when <paramref name="raw"/> is empty or no possible number. A number without an international prefix
    /// (<c>+</c> or the country's international call prefix, e.g. <c>00</c>) belongs to <paramref name="defaultCountry"/> (ISO 3166 alpha-2). An extension is dropped.
    /// </summary>
    public static string? Normalize(string? raw, string? defaultCountry)
    {
        var number = Parse(raw, defaultCountry);
        return number is null ? null : Util.Format(number, PhoneNumberFormat.E164);
    }

    /// <summary>
    /// The number written as <paramref name="format"/> says: <see cref="NumberFormat.AsStored"/> answers <paramref name="number"/> as it is;
    /// <see cref="NumberFormat.E164"/> <c>+493012345678</c>; <see cref="NumberFormat.National"/> <c>030 12345678</c> for a number of
    /// <paramref name="defaultCountry"/>, else international (<c>+44 20 7946 0958</c>); <see cref="NumberFormat.InternationalDigits"/> <c>00493012345678</c>.
    /// A number that cannot be read is answered as it is (trimmed); null or empty stays null.
    /// </summary>
    public static string? Format(string? number, NumberFormat format, string? defaultCountry)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        if (format == NumberFormat.AsStored)
        {
            return number;
        }

        var parsed = Parse(number, defaultCountry);
        if (parsed is null)
        {
            return number.Trim();
        }

        switch (format)
        {
            case NumberFormat.E164:
                return Util.Format(parsed, PhoneNumberFormat.E164);
            case NumberFormat.National:
                var home = Util.GetCountryCodeForRegion(Country(defaultCountry));
                return Util.Format(parsed, parsed.CountryCode == home ? PhoneNumberFormat.NATIONAL : PhoneNumberFormat.INTERNATIONAL);
            case NumberFormat.InternationalDigits:
                return "00" + Util.Format(parsed, PhoneNumberFormat.E164)[1..];
            default:
                return number;
        }
    }

    /// <summary>The digits of <paramref name="raw"/> only (<c>+49 (30) 123-45</c> → <c>493012345</c>); empty for null. For substring matches on numbers.</summary>
    public static string Digits(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return string.Create(raw.Count(char.IsAsciiDigit), raw, (span, source) =>
        {
            var i = 0;
            foreach (var c in source)
            {
                if (char.IsAsciiDigit(c))
                {
                    span[i++] = c;
                }
            }
        });
    }

    /// <summary>Is <paramref name="code"/> a country libphonenumber knows (<c>DE</c>, <c>AT</c>, <c>CH</c> …)?</summary>
    public static bool IsKnownCountry(string? code) => !string.IsNullOrWhiteSpace(code) && Util.GetSupportedRegions().Contains(code.Trim().ToUpperInvariant());

    /// <summary>The number as libphonenumber reads it, or null when it is no possible number.</summary>
    private static PhoneNumber? Parse(string? raw, string? defaultCountry)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var number = Util.Parse(raw.Trim(), Country(defaultCountry));
            return Util.IsPossibleNumber(number) && Util.GetNationalSignificantNumber(number).Length >= MinimumDigits ? number : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }

    private static string Country(string? code) => string.IsNullOrWhiteSpace(code) ? FallbackCountry : code.Trim().ToUpperInvariant();
}
