using System.Security.Cryptography;

namespace MatContactHub.Services.Ldap;

/// <summary>One page of a paged search: the result, the entries of the page, the cookie for the next one (empty at the end) and how many there are in all.</summary>
public sealed record LdapPage(LdapResult Result, IReadOnlyList<LdapEntry> Entries, byte[] Cookie, int Total);

/// <summary>
/// The paged searches of one connection (the simple paged results control, RFC 2696). The first request runs the search on the snapshot of that moment and keeps
/// the matching entries; every page sends the next ones and a new cookie, so the pages fit together even when the directory changes meanwhile. A page never has
/// more than the size limit of the settings; the size limit of the client counts for the whole search (sizeLimitExceeded on the last page). A cookie that is
/// not known, or one used for another search, is refused (unwillingToPerform); a page size of 0 with a cookie ends the search. At most a few searches are kept
/// open per connection (the oldest goes).
/// </summary>
public sealed class LdapPagedSearches(int maxOpen)
{
    private readonly Dictionary<string, State> _open = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _clock;

    public int Open
    {
        get
        {
            lock (_lock)
            {
                return _open.Count;
            }
        }
    }

    public LdapPage Next(LdapDirectory directory, LdapIdentity who, SearchRequest request, int pageSize, byte[] cookie, int serverLimit, TimeSpan timeLimit,
        CancellationToken ct)
    {
        var key = KeyOf(request);
        State? state;
        if (cookie.Length == 0)
        {
            if (pageSize == 0)
            {
                return new LdapPage(LdapResult.Ok, [], [], 0);
            }

            var outcome = LdapSearchEngine.Execute(directory, who, request, request.SizeLimit > 0 ? request.SizeLimit : int.MaxValue, timeLimit, ct);
            if (outcome.Result.Code is not (LdapResultCode.Success or LdapResultCode.SizeLimitExceeded))
            {
                // an error, or the time ran out: what was found is sent at once, without a cookie
                return new LdapPage(outcome.Result, outcome.Entries, [], outcome.Entries.Count);
            }

            state = new State(key, [.. outcome.Entries], outcome.Result);
        }
        else
        {
            lock (_lock)
            {
                _open.Remove(Convert.ToBase64String(cookie), out state);
            }

            if (state is null)
            {
                return Refuse("The cookie of the paged search is not known (it expired or was used already).");
            }

            if (state.Request != key)
            {
                return Refuse("The paged search changed between its pages.");
            }

            if (pageSize == 0)
            {
                return new LdapPage(LdapResult.Ok, [], [], state.Entries.Length);
            }

            if (directory.VisibleBooks(who) is null && state.Entries.Any(e => e.Kind is not (LdapEntryKind.RootDse or LdapEntryKind.Subschema)))
            {
                return new LdapPage(new LdapResult(LdapResultCode.InsufficientAccessRights, string.Empty, "The account may not search any more."), [], [], 0);
            }
        }

        var take = Math.Min(pageSize, Math.Max(1, serverLimit));
        var page = state.Entries.Skip(state.Offset).Take(take).ToList();
        state.Offset += page.Count;
        if (state.Offset >= state.Entries.Length)
        {
            return new LdapPage(state.Final, page, [], state.Entries.Length);
        }

        var next = RandomNumberGenerator.GetBytes(16);
        lock (_lock)
        {
            if (_open.Count >= maxOpen)
            {
                var oldest = _open.MinBy(p => p.Value.Touched).Key;
                _open.Remove(oldest);
            }

            state.Touched = ++_clock;
            _open[Convert.ToBase64String(next)] = state;
        }

        return new LdapPage(LdapResult.Ok, page, next, state.Entries.Length);
    }

    /// <summary>Forgets every open search (Unbind, StartTLS, a new bind).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _open.Clear();
        }
    }

    private static LdapPage Refuse(string message) => new(new LdapResult(LdapResultCode.UnwillingToPerform, string.Empty, message), [], [], 0);

    /// <summary>What makes two requests the same search: base, scope, filter, the attributes, typesOnly.</summary>
    private static string KeyOf(SearchRequest r)
        => string.Join('\u001f', LdapDn.Normalize(r.BaseDn) ?? r.BaseDn, (int)r.Scope, r.Filter.ToString(), r.TypesOnly, string.Join(',', r.Attributes).ToLowerInvariant());

    private sealed class State(string request, LdapEntry[] entries, LdapResult final)
    {
        public string Request { get; } = request;

        public LdapEntry[] Entries { get; } = entries;

        public LdapResult Final { get; } = final;

        public int Offset { get; set; }

        public long Touched { get; set; }
    }
}
