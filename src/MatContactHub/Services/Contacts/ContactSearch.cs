using System.Text;
using System.Text.RegularExpressions;
using MatContactHub.Data;
using Microsoft.EntityFrameworkCore;
using PhoneNumbers;

namespace MatContactHub.Services.Contacts;

/// <summary>
/// The search of the contacts (the list, the search in the header bar, the directory API). Every word of the search must be in one of the names, the company, the
/// department, an e-mail address or the customer number (<c>max acme</c> finds Max Mustermann of ACME). A search that looks like a telephone number also finds the
/// numbers, whatever way they are written: its digits are compared with the digits of every number, and its national number (with
/// <see cref="PhoneNormalizer"/> and the default country) too, so <c>0301234</c>, <c>+49 30 1234</c> and <c>0049 (30) 12-34</c> find each other.
/// </summary>
public static class ContactSearch
{
    /// <summary>The fewest digits a search needs to be compared with the numbers (shorter ones would find half the directory).</summary>
    public const int MinimumPhoneDigits = 3;

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// <summary>The contacts of <paramref name="query"/> that match <paramref name="search"/> (all of them for an empty search).</summary>
    public static IQueryable<Contact> Apply(IQueryable<Contact> query, string? search, string? defaultCountry)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var text = search.Trim();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8).Select(Like).ToList();
        var patterns = PhonePatterns(text, defaultCountry);
        if (patterns.Count == 0)
        {
            foreach (var word in words)
            {
                query = query.Where(TextMatches(word));
            }

            return query;
        }

        // a number: its digits in any of the numbers, or (a customer number, a name with digits) the words as text
        var first = patterns[0];
        var second = patterns.Count > 1 ? patterns[1] : patterns[0];
        var whole = Like(text);
        return query.Where(c =>
            (c.PhoneBusiness != null && (Regex.IsMatch(c.PhoneBusiness, first) || Regex.IsMatch(c.PhoneBusiness, second)))
            || (c.PhoneBusiness2 != null && (Regex.IsMatch(c.PhoneBusiness2, first) || Regex.IsMatch(c.PhoneBusiness2, second)))
            || (c.PhoneMobile != null && (Regex.IsMatch(c.PhoneMobile, first) || Regex.IsMatch(c.PhoneMobile, second)))
            || (c.PhoneHome != null && (Regex.IsMatch(c.PhoneHome, first) || Regex.IsMatch(c.PhoneHome, second)))
            || (c.PhoneOther != null && (Regex.IsMatch(c.PhoneOther, first) || Regex.IsMatch(c.PhoneOther, second)))
            || (c.Fax != null && (Regex.IsMatch(c.Fax, first) || Regex.IsMatch(c.Fax, second)))
            || EF.Functions.ILike(c.DisplayName, whole)
            || EF.Functions.ILike(c.CustomerNumber ?? string.Empty, whole));
    }

    /// <summary>The contacts with a number (any of the six) that matches one of <paramref name="patterns"/> (of <see cref="PhonePatterns"/>; one or two).</summary>
    public static IQueryable<Contact> WherePhone(IQueryable<Contact> query, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            return query.Where(c => false);
        }

        var first = patterns[0];
        var second = patterns.Count > 1 ? patterns[1] : patterns[0];
        return query.Where(c =>
            (c.PhoneBusiness != null && (Regex.IsMatch(c.PhoneBusiness, first) || Regex.IsMatch(c.PhoneBusiness, second)))
            || (c.PhoneBusiness2 != null && (Regex.IsMatch(c.PhoneBusiness2, first) || Regex.IsMatch(c.PhoneBusiness2, second)))
            || (c.PhoneMobile != null && (Regex.IsMatch(c.PhoneMobile, first) || Regex.IsMatch(c.PhoneMobile, second)))
            || (c.PhoneHome != null && (Regex.IsMatch(c.PhoneHome, first) || Regex.IsMatch(c.PhoneHome, second)))
            || (c.PhoneOther != null && (Regex.IsMatch(c.PhoneOther, first) || Regex.IsMatch(c.PhoneOther, second)))
            || (c.Fax != null && (Regex.IsMatch(c.Fax, first) || Regex.IsMatch(c.Fax, second))));
    }

    /// <summary>
    /// The patterns (PostgreSQL regular expressions) a number of a contact must match for <paramref name="search"/>: the digits of the search with any other characters
    /// between them, and its national significant number when libphonenumber can read the search. Empty when the search is no number.
    /// </summary>
    public static IReadOnlyList<string> PhonePatterns(string? search, string? defaultCountry)
    {
        if (string.IsNullOrWhiteSpace(search) || !search.Trim().All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '(' or ')' or '-' or '/' or '.'))
        {
            return [];
        }

        var digits = PhoneNormalizer.Digits(search);
        if (digits.Length < MinimumPhoneDigits)
        {
            return [];
        }

        var patterns = new List<string> { DigitsPattern(digits) };
        if (NationalNumber(search, defaultCountry) is { } national && national != digits)
        {
            patterns.Add(DigitsPattern(national));
        }

        return patterns;
    }

    /// <summary>The national significant number of <paramref name="raw"/> (<c>+49 30 1234</c> and <c>030 1234</c> → <c>301234</c>), or null when it is no number.</summary>
    public static string? NationalNumber(string? raw, string? defaultCountry)
    {
        var e164 = PhoneNormalizer.Normalize(raw, defaultCountry);
        if (e164 is null)
        {
            return null;
        }

        try
        {
            return Util.GetNationalSignificantNumber(Util.Parse(e164, null));
        }
        catch (NumberParseException)
        {
            return null;
        }
    }

    /// <summary>A LIKE pattern for a word anywhere (its <c>%</c>, <c>_</c> and <c>\</c> are taken literally).</summary>
    public static string Like(string word)
        => "%" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static System.Linq.Expressions.Expression<Func<Contact, bool>> TextMatches(string pattern)
        => c => EF.Functions.ILike(c.DisplayName, pattern)
                || EF.Functions.ILike(c.FirstName ?? string.Empty, pattern)
                || EF.Functions.ILike(c.LastName ?? string.Empty, pattern)
                || EF.Functions.ILike(c.MiddleName ?? string.Empty, pattern)
                || EF.Functions.ILike(c.Company ?? string.Empty, pattern)
                || EF.Functions.ILike(c.Department ?? string.Empty, pattern)
                || EF.Functions.ILike(c.JobTitle ?? string.Empty, pattern)
                || EF.Functions.ILike(c.Email ?? string.Empty, pattern)
                || EF.Functions.ILike(c.Email2 ?? string.Empty, pattern)
                || EF.Functions.ILike(c.Email3 ?? string.Empty, pattern)
                || EF.Functions.ILike(c.CustomerNumber ?? string.Empty, pattern);

    /// <summary><c>301234</c> → <c>3\D*0\D*1\D*2\D*3\D*4</c>: the digits in this order, with anything that is no digit between them.</summary>
    private static string DigitsPattern(string digits)
    {
        var sb = new StringBuilder(digits.Length * 4);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(@"\D*");
            }

            sb.Append(digits[i]);
        }

        return sb.ToString();
    }
}
