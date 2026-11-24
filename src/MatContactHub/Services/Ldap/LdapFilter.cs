using System.Text;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// A search filter (RFC 4511, 4.5.1.7): and, or, not, equality, substrings, greater or equal, less or equal, present, approximate and extensible match. The protocol
/// carries it in BER (<see cref="LdapCodec"/>); <see cref="Parse"/> and <see cref="ToString"/> are its string form (RFC 4515), for the query log, the help and the tests.
/// </summary>
public abstract record LdapFilter
{
    /// <summary>The deepest nesting of and, or and not that is accepted (a filter of a telephone has two or three levels).</summary>
    public const int MaxDepth = 32;

    /// <summary>The most parts a filter may have.</summary>
    public const int MaxNodes = 512;

    public sealed record And(IReadOnlyList<LdapFilter> Filters) : LdapFilter;

    public sealed record Or(IReadOnlyList<LdapFilter> Filters) : LdapFilter;

    public sealed record Not(LdapFilter Filter) : LdapFilter;

    public sealed record Equality(string Attribute, string Value) : LdapFilter;

    /// <summary>A substring filter: at most one initial part, any number of parts in between, at most one final part (at least one part).</summary>
    public sealed record Substrings(string Attribute, string? Initial, IReadOnlyList<string> Any, string? Final) : LdapFilter;

    public sealed record GreaterOrEqual(string Attribute, string Value) : LdapFilter;

    public sealed record LessOrEqual(string Attribute, string Value) : LdapFilter;

    public sealed record Present(string Attribute) : LdapFilter;

    public sealed record Approx(string Attribute, string Value) : LdapFilter;

    public sealed record Extensible(string? MatchingRule, string? Attribute, string Value, bool DnAttributes) : LdapFilter;

