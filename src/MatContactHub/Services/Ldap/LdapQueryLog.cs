namespace MatContactHub.Services.Ldap;

/// <summary>One line of the query log: when, from where, as whom, what (a search: base, scope, filter; a bind), how many entries, how it ended, how long it took.</summary>
public sealed record LdapLogEntry(DateTime Time, string Client, string? BindName, string Operation, string? BaseDn, string? Scope, string? Filter, int Results,
    LdapResultCode Code, double Milliseconds, bool Secure);

/// <summary>The lines of the log a page shows.</summary>
public sealed record LdapLogPage(IReadOnlyList<LdapLogEntry> Items, int Total, int Matching);

/// <summary>
/// The query log of the LDAP server: the last <see cref="Capacity"/> searches and binds in memory (a ring buffer; gone at a restart). It answers the question
/// "what does my telephone ask, and does it find anything?" on the tab Log. Only administrators read it (<see cref="Read"/> checks the actor).
/// </summary>
public sealed class LdapQueryLog
{
    public const int Capacity = 500;

    private readonly LdapLogEntry?[] _ring = new LdapLogEntry?[Capacity];
    private readonly Lock _lock = new();
    private int _next;
    private int _count;

    public void Add(LdapLogEntry entry)
    {
        lock (_lock)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    /// <summary>Every line, the newest first.</summary>
    public IReadOnlyList<LdapLogEntry> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<LdapLogEntry>(_count);
            for (var i = 1; i <= _count; i++)
            {
                list.Add(_ring[(_next - i + Capacity) % Capacity]!);
            }

            return list;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// A page of the log for an administrator (null for anybody else): <paramref name="search"/> looks in the client, the bind name, the base and the filter;
    /// <paramref name="outcome"/> is <c>ok</c> (success), <c>empty</c> (a search without entries), <c>error</c> (anything else) or <c>bind</c> (the binds).
    /// </summary>
    public LdapLogPage? Read(Actor actor, string? search, string? outcome, int page, int pageSize)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var all = Snapshot();
        IEnumerable<LdapLogEntry> query = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(e => Has(e.Client, s) || Has(e.BindName, s) || Has(e.BaseDn, s) || Has(e.Filter, s));
        }

        query = outcome switch
        {
            "ok" => query.Where(e => e.Code == LdapResultCode.Success && (e.Operation != "search" || e.Results > 0)),
            "empty" => query.Where(e => e.Operation == "search" && e.Code == LdapResultCode.Success && e.Results == 0),
            "error" => query.Where(e => e.Code != LdapResultCode.Success),
            "bind" => query.Where(e => e.Operation == "bind"),
            _ => query,
        };
        var matching = query.ToList();
        return new LdapLogPage([.. matching.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)], all.Count, matching.Count);
    }

    /// <summary>Empties the log (administrators only); false for anybody else.</summary>
    public bool Clear(Actor actor)
    {
        if (!actor.IsAdmin)
        {
            return false;
        }

        Clear();
        return true;
    }

    private static bool Has(string? text, string search) => text is not null && text.Contains(search, StringComparison.OrdinalIgnoreCase);
}
