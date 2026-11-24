using System.Globalization;
using System.Text;

namespace MatContactHub.Services.Ldap;

/// <summary>What an entry of the tree is.</summary>
public enum LdapEntryKind
{
    RootDse,
    Subschema,
    Base,
    AddressBook,
    Contact,
}

/// <summary>An attribute of an entry: its canonical name, its values and, for a telephone number, the values prepared for the filters.</summary>
public sealed class LdapValues(string name, string[] values, LdapPhoneValue[]? phones = null)
{
    public string Name { get; } = name;

    public string[] Values { get; } = values;

    /// <summary>The values as numbers (only for the attributes of <see cref="LdapAttributes.Phones"/>), in the order of <see cref="Values"/>.</summary>
    public LdapPhoneValue[]? Phones { get; } = phones;
}

/// <summary>
/// An entry of the in-memory directory (immutable): the DN as it is sent, its key (<see cref="LdapDn.Normalize"/>), the attributes (user and operational, looked up
/// without case) and, for an address book and its contacts, the address book it belongs to (what an LDAP account may see).
/// </summary>
public sealed class LdapEntry
{
    private readonly Dictionary<string, LdapValues> _attributes;

    public LdapEntry(LdapEntryKind kind, string dn, long addressBookId, IEnumerable<LdapValues> attributes)
    {
        Kind = kind;
        Dn = dn;
        Key = LdapDn.Normalize(dn) ?? dn.ToLowerInvariant();
        AddressBookId = addressBookId;
        _attributes = new Dictionary<string, LdapValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attributes)
        {
            if (a.Values.Length > 0)
            {
                _attributes[a.Name] = a;
            }
        }
    }

    public LdapEntryKind Kind { get; }

    public string Dn { get; }

    public string Key { get; }

    /// <summary>The address book of an <see cref="LdapEntryKind.AddressBook"/> or a <see cref="LdapEntryKind.Contact"/>; 0 for the others.</summary>
    public long AddressBookId { get; }

    public IEnumerable<LdapValues> Attributes => _attributes.Values;

    /// <summary>The attribute with a canonical name, or null.</summary>
    public LdapValues? Get(string canonical) => _attributes.GetValueOrDefault(canonical);

    /// <summary>The first value of an attribute (for tests and the log).</summary>
    public string? First(string canonical) => Get(canonical)?.Values.FirstOrDefault();
}

/// <summary>Comparisons of the matching rules the directory uses: caseIgnoreMatch (without case, insignificant spaces) and caseIgnoreSubstringsMatch.</summary>
public static class LdapMatch
{
    /// <summary>A value compared without case, its leading and trailing spaces and its runs of spaces (RFC 4518, insignificant space handling).</summary>
    public static string Prepare(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var sb = new StringBuilder(trimmed.Length);
        var space = false;
        foreach (var c in trimmed)
        {
            if (c == ' ')
            {
                if (!space)
                {
                    sb.Append(c);
                }

                space = true;
                continue;
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    public static bool EqualsIgnoreCase(string value, string assertion) => string.Equals(Prepare(value), assertion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is <paramref name="initial"/> at the start, <paramref name="final"/> at the end and every one of <paramref name="any"/> in order between them?</summary>
    public static bool Substring(string value, string? initial, IReadOnlyList<string> any, string? final, StringComparison comparison)
    {
        var start = 0;
        var end = value.Length;
        if (initial is not null)
        {
            if (!value.StartsWith(initial, comparison))
            {
                return false;
            }

            start = initial.Length;
        }

        if (final is not null)
        {
            if (final.Length > end - start || !value.EndsWith(final, comparison))
            {
                return false;
            }

            end -= final.Length;
        }

        foreach (var part in any)
        {
            if (part.Length == 0)
            {
                continue;
            }

            var at = end - start >= part.Length ? value.IndexOf(part, start, end - start, comparison) : -1;
            if (at < 0)
            {
                return false;
            }

            start = at + part.Length;
        }

        return true;
    }

    /// <summary>The value for an approximate match: letters and digits only, without accents and case (<c>Müller-Lüdenscheidt</c> → <c>mullerludenscheidt</c>).</summary>
    public static string Approximate(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || !char.IsLetterOrDigit(c))
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
