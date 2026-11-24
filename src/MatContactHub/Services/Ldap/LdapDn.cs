using System.Text;

namespace MatContactHub.Services.Ldap;

/// <summary>One RDN of a DN: the attribute (canonical, small letters) and the value (unescaped, as written).</summary>
public sealed record LdapRdn(string Attribute, string Value);

/// <summary>
/// Distinguished names (RFC 4514): parse, compare and write them. The directory compares DNs without case and without the spaces around the separators
/// (<c>CN=Ann, OU=Staff ,dc=Example</c> is <c>cn=ann,ou=staff,dc=example</c>): <see cref="Normalize"/> gives that key. Attribute names are written canonically
/// (<c>commonName</c> → <c>cn</c>), escapes are resolved (<c>\,</c>, <c>\2C</c>, quoted values of old clients).
/// </summary>
public static class LdapDn
{
    /// <summary>The RDNs of <paramref name="dn"/> from the left (the entry) to the right (the root); empty for the root DSE (""); null when it is no DN.</summary>
    public static IReadOnlyList<LdapRdn>? Parse(string? dn)
    {
        if (dn is null)
        {
            return null;
        }

        var text = dn.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        if (text.Length > 4096)
        {
            return null;
        }

        var result = new List<LdapRdn>();
        var i = 0;
        while (i < text.Length)
        {
            // the attribute up to '='
            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                return null;
            }

            var attribute = text[i..eq].Trim();
            if (attribute.Length == 0 || !attribute.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            {
                return null;
            }

            i = eq + 1;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            // the value up to an unescaped ',' or ';' (a '+' of a multi-valued RDN stays part of the value: this directory has none)
            var value = new StringBuilder();
            var bytes = new List<byte>();
            var trailingSpaces = 0;
            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                if (close < 0)
                {
                    return null;
                }

                value.Append(text, i + 1, close - i - 1);
                i = close + 1;
                while (i < text.Length && text[i] == ' ')
                {
                    i++;
                }
            }
            else
            {
                while (i < text.Length && text[i] is not (',' or ';'))
                {
                    var c = text[i];
                    if (c == '\\')
                    {
                        if (i + 1 >= text.Length)
                        {
                            return null;
                        }

                        if (i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
                        {
                            bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                            i += 3;
                            trailingSpaces = 0;
                            continue;
                        }

                        Flush(value, bytes);
                        value.Append(text[i + 1]);
                        i += 2;
                        trailingSpaces = 0;
                        continue;
                    }

                    Flush(value, bytes);
                    value.Append(c);
                    trailingSpaces = c == ' ' ? trailingSpaces + 1 : 0;
                    i++;
                }

                Flush(value, bytes);
                if (trailingSpaces > 0)
                {
                    value.Length -= trailingSpaces;
                }
            }

            result.Add(new LdapRdn(LdapAttributes.Canonical(attribute).ToLowerInvariant(), value.ToString()));
            if (i < text.Length)
            {
                if (text[i] is not (',' or ';'))
                {
                    return null;
                }

                i++;
                if (i >= text.Length)
                {
                    return null;
                }
            }
        }

        return result;
    }

    /// <summary>The key of a DN for comparing (small letters, canonical attribute names, single spaces, minimal escapes); null when it is no DN.</summary>
    public static string? Normalize(string? dn)
    {
        var rdns = Parse(dn);
        return rdns is null ? null : Join(rdns);
    }

    /// <summary>The key of a list of RDNs (see <see cref="Normalize"/>).</summary>
    public static string Join(IEnumerable<LdapRdn> rdns)
        => string.Join(',', rdns.Select(r => r.Attribute + "=" + Escape(Fold(r.Value))));

    /// <summary>The value of an RDN for comparing: trimmed, inner runs of spaces as one, small letters.</summary>
    public static string Fold(string value)
    {
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>A value written into a DN (RFC 4514, 2.4): <c>, + " \ &lt; &gt; ; =</c> escaped, a leading <c>#</c> or space and a trailing space too.</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' || (i == 0 && c is '#' or ' ') || (i == value.Length - 1 && c == ' '))
            {
                sb.Append('\\');
            }

            if (c == '\0')
            {
                sb.Append("\\00");
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>The parent of a normalized DN (<c>ou=a,dc=b</c> → <c>dc=b</c>; the root's parent is null).</summary>
    public static string? ParentOf(IReadOnlyList<LdapRdn> rdns) => rdns.Count == 0 ? null : Join(rdns.Skip(1));

    private static void Flush(StringBuilder value, List<byte> bytes)
    {
        if (bytes.Count > 0)
        {
            value.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
    }
}
