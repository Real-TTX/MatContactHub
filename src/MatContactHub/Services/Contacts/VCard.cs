using System.Globalization;
using System.Text;
using MatContactHub.Data;

namespace MatContactHub.Services.Contacts;

public enum VCardVersion
{
    /// <summary>vCard 3.0 (RFC 2426): what Outlook, most phones and older clients read.</summary>
    V3 = 3,

    /// <summary>vCard 4.0 (RFC 6350).</summary>
    V4 = 4,
}

/// <summary>
/// vCards of contacts: written as 3.0 or 4.0 (UTF-8, CRLF, lines folded at 75 octets, text escaped: <c>\\</c>, <c>\,</c>, <c>\;</c>, <c>\n</c>; no photo), and read back
/// from 2.1, 3.0 and 4.0 (folded lines, groups such as <c>item1.TEL</c>, <c>TYPE</c> in every spelling, quoted-printable of 2.1). The <see cref="Contact.Token"/> is the
/// <c>UID</c>. Fields without a standard property travel as <c>X-MCH-*</c> (customer number, Custom1..5), so a contact comes back as it went out.
/// </summary>
public static class VCard
{
    /// <summary>The longest line in octets, without the CRLF (RFC 6350 3.2).</summary>
    public const int LineOctets = 75;

    /// <summary>The contacts as one vCard file.</summary>
    public static string Write(IEnumerable<Contact> contacts, VCardVersion version)
    {
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            WriteOne(sb, c, version);
        }

