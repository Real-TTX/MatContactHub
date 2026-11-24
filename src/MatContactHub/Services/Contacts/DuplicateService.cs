using System.Globalization;
using System.Text;
using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Contacts;

/// <summary>Why contacts are taken for the same person.</summary>
public enum DuplicateReason
{
    /// <summary>The same telephone number (compared in E.164 with <see cref="PhoneNormalizer"/>).</summary>
    Phone = 1,

    /// <summary>The same e-mail address (without case).</summary>
    Email = 2,

    /// <summary>The same name and company.</summary>
    NameAndCompany = 3,
}

/// <summary>A contact of a group of duplicates: what tells it apart, its address book and the role of the actor there, how many fields it has filled.</summary>
public sealed record DuplicateMember(
    long Id,
    string DisplayName,
    string? Company,
    string? Email,
    string? Phone,
    string? City,
    long AddressBookId,
    string AddressBook,
    string? AddressBookColor,
    bool Synced,
    string? SourceName,
    bool Hidden,
    AddressBookRole Role,
    int Filled);

/// <summary>Why the contacts of a group look alike: the reason and the value they share (the number, the address, the name).</summary>
public sealed record DuplicateMatch(DuplicateReason Reason, string Value);

/// <summary>
/// A group of contacts that look like the same person: its key (stable while the group stays the same), why, its contacts (the suggested one to keep first) and whether
/// the actor may merge it (an editor of every address book in it).
/// </summary>
public sealed record DuplicateGroup(string Key, IReadOnlyList<DuplicateMatch> Matches, IReadOnlyList<DuplicateMember> Contacts, bool CanChange)
{
    public IReadOnlyList<DuplicateReason> Reasons => [.. Matches.Select(m => m.Reason).Distinct().Order()];

    /// <summary>The value that names the group: the first thing its contacts share.</summary>
    public string Label => Matches.Count > 0 ? Matches[0].Value : string.Empty;
}

/// <summary>
/// The duplicates (module C) among the contacts the actor may see. Two contacts are linked when they share a telephone number (E.164, the fax left out), an e-mail
/// address (without case) or the name and the company (small letters, without accents and punctuation); a pair marked "not a duplicate"
/// (<see cref="DuplicateIgnore"/>) is never linked. The links are joined into groups (union-find: A~B and B~C is one group). A value that more than
/// <see cref="MaxShared"/> contacts share (the switchboard of a company, an info@ address) links nobody. A merge keeps one contact: a manual one gets the empty
/// fields filled from the others; the merged manual contacts are deleted (softly), the merged synced ones hidden (the source would bring them back) and marked as
/// "not a duplicate" of the kept one.
/// </summary>
public static class DuplicateService
{
    /// <summary>A value shared by more contacts than this is no sign of a duplicate (a central number, a shared mailbox).</summary>
    public const int MaxShared = 25;

    /// <summary>The groups of duplicates among the contacts the actor may see, the largest first.</summary>
    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        var roles = await AddressBookService.RolesAsync(db, actor, ct);
        var books = roles.Keys.ToList();
        var country = await ContactService.DefaultCountryAsync(db, ct);
        var contacts = await db.Contacts.AsNoTracking().Include(c => c.AddressBook).Include(c => c.DataSource)
            .Where(c => books.Contains(c.AddressBookId)).ToListAsync(ct);
        var byId = contacts.ToDictionary(c => c.Id);

        // the values that link contacts
        var keys = new Dictionary<string, (DuplicateReason Reason, string Value, List<long> Ids)>(StringComparer.Ordinal);
        void Add(string key, DuplicateReason reason, string value, long id)
        {
            if (!keys.TryGetValue(key, out var entry))
            {
                entry = (reason, value, []);
                keys[key] = entry;
            }

            if (!entry.Ids.Contains(id))
            {
                entry.Ids.Add(id);
            }
        }

        foreach (var c in contacts)
        {
            foreach (var field in ContactFields.Phones.Where(f => f.Name != "Fax"))
            {
                if (PhoneNormalizer.Normalize(field.Get(c), country) is { } number)
                {
                    Add("p:" + number, DuplicateReason.Phone, number, c.Id);
                }
            }

            foreach (var field in ContactFields.Emails)
            {
                if (ContactFields.Clean(field.Get(c)) is { } mail)
                {
                    Add("e:" + mail.ToLowerInvariant(), DuplicateReason.Email, mail.ToLowerInvariant(), c.Id);
                }
            }

            var name = NormalizeName(string.IsNullOrWhiteSpace(c.FirstName) && string.IsNullOrWhiteSpace(c.LastName) ? c.DisplayName : $"{c.FirstName} {c.LastName}");
            if (name.Length > 0)
            {
                var company = NormalizeName(c.Company);
                var label = string.IsNullOrWhiteSpace(c.Company) || string.Equals(name, company, StringComparison.Ordinal) ? c.DisplayName : $"{c.DisplayName}, {c.Company}";
                Add("n:" + name + "|" + company, DuplicateReason.NameAndCompany, label, c.Id);
            }
        }