    /// <summary>The string form of RFC 4515: <c>(&amp;(objectClass=person)(cn=Ann*))</c>.</summary>
    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb, this);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, LdapFilter filter)
    {
        sb.Append('(');
        switch (filter)
        {
            case And a:
                sb.Append('&');
                foreach (var f in a.Filters)
                {
                    Write(sb, f);
                }

                break;
            case Or o:
                sb.Append('|');
                foreach (var f in o.Filters)
                {
                    Write(sb, f);
                }

                break;
            case Not n:
                sb.Append('!');
                Write(sb, n.Filter);
                break;
            case Equality e:
                sb.Append(e.Attribute).Append('=').Append(Escape(e.Value));
                break;
            case Substrings s:
                sb.Append(s.Attribute).Append('=');
                if (s.Initial is not null)
                {
                    sb.Append(Escape(s.Initial));
                }

                sb.Append('*');
                foreach (var any in s.Any)
                {
                    sb.Append(Escape(any)).Append('*');
                }

                if (s.Final is not null)
                {
                    sb.Append(Escape(s.Final));
                }

                break;
            case GreaterOrEqual g:
                sb.Append(g.Attribute).Append(">=").Append(Escape(g.Value));
                break;
            case LessOrEqual l:
                sb.Append(l.Attribute).Append("<=").Append(Escape(l.Value));
                break;
            case Present p:
                sb.Append(p.Attribute).Append("=*");
                break;
            case Approx x:
                sb.Append(x.Attribute).Append("~=").Append(Escape(x.Value));
                break;
            case Extensible m:
                sb.Append(m.Attribute);
                if (m.DnAttributes)
                {
                    sb.Append(":dn");
                }

                if (m.MatchingRule is not null)
                {
                    sb.Append(':').Append(m.MatchingRule);
                }

                sb.Append(":=").Append(Escape(m.Value));
                break;
        }

        sb.Append(')');
    }

    /// <summary>A value written into a filter string: <c>*</c>, <c>(</c>, <c>)</c>, <c>\</c> and NUL as <c>\2a</c> …</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\\' => "\\5c",
                '\0' => "\\00",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>Reads the string form (RFC 4515); a filter without the outer parentheses (<c>cn=Ann</c>) is accepted too. Throws <see cref="FormatException"/>.</summary>
    public static LdapFilter Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.Length == 0)
        {
            throw new FormatException("The filter is empty.");
        }

        if (s[0] != '(')
        {
            s = "(" + s + ")";
        }

        var pos = 0;
        var nodes = 0;
        var filter = ParseFilter(s, ref pos, 0, ref nodes);
        if (pos != s.Length)
        {
            throw new FormatException($"Unexpected text at {pos}.");
        }

        return filter;
    }

    /// <summary>Like <see cref="Parse"/>, null instead of an exception.</summary>
    public static LdapFilter? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static LdapFilter ParseFilter(string s, ref int pos, int depth, ref int nodes)
    {
        if (depth > MaxDepth || ++nodes > MaxNodes)
        {
            throw new FormatException("The filter is nested too deeply or has too many parts.");
        }

        Expect(s, ref pos, '(');
        if (pos >= s.Length)
        {
            throw new FormatException("The filter ends early.");
        }

        LdapFilter result;
        switch (s[pos])
        {
            case '&':
            case '|':
                var and = s[pos] == '&';
                pos++;
                var list = new List<LdapFilter>();
                while (pos < s.Length && s[pos] == '(')
                {
                    list.Add(ParseFilter(s, ref pos, depth + 1, ref nodes));
                }

                result = and ? new And(list) : new Or(list);
                break;
            case '!':
                pos++;
                result = new Not(ParseFilter(s, ref pos, depth + 1, ref nodes));
                break;
            default:
                result = ParseItem(s, ref pos);
                break;
        }

        Expect(s, ref pos, ')');
        return result;
    }

    private static LdapFilter ParseItem(string s, ref int pos)
    {
        var end = s.IndexOf(')', pos);
        if (end < 0)
        {
            throw new FormatException("A ')' is missing.");
        }

        var item = s[pos..end];
        pos = end;
        var eq = item.IndexOf('=');
        if (eq <= 0)
        {
            throw new FormatException($"'{item}' is no comparison.");
        }

        var raw = item[(eq + 1)..];
        var op = item[eq - 1];
        switch (op)
        {
            case '>':
                return new GreaterOrEqual(AttributeOf(item[..(eq - 1)]), Unescape(raw));
            case '<':
                return new LessOrEqual(AttributeOf(item[..(eq - 1)]), Unescape(raw));
            case '~':
                return new Approx(AttributeOf(item[..(eq - 1)]), Unescape(raw));
            case ':':
                return ParseExtensible(item[..(eq - 1)], Unescape(raw));
        }

        var attribute = AttributeOf(item[..eq]);
        if (raw == "*")
        {
            return new Present(attribute);
        }

        if (!raw.Contains('*'))
        {
            return new Equality(attribute, Unescape(raw));
        }

        var parts = raw.Split('*');
        var initial = parts[0].Length > 0 ? Unescape(parts[0]) : null;
        var final = parts[^1].Length > 0 ? Unescape(parts[^1]) : null;
        var any = parts[1..^1].Where(p => p.Length > 0).Select(Unescape).ToList();
        return new Substrings(attribute, initial, any, final);
    }

    private static Extensible ParseExtensible(string left, string value)
    {
        // attr[:dn][:rule]:=value  or  [:dn]:rule:=value
        var parts = left.Split(':');
        var attribute = parts[0].Length > 0 ? AttributeOf(parts[0]) : null;
        var dn = false;
        string? rule = null;
        foreach (var part in parts.Skip(1))
        {
            if (part.Equals("dn", StringComparison.OrdinalIgnoreCase))
            {
                dn = true;
            }
            else if (part.Length > 0)
            {
                rule = part;
            }
        }

        if (attribute is null && rule is null)
        {
            throw new FormatException("An extensible match needs an attribute or a matching rule.");
        }

        return new Extensible(rule, attribute, value, dn);
    }

    private static string AttributeOf(string text)
    {
        var a = text.Trim();
        if (a.Length == 0 || !a.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or ';' or '_'))
        {
            throw new FormatException($"'{text}' is no attribute.");
        }

        return a;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        // escaped bytes are UTF-8 (\c3\a4 is ä): they are collected and decoded together, the other characters are taken as they are
        var sb = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2]))
                {
                    throw new FormatException("An escape needs two hex digits.");
                }

                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }

            if (bytes.Count > 0)
            {
                sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                bytes.Clear();
            }

            sb.Append(text[i]);
        }

        if (bytes.Count > 0)
        {
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
        }

        return sb.ToString();
    }

    private static void Expect(string s, ref int pos, char c)
    {
        if (pos >= s.Length || s[pos] != c)
        {
            throw new FormatException($"'{c}' expected at {pos}.");
        }

        pos++;
    }
}
