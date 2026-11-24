using MatContactHub.Services.Contacts;
using PhoneNumbers;

namespace MatContactHub.Services.Ldap;

/// <summary>A telephone number of an entry, prepared for the filters: E.164, the national significant number and the digit forms a substring is looked for in.</summary>
public sealed record LdapPhoneValue(string? E164, string? Nsn, IReadOnlyList<string> Forms);

/// <summary>A substring filter on a number, in digits: one way to read the parts (an initial part, parts in between, a final part).</summary>
public sealed record LdapPhonePattern(string? Initial, IReadOnlyList<string> Any, string? Final);

/// <summary>
/// The telephone numbers of the LDAP server: a telephone asks for <c>+4930123</c>, <c>004930123</c>, <c>030123</c> or <c>*30123</c> and means the same number.
/// <list type="bullet">
/// <item>Equality: the same number in E.164 (through <see cref="PhoneNormalizer"/> with the default country) or the same national significant number;
/// a value that is no number (an extension <c>12</c>) is compared by its digits.</item>
/// <item>Substring: on digits. A stored number is looked at as international digits (<c>4930123</c>), as stored (<c>030123</c>) and nationally (<c>030123</c>);
/// a part of the filter that starts with <c>+</c>, <c>00</c> or the national prefix (<c>0</c>) is also read without it, internationally (<c>030</c> → <c>4930</c>).</item>
/// </list>
/// One instance per default country (thread-safe, without state).
/// </summary>
public sealed class LdapPhone
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>The most parts of a substring filter on a number that are read in both ways (2^n readings).</summary>
    private const int MaxAlternativeParts = 6;

    public LdapPhone(string? defaultCountry)
    {
        Country = PhoneNormalizer.IsKnownCountry(defaultCountry) ? defaultCountry!.Trim().ToUpperInvariant() : PhoneNormalizer.FallbackCountry;
        CountryCode = Util.GetCountryCodeForRegion(Country).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = Util.GetMetadataForRegion(Country)?.NationalPrefix;
        NationalPrefix = string.IsNullOrEmpty(prefix) ? "0" : prefix;
    }

    public string Country { get; }

    /// <summary>The calling code of <see cref="Country"/> (<c>49</c>).</summary>
    public string CountryCode { get; }

    /// <summary>The prefix of national numbers in <see cref="Country"/> (<c>0</c>).</summary>
    public string NationalPrefix { get; }

    /// <summary>A stored number, prepared.</summary>
    public LdapPhoneValue Prepare(string raw)
    {
        var e164 = PhoneNormalizer.Normalize(raw, Country);
        var forms = new List<string>(3);
        string? nsn = null;
        if (e164 is not null)
        {
            forms.Add(e164[1..]);
            nsn = NsnOf(e164, out var national);
            if (national is not null)
            {
                forms.Add(national);
            }
        }

        var digits = PhoneNormalizer.Digits(raw);
        if (digits.Length > 0)
        {
            forms.Add(digits);
        }

        return new LdapPhoneValue(e164, nsn, forms.Distinct().ToList());
    }

    /// <summary>Does the stored number equal the number of the filter (prepared with <see cref="Prepare"/> too)?</summary>
    public static bool Equal(LdapPhoneValue value, LdapPhoneValue assertion)
    {
        if (assertion.E164 is not null && value.E164 is not null)
        {
            return assertion.E164 == value.E164 || (assertion.Nsn is not null && assertion.Nsn == value.Nsn);
        }

        // one of them is no number: the digits as written
        var a = assertion.Forms.LastOrDefault();
        return a is not null && value.Forms.Contains(a);
    }

    /// <summary>The ways to read a substring filter on a number; empty when a part has no digit (the filter is then compared as text).</summary>
    public IReadOnlyList<LdapPhonePattern> Patterns(string? initial, IReadOnlyList<string> any, string? final)
    {
        var parts = new List<string>();
        if (initial is not null)
        {
            parts.Add(initial);
        }

        parts.AddRange(any);
        if (final is not null)
        {
            parts.Add(final);
        }

        var options = new List<string[]>();
        foreach (var part in parts)
        {
            var digits = PhoneNormalizer.Digits(part);
            if (digits.Length == 0)
            {
                return [];
            }

            options.Add(parts.Count <= MaxAlternativeParts ? Readings(part.Trim(), digits) : [digits]);
        }

        var result = new List<LdapPhonePattern>();
        foreach (var combination in Combinations(options))
        {
            var i = 0;
            var first = initial is not null ? combination[i++] : null;
            var middle = new List<string>();
            for (var k = 0; k < any.Count; k++)
            {
                middle.Add(combination[i++]);
            }

            var last = final is not null ? combination[i] : null;
            result.Add(new LdapPhonePattern(first, middle, last));
        }

        return result;
    }

    /// <summary>Does one of the forms of the stored number contain the pattern (initial at the start, final at the end, the others in order between)?</summary>
    public static bool Matches(LdapPhoneValue value, IReadOnlyList<LdapPhonePattern> patterns)
    {
        foreach (var form in value.Forms)
        {
            foreach (var p in patterns)
            {
                if (LdapMatch.Substring(form, p.Initial, p.Any, p.Final, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The readings of one part: its digits, and without an international or national prefix, as international digits.</summary>
    private string[] Readings(string part, string digits)
    {
        if (part.StartsWith('+'))
        {
            return [digits];
        }

        if (digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 2)
        {
            return [digits, digits[2..]];
        }

        if (digits.StartsWith(NationalPrefix, StringComparison.Ordinal) && digits.Length > NationalPrefix.Length)
        {
            return [digits, CountryCode + digits[NationalPrefix.Length..]];
        }

        return [digits];
    }

    private static IEnumerable<string[]> Combinations(List<string[]> options)
    {
        var index = new int[options.Count];
        while (true)
        {
            yield return [.. options.Select((o, i) => o[index[i]])];
            var k = options.Count - 1;
            while (k >= 0 && ++index[k] == options[k].Length)
            {
                index[k] = 0;
                k--;
            }

            if (k < 0)
            {
                yield break;
            }
        }
    }

    /// <summary>The national significant number of an E.164 number (<c>+493012345</c> → <c>3012345</c>) and its national digits (<c>03012345</c>).</summary>
    private static string? NsnOf(string e164, out string? national)
    {
        national = null;
        try
        {
            var number = Util.Parse(e164, null);
            national = PhoneNormalizer.Digits(Util.Format(number, PhoneNumberFormat.NATIONAL));
            if (national.Length == 0)
            {
                national = null;
            }

            return Util.GetNationalSignificantNumber(number);
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
}