        var ignored = await IgnoredPairsAsync(db, ct);
        var parent = new Dictionary<long, long>();
        long Find(long x)
        {
            while (parent.TryGetValue(x, out var p) && p != x)
            {
                parent[x] = parent.TryGetValue(p, out var gp) ? gp : p;
                x = p;
            }

            return x;
        }

        void Union(long a, long b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        var matches = new List<(long Member, DuplicateMatch Match)>();
        foreach (var (_, (reason, value, ids)) in keys)
        {
            if (ids.Count < 2 || ids.Count > MaxShared)
            {
                continue;
            }

            long? linked = null;
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                {
                    if (!ignored.Contains(Pair(ids[i], ids[j])))
                    {
                        Union(ids[i], ids[j]);
                        linked = ids[i];
                    }
                }
            }

            if (linked is { } member)
            {
                matches.Add((member, new DuplicateMatch(reason, value)));
            }
        }

        var groups = new List<DuplicateGroup>();
        var matchesByRoot = matches.GroupBy(m => Find(m.Member)).ToDictionary(g => g.Key, g => g.Select(m => m.Match).OrderBy(m => m.Reason).ThenBy(m => m.Value, StringComparer.Ordinal).ToList());
        foreach (var group in parent.Keys.GroupBy(Find).Where(g => g.Count() > 1))
        {
            var members = group.Select(id => byId[id]).Select(c => Member(c, roles[c.AddressBookId])).OrderByDescending(m => !m.Synced).ThenByDescending(m => m.Filled)
                .ThenBy(m => m.Id).ToList();
            var canChange = members.All(m => m.Role >= AddressBookRole.Editor);
            groups.Add(new DuplicateGroup("g" + group.Key.ToString(CultureInfo.InvariantCulture), matchesByRoot.GetValueOrDefault(group.Key) ?? [], members, canChange));
        }

        return [.. groups.OrderByDescending(g => g.Contacts.Count).ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Marks the contacts as "not a duplicate" of each other (every pair of them; the actor must be an editor of each of their address books).</summary>
    public static async Task<Outcome<int>> IgnoreAsync(AppDbContext db, Actor actor, IReadOnlyCollection<long> contactIds, CancellationToken ct = default)
    {
        var ids = contactIds.Distinct().Take(DuplicateLimit).ToList();
        if (ids.Count < 2)
        {
            return Outcome<int>.Fail(Text.T("Duplicates.TooFew"));
        }

        var editable = await ContactService.EditableBooksAsync(db, actor, ct);
        var found = await db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.AddressBookId, c.DisplayName }).ToListAsync(ct);
        if (found.Count != ids.Count || found.Any(c => !editable.Contains(c.AddressBookId)))
        {
            return Outcome<int>.Denied();
        }

