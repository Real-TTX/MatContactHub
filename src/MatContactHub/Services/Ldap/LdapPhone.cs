using MatContactHub.Services.Contacts;
using PhoneNumbers;

namespace MatContactHub.Services.Ldap;

/// <summary>A telephone number of an entry, prepared for the filters: E.164 and the digit forms a substring is looked for in.</summary>
public sealed record LdapPhoneValue(string? E164, IReadOnlyList<string> Forms);

/// <summary>A substring filter on a number, in digits: one way to read the parts (an initial part, parts in between, a final part).</summary>
public sealed record LdapPhonePattern(string? Initial, IReadOnlyList<string> Any, string? Final);

/// <summary>
/// What the readings of the substring filters on numbers of one search filter may cost together (<see cref="LdapPhone.Patterns"/>): a telephone needs a few,
/// <see cref="Default"/> keeps a crafted filter of hundreds of such parts from multiplying the work of the search. Not thread-safe (one per compiled filter).
/// </summary>
public sealed class LdapPhoneBudget(int patterns = LdapPhoneBudget.Default)
{
    public const int Default = 256;

    private int _left = patterns;

    /// <summary>Takes <paramref name="count"/> patterns; false (nothing taken) when fewer are left.</summary>
    public bool TryTake(int count)
    {
        if (count > _left)
        {
            return false;
        }

        _left -= count;
        return true;
    }
}

/// <summary>
/// The telephone numbers of the LDAP server: a telephone asks for <c>+4930123</c>, <c>004930123</c>, <c>030123</c> or <c>*30123</c> and means the same number.
/// <list type="bullet">
/// <item>Equality: the same number in E.164 (through <see cref="PhoneNormalizer"/> with the default country, so <c>030…</c>, <c>0049 30…</c> and <c>+49 30…</c>
/// are one number, while the same digits in another country, <c>+43 30…</c>, are another); a value that is no number (an extension <c>12</c>) is compared by its digits.</item>
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
        if (e164 is not null)
        {
            forms.Add(e164[1..]);
            var national = NationalDigits(e164);
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

        return new LdapPhoneValue(e164, forms.Distinct().ToList());
    }

    /// <summary>Does the stored number equal the number of the filter (prepared with <see cref="Prepare"/> too)?</summary>
    public static bool Equal(LdapPhoneValue value, LdapPhoneValue assertion)
    {
        if (assertion.E164 is not null && value.E164 is not null)
        {
            // E.164 only: two countries may have the same national digits (+49 30 1234567, +43 30 1234567)
            return assertion.E164 == value.E164;
        }

        // one of them is no number: the digits as written
        var a = assertion.Forms.LastOrDefault();
        return a is not null && value.Forms.Contains(a);
    }

    /// <summary>
    /// The ways to read a substring filter on a number; empty when a part has no digit (the filter is then compared as text). Every way is one more pass over every
    /// number, so they cost <paramref name="budget"/> (one for the whole search filter): when it cannot pay for them, the parts are read as their digits only.
    /// </summary>
    public IReadOnlyList<LdapPhonePattern> Patterns(string? initial, IReadOnlyList<string> any, string? final, LdapPhoneBudget? budget = null)
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

        // a filter of many such parts (an attack, no telephone) falls back to the digits as written
        var count = options.Aggregate(1, (n, o) => n * o.Length);
        if (count > 1 && budget is not null && !budget.TryTake(count))
        {
            options = [.. options.Select(o => new[] { o[0] })];
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

    /// <summary>The national digits of an E.164 number (<c>+493012345</c> → <c>03012345</c>), or null.</summary>
    private static string? NationalDigits(string e164)
    {
        try
        {
            var national = PhoneNormalizer.Digits(Util.Format(Util.Parse(e164, null), PhoneNumberFormat.NATIONAL));
            return national.Length == 0 ? null : national;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
}
