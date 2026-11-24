using System.Diagnostics;

namespace MatContactHub.Services.Ldap;

/// <summary>What a search found: the result (success, sizeLimitExceeded, timeLimitExceeded or an error) and the entries to send (the partial ones with a limit).</summary>
public sealed record LdapSearchOutcome(LdapResult Result, IReadOnlyList<LdapEntry> Entries);

/// <summary>Which attributes a search asked for (RFC 4511, 4.5.1.8): none (<c>1.1</c>), all user attributes (none named or <c>*</c>), all operational ones
/// (<c>+</c>, RFC 3673), and the ones named (by any of their names).</summary>
public sealed class LdapAttributeSelection
{
    private readonly HashSet<string> _named = new(StringComparer.OrdinalIgnoreCase);

    public LdapAttributeSelection(IReadOnlyList<string> requested)
    {
        var list = requested.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        AllUser = list.Count == 0 || list.Contains("*");
        AllOperational = list.Contains("+");
        foreach (var a in list.Where(a => a is not ("*" or "+" or "1.1")))
        {
            _named.Add(LdapAttributes.Canonical(a));
        }
    }

    public bool AllUser { get; }

    public bool AllOperational { get; }

    /// <summary>Is the attribute sent? The root DSE and the subschema send all their attributes for <c>*</c> (old clients read them that way).</summary>
    public bool Includes(LdapEntry entry, LdapValues attribute)
    {
        if (_named.Contains(attribute.Name))
        {
            return true;
        }

        var operational = LdapAttributes.Operational.Contains(attribute.Name);
        if (entry.Kind is LdapEntryKind.RootDse or LdapEntryKind.Subschema && !operational)
        {
            return AllUser || AllOperational;
        }

        return operational ? AllOperational : AllUser;
    }

    /// <summary>The entry as it is sent: the selected attributes, without values for <paramref name="typesOnly"/>.</summary>
    public SearchResultEntry ToResult(LdapEntry entry, bool typesOnly)
        => new(entry.Dn, [.. entry.Attributes.Where(a => Includes(entry, a)).Select(a => new LdapAttribute(a.Name, typesOnly ? [] : a.Values))]);
}

/// <summary>
/// The search of the LDAP server on one snapshot (<see cref="LdapDirectory"/>), without the network: who may search, the base, the scope, the filter, the limits.
/// The root DSE (base "" with scope base) and the subschema are open to everybody; everything else needs a bound account, or an anonymous connection when the
/// settings allow it, and shows only the address books that account reads. What may not be seen is answered like what does not exist (noSuchObject).
/// A base of "" with scope one or sub searches the tree under the base DN (a telephone with an empty base still finds its contacts).
/// </summary>
public static class LdapSearchEngine
{
    /// <summary>How often the time is looked at (every n entries).</summary>
    private const int TimeCheckEvery = 256;

    /// <summary>
    /// Runs a search. At most <paramref name="maxEntries"/> entries come back; when more match the result is sizeLimitExceeded with the first ones, when the search
    /// takes longer than <paramref name="timeLimit"/> it is timeLimitExceeded with what was found so far. <paramref name="ct"/> stops it (Abandon).
    /// </summary>
    public static LdapSearchOutcome Execute(LdapDirectory directory, LdapIdentity who, SearchRequest request, int maxEntries, TimeSpan timeLimit, CancellationToken ct)
    {
        var rdns = LdapDn.Parse(request.BaseDn);
        if (rdns is null)
        {
            return Fail(LdapResultCode.InvalidDnSyntax, $"'{request.BaseDn}' is no DN.");
        }

        var key = LdapDn.Join(rdns);
        var matcher = LdapFilterEvaluator.Compile(request.Filter, directory.Phone);

        // what anybody may read
        if (request.Scope == LdapScope.BaseObject && (key.Length == 0 || key == directory.Subschema.Key))
        {
            var entry = key.Length == 0 ? directory.RootDse : directory.Subschema;
            return new LdapSearchOutcome(LdapResult.Ok, matcher.Matches(entry) ? [entry] : []);
        }

        var visible = directory.VisibleBooks(who);
        if (visible is null)
        {
            return Fail(LdapResultCode.InsufficientAccessRights,
                who.IsAnonymous ? "Anonymous searches are off: bind with an LDAP account." : "The account is disabled or does not exist any more.");
        }

        IEnumerable<LdapEntry> candidates;
        if (key.Length == 0)
        {
            candidates = request.Scope == LdapScope.SingleLevel ? [directory.Base] : Subtree(directory, directory.Base, visible);
        }
        else
        {
            var entry = directory.Find(key);
            if (entry is null || !IsVisible(entry, visible) || entry.Kind is LdapEntryKind.RootDse or LdapEntryKind.Subschema)
            {
                return new LdapSearchOutcome(new LdapResult(LdapResultCode.NoSuchObject, MatchedDn(directory, rdns, visible), "No such entry."), []);
            }

            candidates = request.Scope switch
            {
                LdapScope.BaseObject => [entry],
                LdapScope.SingleLevel => Children(directory, entry, visible),
                _ => Subtree(directory, entry, visible),
            };
        }

        var found = new List<LdapEntry>();
        var watch = Stopwatch.StartNew();
        var seen = 0;
        foreach (var candidate in candidates)
        {
            if (++seen % TimeCheckEvery == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (watch.Elapsed > timeLimit)
                {
                    return new LdapSearchOutcome(new LdapResult(LdapResultCode.TimeLimitExceeded, string.Empty, "The time limit was reached."), found);
                }
            }

            if (!matcher.Matches(candidate))
            {
                continue;
            }

            if (found.Count >= maxEntries)
            {
                return new LdapSearchOutcome(new LdapResult(LdapResultCode.SizeLimitExceeded, string.Empty, $"More than {maxEntries} entries match."), found);
            }

            found.Add(candidate);
        }

        return new LdapSearchOutcome(LdapResult.Ok, found);
    }

    private static LdapSearchOutcome Fail(LdapResultCode code, string message) => new(new LdapResult(code, string.Empty, message), []);

    private static bool IsVisible(LdapEntry entry, IReadOnlySet<long> visible)
        => entry.Kind is not (LdapEntryKind.AddressBook or LdapEntryKind.Contact) || visible.Contains(entry.AddressBookId);

    private static IEnumerable<LdapEntry> Children(LdapDirectory directory, LdapEntry entry, IReadOnlySet<long> visible) => entry.Kind switch
    {
        LdapEntryKind.Base => directory.Books.Where(b => visible.Contains(b.AddressBookId)),
        LdapEntryKind.AddressBook => directory.ContactsOf(entry.AddressBookId),
        _ => [],
    };

    private static IEnumerable<LdapEntry> Subtree(LdapDirectory directory, LdapEntry entry, IReadOnlySet<long> visible)
    {
        yield return entry;
        foreach (var child in Children(directory, entry, visible))
        {
            foreach (var e in Subtree(directory, child, visible))
            {
                yield return e;
            }
        }
    }

    /// <summary>The DN of the nearest parent that exists and may be seen (the matchedDN of noSuchObject).</summary>
    private static string MatchedDn(LdapDirectory directory, IReadOnlyList<LdapRdn> rdns, IReadOnlySet<long> visible)
    {
        for (var i = 1; i < rdns.Count; i++)
        {
            var parent = directory.Find(LdapDn.Join(rdns.Skip(i)));
            if (parent is not null && IsVisible(parent, visible) && parent.Kind is not (LdapEntryKind.RootDse or LdapEntryKind.Subschema))
            {
                return parent.Dn;
            }
        }

        return string.Empty;
    }
}