        var added = await AddIgnoresAsync(db, ids, ct);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", string.Join(", ", found.Select(c => c.DisplayName).Take(5)), "not a duplicate", null, ct);
        return Outcome<int>.Success(added);
    }

    /// <summary>Marks two contacts as "not a duplicate".</summary>
    public static Task<Outcome<int>> IgnoreAsync(AppDbContext db, Actor actor, long contactId, long otherContactId, CancellationToken ct = default)
        => IgnoreAsync(db, actor, [contactId, otherContactId], ct);

    /// <summary>
    /// Merges <paramref name="mergedIds"/> into <paramref name="keepId"/>; the actor must be an editor of every address book involved. A manual kept contact gets its
    /// empty fields from the others (in their order; the e-mail addresses are gathered into its free ones); merged manual contacts are deleted (softly), merged
    /// synced ones hidden and marked "not a duplicate" of the kept one. Answers the kept id.
    /// </summary>
    public static async Task<Outcome<long>> MergeAsync(AppDbContext db, Actor actor, long keepId, IReadOnlyList<long> mergedIds, CancellationToken ct = default)
    {
        var others = mergedIds.Where(id => id != keepId).Distinct().Take(DuplicateLimit).ToList();
        if (others.Count == 0)
        {
            return Outcome<long>.Fail(Text.T("Duplicates.TooFew"));
        }

        var editable = await ContactService.EditableBooksAsync(db, actor, ct);
        var all = await db.Contacts.Where(c => c.Id == keepId || others.Contains(c.Id)).ToListAsync(ct);
        if (all.Count != others.Count + 1 || all.Any(c => !editable.Contains(c.AddressBookId)))
        {
            return Outcome<long>.Denied();
        }

        var keep = all.Single(c => c.Id == keepId);
        var merged = others.Select(id => all.Single(c => c.Id == id)).ToList();
        if (keep.DataSourceId is null)
        {
            Fill(keep, merged);
        }

        foreach (var c in merged)
        {
            if (c.DataSourceId is null)
            {
                db.Contacts.Remove(c);
            }
            else
            {
                c.Hidden = true;
            }
        }

        await AddIgnoresAsync(db, [keep.Id, .. merged.Where(c => c.DataSourceId is not null).Select(c => c.Id)], ct, onlyWith: keep.Id);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "contact", keep.DisplayName, Text.T("Duplicates.Merged.Log", merged.Count), keep.Id, ct);
        return Outcome<long>.Success(keep.Id);
    }

    /// <summary>The name for comparing: small letters, without accents, everything but letters and digits a single space (<c>Dr. Jürgen  Müller</c> → <c>dr jurgen muller</c>).</summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (space && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(ch == 'ß' ? "ss" : ch.ToString());
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>The most contacts one merge or one "not a duplicate" takes.</summary>
    private const int DuplicateLimit = 50;

    private static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);

    private static async Task<HashSet<(long, long)>> IgnoredPairsAsync(AppDbContext db, CancellationToken ct)
        => [.. (await db.DuplicateIgnores.AsNoTracking().Select(i => new { i.ContactIdLow, i.ContactIdHigh }).ToListAsync(ct)).Select(i => (i.ContactIdLow, i.ContactIdHigh))];

    /// <summary>Adds the pairs of <paramref name="ids"/> that are not marked yet (only those with <paramref name="onlyWith"/> when it is given); answers how many.</summary>
    private static async Task<int> AddIgnoresAsync(AppDbContext db, IReadOnlyList<long> ids, CancellationToken ct, long? onlyWith = null)
    {
        var existing = await db.DuplicateIgnores.AsNoTracking().Where(i => ids.Contains(i.ContactIdLow) && ids.Contains(i.ContactIdHigh))
            .Select(i => new { i.ContactIdLow, i.ContactIdHigh }).ToListAsync(ct);
        var known = existing.Select(i => (i.ContactIdLow, i.ContactIdHigh)).ToHashSet();
        var added = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                if (onlyWith is { } with && ids[i] != with && ids[j] != with)
                {
                    continue;
                }

                var pair = Pair(ids[i], ids[j]);
                if (pair.Item1 != pair.Item2 && known.Add(pair))
                {
                    db.DuplicateIgnores.Add(new DuplicateIgnore { ContactIdLow = pair.Item1, ContactIdHigh = pair.Item2 });
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>Fills the empty fields of <paramref name="keep"/> from <paramref name="others"/> (the first value wins); the e-mail addresses go into its free ones.</summary>
    private static void Fill(Contact keep, IReadOnlyList<Contact> others)
    {
        foreach (var field in ContactFields.All.Where(f => f.Kind != ContactFieldKind.Email && f.Name != "DisplayName"))
        {
            if (field.Get(keep) is not null)
            {
                continue;
            }

            if (others.Select(field.Get).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } value)
            {
                field.Set(keep, value);
            }
        }

        keep.Birthday ??= others.Select(o => o.Birthday).FirstOrDefault(b => b is not null);
        var mails = ContactFields.Emails.Select(f => f.Get(keep)).Concat(others.SelectMany(o => ContactFields.Emails.Select(f => f.Get(o))))
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(ContactFields.Emails.Count).ToList();
        for (var i = 0; i < ContactFields.Emails.Count; i++)
        {
            ContactFields.Emails[i].Set(keep, i < mails.Count ? mails[i] : null);
        }
    }

    private static DuplicateMember Member(Contact c, AddressBookRole role)
    {
        var filled = ContactFields.All.Count(f => !string.IsNullOrWhiteSpace(f.Get(c))) + (c.Birthday is null ? 0 : 1);
        return new DuplicateMember(c.Id, c.DisplayName, c.Company, c.Email ?? c.Email2 ?? c.Email3, c.PhoneBusiness ?? c.PhoneMobile ?? c.PhoneBusiness2 ?? c.PhoneHome ?? c.PhoneOther,
            c.City, c.AddressBookId, c.AddressBook?.Name ?? string.Empty, c.AddressBook?.Color, c.DataSourceId is not null, c.DataSource?.Name, c.Hidden, role, filled);
    }
}