        return sb.ToString();
    }

    /// <summary>One contact as a vCard (appended to <paramref name="sb"/>).</summary>
    public static void WriteOne(StringBuilder sb, Contact c, VCardVersion version)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(c);
        var v4 = version == VCardVersion.V4;
        Line(sb, "BEGIN:VCARD");
        Line(sb, v4 ? "VERSION:4.0" : "VERSION:3.0");
        Line(sb, "PRODID:-//MatContactHub//MatContactHub//EN");
        Line(sb, "UID:" + (v4 ? "urn:uuid:" : string.Empty) + c.Token.ToString("D"));
        var name = string.IsNullOrWhiteSpace(c.DisplayName) ? ContactService.DisplayNameOf(null, c.FirstName, c.LastName, c.Company) : c.DisplayName;
        Line(sb, "FN:" + Escape(name));
        var prefix = string.Join(',', new[] { c.Salutation, c.Title }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Escape(p!)));
        Line(sb, "N:" + string.Join(';', Escape(c.LastName), Escape(c.FirstName), Escape(c.MiddleName), prefix, string.Empty));
        if (!v4 && string.IsNullOrWhiteSpace(c.FirstName) && string.IsNullOrWhiteSpace(c.LastName) && !string.IsNullOrWhiteSpace(c.Company))
        {
            Line(sb, "X-ABShowAs:COMPANY");
        }

        if (c.Company is not null || c.Department is not null)
        {
            Line(sb, "ORG:" + Escape(c.Company) + (c.Department is null ? string.Empty : ";" + Escape(c.Department)));
        }

        Optional(sb, "TITLE", c.JobTitle);
        var first = true;
        foreach (var mail in new[] { c.Email, c.Email2, c.Email3 }.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            Line(sb, (v4 ? (first ? "EMAIL;TYPE=work;PREF=1:" : "EMAIL;TYPE=work:") : (first ? "EMAIL;TYPE=INTERNET,WORK,PREF:" : "EMAIL;TYPE=INTERNET,WORK:")) + Escape(mail));
            first = false;
        }

        Phone(sb, v4, c.PhoneBusiness, "work,voice", "WORK,VOICE");
        Phone(sb, v4, c.PhoneBusiness2, "work,voice", "WORK,VOICE");
        Phone(sb, v4, c.PhoneMobile, "cell,voice", "CELL,VOICE");
        Phone(sb, v4, c.PhoneHome, "home,voice", "HOME,VOICE");
        Phone(sb, v4, c.PhoneOther, "voice", "VOICE");
        Phone(sb, v4, c.Fax, "work,fax", "WORK,FAX");
        if (new[] { c.Street, c.PostalCode, c.City, c.State, c.Country }.Any(p => !string.IsNullOrWhiteSpace(p)))
        {
            Line(sb, (v4 ? "ADR;TYPE=work:" : "ADR;TYPE=WORK:") + string.Join(';', string.Empty, string.Empty, Escape(c.Street), Escape(c.City), Escape(c.State), Escape(c.PostalCode), Escape(c.Country)));
        }

        Optional(sb, "URL", c.Website);
        if (c.Birthday is { } birthday)
        {
            Line(sb, "BDAY:" + birthday.ToString(v4 ? "yyyyMMdd" : "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        Optional(sb, "NOTE", c.Notes);
        if (c.AddressBook?.Name is { Length: > 0 } book)
        {
            Line(sb, "CATEGORIES:" + Escape(book));
        }

        Optional(sb, "X-MCH-CUSTOMERNUMBER", c.CustomerNumber);
        Optional(sb, "X-MCH-CUSTOM1", c.Custom1);
        Optional(sb, "X-MCH-CUSTOM2", c.Custom2);
        Optional(sb, "X-MCH-CUSTOM3", c.Custom3);
        Optional(sb, "X-MCH-CUSTOM4", c.Custom4);
        Optional(sb, "X-MCH-CUSTOM5", c.Custom5);
        var changed = DateTime.SpecifyKind(c.UpdateDate == default ? DateTime.UtcNow : c.UpdateDate, DateTimeKind.Utc);
        Line(sb, "REV:" + changed.ToString(v4 ? "yyyyMMdd'T'HHmmss'Z'" : "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Line(sb, "END:VCARD");
    }

    /// <summary>A text value escaped for a vCard: backslash, comma, semicolon and line breaks (null is empty).</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case ',': sb.Append("\\,"); break;
                case ';': sb.Append("\\;"); break;
                case '\r':
                    sb.Append("\\n");
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads every vCard of <paramref name="text"/> into the fields of a form (<see cref="ContactForm.AddressBookId"/> stays 0). A card without a name, a company, an
    /// e-mail address and a number is left out.
    /// </summary>
    public static IReadOnlyList<ContactForm> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<ContactForm>();
        List<Property>? card = null;
        foreach (var property in Properties(text))
        {
            if (property.Name == "BEGIN" && property.Value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                card = [];
            }
            else if (property.Name == "END" && property.Value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (card is not null && ToForm(card) is { } form)
                {
                    result.Add(form);
                }

                card = null;
            }
            else
            {
                card?.Add(property);
            }
        }

        return result;
    }

    private static void Optional(StringBuilder sb, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Line(sb, name + ":" + Escape(value));
        }
    }

    private static void Phone(StringBuilder sb, bool v4, string? number, string types4, string types3)
    {
        if (!string.IsNullOrWhiteSpace(number))
        {
            Line(sb, (v4 ? $"TEL;VALUE=text;TYPE={types4}:" : $"TEL;TYPE={types3}:") + Escape(number));
        }
    }

    /// <summary>Appends a content line folded at <see cref="LineOctets"/> octets of UTF-8 (a character is never split), each part ended by CRLF.</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var octets = 0;
        var limit = LineOctets;
        for (var i = 0; i < line.Length; i++)
        {
            var length = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(line.AsSpan(i, length));
            if (octets + size > limit)
            {
                sb.Append("\r\n ");
                octets = 0;
                limit = LineOctets - 1; // the space that starts a continued line counts
            }

            sb.Append(line, i, length);
            octets += size;
            i += length - 1;
        }

        sb.Append("\r\n");
    }

    private sealed record Property(string Name, IReadOnlyList<string> Types, string Value, Dictionary<string, string> Parameters);

    /// <summary>The content lines of a vCard text, unfolded, with the group taken off the name and the parameters read.</summary>
    private static IEnumerable<Property> Properties(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var logical = new List<string>();

        // the line being unfolded: built in one StringBuilder (a PHOTO of iOS or Outlook is folded into thousands of lines; += would copy it every time),
        // its head (name and parameters, up to the colon) read once from the parts as they come
        var current = new StringBuilder();
        var started = false;
        var colon = -1;
        var quoted = false;
        var quotedPrintable = false;
        void Append(string part, int from)
        {
            var offset = current.Length - from;
            current.Append(part, from, part.Length - from);
            for (var i = from; colon < 0 && i < part.Length; i++)
            {
                if (part[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (part[i] == ':' && !quoted)
                {
                    colon = offset + i;
                    quotedPrintable = colon > 0 && current.ToString(0, colon).Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        foreach (var line in lines)
        {
            if (started && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                Append(line, 1);
            }
            else if (started && quotedPrintable && current.Length > 0 && current[^1] == '=')
            {
                // a soft line break of quoted-printable (vCard 2.1): the value goes on in this line
                current.Length--;
                Append(line, 0);
            }
            else
            {
                if (started)
                {
                    logical.Add(current.ToString());
                }

                current.Clear();
                (started, colon, quoted, quotedPrintable) = (true, -1, false, false);
                Append(line, 0);
            }
        }

        if (started)
        {
            logical.Add(current.ToString());
        }

        foreach (var line in logical)
        {
            if (Parse(line) is { } property)
            {
                yield return property;
            }
        }
    }

    /// <summary>The position of the colon that ends the name and the parameters (a colon inside a quoted parameter value does not count).</summary>
    private static int ValueStart(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static Property? Parse(string line)
    {
        var colon = ValueStart(line);
        if (colon <= 0)
        {
            return null;
        }

        var head = line[..colon];
        var value = line[(colon + 1)..];
        var parts = SplitParameters(head);
        var name = parts[0];
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        var types = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                // vCard 2.1: TEL;WORK;VOICE:... and ENCODING without a name
                if (p.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
                {
                    parameters["ENCODING"] = p;
                }
                else
                {
                    types.Add(p.ToUpperInvariant());
                }

                continue;
            }

            var key = p[..eq].Trim();
            var val = p[(eq + 1)..].Trim().Trim('"');
            if (key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                types.AddRange(val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()));
            }
            else
            {
                parameters[key] = val;
            }
        }

        if (parameters.TryGetValue("PREF", out _))
        {
            types.Add("PREF");
        }

        if (parameters.TryGetValue("ENCODING", out var encoding) && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
        {
            value = DecodeQuotedPrintable(value, parameters.TryGetValue("CHARSET", out var charset) ? charset : "UTF-8");
        }

        return new Property(name.Trim().ToUpperInvariant(), types, value, parameters);
    }

    private static List<string> SplitParameters(string head)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var ch in head)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                sb.Append(ch);
            }
            else if (ch == ';' && !quoted)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        parts.Add(sb.ToString());
        return parts;
    }

    private static string DecodeQuotedPrintable(string value, string charset)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        Encoding encoding;
        try
        {
            encoding = charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString([.. bytes]);
    }

    /// <summary>The parts of a structured value (split at unescaped <paramref name="separator"/>), each unescaped (with <paramref name="unescape"/> false still escaped,
    /// for a part that is split again: the prefixes of N at their own commas).</summary>
    private static List<string> Split(string value, char separator, bool unescape = true)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\\' && i + 1 < value.Length)
            {
                sb.Append(ch).Append(value[i + 1]);
                i++;
            }
            else if (ch == separator)
            {
                parts.Add(unescape ? Unescape(sb.ToString()) : sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        parts.Add(unescape ? Unescape(sb.ToString()) : sb.ToString());
        return parts;
    }

    /// <summary>A text value as it was before <see cref="Escape"/>.</summary>
    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch
                {
                    'n' or 'N' => '\n',
                    _ => next,
                });
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    private static ContactForm? ToForm(List<Property> card)
    {
        var form = new ContactForm();
        var phones = new List<(string Number, IReadOnlyList<string> Types)>();
        var mails = new List<(string Mail, bool Preferred)>();
        foreach (var p in card)
        {
            switch (p.Name)
            {
                case "FN":
                    form.DisplayName = Clean(Unescape(p.Value));
                    break;
                case "N":
                    // split without unescaping: an escaped comma inside a prefix (Title "Dr., MBA") is no separator of the prefixes
                    var n = Split(p.Value, ';', unescape: false);
                    form.LastName = Clean(Unescape(At(n, 0) ?? string.Empty));
                    form.FirstName = Clean(Unescape(At(n, 1) ?? string.Empty));
                    form.MiddleName = Clean(Unescape(At(n, 2) ?? string.Empty));
                    var prefixes = Split(At(n, 3) ?? string.Empty, ',').Select(Clean).OfType<string>().ToList();
                    if (prefixes.Count >= 2)
                    {
                        form.Salutation = prefixes[0];
                        form.Title = string.Join(' ', prefixes.Skip(1));
                    }
                    else if (prefixes.Count == 1)
                    {
                        if (IsAcademicTitle(prefixes[0]))
                        {
                            form.Title = prefixes[0];
                        }
                        else
                        {
                            form.Salutation = prefixes[0];
                        }
                    }

                    break;
                case "ORG":
                    var org = Split(p.Value, ';');
                    form.Company = Clean(At(org, 0));
                    form.Department = Clean(string.Join(", ", org.Skip(1).Select(Clean).OfType<string>()));
                    break;
                case "TITLE":
                    form.JobTitle = Clean(Unescape(p.Value));
                    break;
                case "EMAIL":
                    if (Clean(Unescape(p.Value)) is { } mail)
                    {
                        mails.Add((mail.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? mail[7..] : mail, p.Types.Contains("PREF")));
                    }

                    break;
                case "TEL":
                    if (Clean(Unescape(p.Value)) is { } number)
                    {
                        phones.Add((number.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? number[4..] : number, p.Types));
                    }

                    break;
                case "ADR":
                    // the first address wins (a work address before others)
                    if (form.Street is null && form.City is null && form.PostalCode is null)
                    {
                        var adr = Split(p.Value, ';');
                        var street = string.Join(", ", new[] { At(adr, 1), At(adr, 2) }.Select(Clean).OfType<string>());
                        form.Street = Clean(street);
                        form.City = Clean(At(adr, 3));
                        form.State = Clean(At(adr, 4));
                        form.PostalCode = Clean(At(adr, 5));
                        form.Country = Clean(At(adr, 6));
                    }

                    break;
                case "URL":
                    form.Website ??= Clean(Unescape(p.Value));
                    break;
                case "BDAY":
                    form.Birthday = ParseDate(Unescape(p.Value).Trim());
                    break;
                case "NOTE":
                    form.Notes = Clean(Unescape(p.Value));
                    break;
                case "X-MCH-CUSTOMERNUMBER":
                    form.CustomerNumber = Clean(Unescape(p.Value));
                    break;
                case "X-MCH-CUSTOM1":
                    form.Custom1 = Clean(Unescape(p.Value));
                    break;
                case "X-MCH-CUSTOM2":
                    form.Custom2 = Clean(Unescape(p.Value));
                    break;
                case "X-MCH-CUSTOM3":
                    form.Custom3 = Clean(Unescape(p.Value));
                    break;
                case "X-MCH-CUSTOM4":
                    form.Custom4 = Clean(Unescape(p.Value));
                    break;
                case "X-MCH-CUSTOM5":
                    form.Custom5 = Clean(Unescape(p.Value));
                    break;
            }
        }

        // the preferred address first, then in their order
        var ordered = mails.Where(m => m.Preferred).Concat(mails.Where(m => !m.Preferred)).Select(m => m.Mail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        form.Email = At(ordered, 0);
        form.Email2 = At(ordered, 1);
        form.Email3 = At(ordered, 2);
        AssignPhones(form, phones);

        if (form.DisplayName is null && form.FirstName is null && form.LastName is null && form.Company is null && form.Email is null && form.PhoneBusiness is null
            && form.PhoneMobile is null && form.PhoneOther is null && form.PhoneHome is null && form.PhoneBusiness2 is null)
        {
            return null;
        }

        return form;
    }

    /// <summary>The numbers into the fields by their types: a fax, a mobile, home, the first two of work, the rest into the free fields.</summary>
    private static void AssignPhones(ContactForm form, List<(string Number, IReadOnlyList<string> Types)> phones)
    {
        var rest = new List<string>();
        foreach (var (number, types) in phones)
        {
            bool Has(string t) => types.Contains(t);
            if (Has("FAX"))
            {
                if (form.Fax is null)
                {
                    form.Fax = number;
                    continue;
                }
            }
            else if (Has("CELL") || Has("MOBILE"))
            {
                if (form.PhoneMobile is null)
                {
                    form.PhoneMobile = number;
                    continue;
                }
            }
            else if (Has("HOME"))
            {
                if (form.PhoneHome is null)
                {
                    form.PhoneHome = number;
                    continue;
                }
            }
            else if (Has("WORK"))
            {
                if (form.PhoneBusiness is null)
                {
                    form.PhoneBusiness = number;
                    continue;
                }

                if (form.PhoneBusiness2 is null)
                {
                    form.PhoneBusiness2 = number;
                    continue;
                }
            }
            else if (form.PhoneOther is null)
            {
                form.PhoneOther = number;
                continue;
            }

            rest.Add(number);
        }

        foreach (var number in rest)
        {
            if (form.PhoneBusiness is null)
            {
                form.PhoneBusiness = number;
            }
            else if (form.PhoneOther is null)
            {
                form.PhoneOther = number;
            }
            else if (form.PhoneBusiness2 is null)
            {
                form.PhoneBusiness2 = number;
            }
        }
    }

    private static DateOnly? ParseDate(string value)
    {
        var date = value.Length >= 10 && value[4] == '-' ? value[..10] : value.Length >= 8 ? value[..8] : value;
        return DateOnly.TryParseExact(date, ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static bool IsAcademicTitle(string value)
        => new[] { "dr", "prof", "dipl", "mag", "ing", "phd", "mba", "msc", "bsc" }.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string? At(IReadOnlyList<string> list, int index) => index < list.Count ? list[index] : null;

    private static string? Clean(string? value) => ContactFields.Clean(value);
}
