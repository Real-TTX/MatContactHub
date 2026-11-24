using MatContactHub.Services.Ldap;

namespace MatContactHub.Tests;

/// <summary>The search on the snapshot: who may search, the tree and the scopes, the limits, the attributes, the paged results.</summary>
public sealed class LdapSearchTests
{
    private static readonly LdapIdentity Starface = new("starface");
    private static readonly LdapIdentity Phone = new("phone");
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static SearchRequest Request(string baseDn, LdapScope scope, string filter = "(objectClass=*)", int sizeLimit = 0, params string[] attributes)
        => new(baseDn, scope, LdapDerefAliases.Never, sizeLimit, 0, false, LdapFilter.Parse(filter), attributes);

    private static LdapSearchOutcome Search(LdapDirectory d, LdapIdentity who, SearchRequest r, int max = 1000) => LdapSearchEngine.Execute(d, who, r, max, Minute, CancellationToken.None);

    [Fact]
    public void The_root_DSE_and_the_subschema_are_open_to_everybody()
    {
        var d = LdapTestData.Directory();
        var root = Search(d, LdapIdentity.Anonymous, Request(string.Empty, LdapScope.BaseObject));
        Assert.Equal(LdapResultCode.Success, root.Result.Code);
        var entry = Assert.Single(root.Entries);
        Assert.Equal(LdapTestData.Base, entry.First("namingContexts"));
        Assert.Contains(LdapOids.PagedResults, entry.Get("supportedControl")!.Values);
        Assert.Contains(LdapOids.StartTls, entry.Get("supportedExtension")!.Values);
        Assert.Equal("3", entry.First("supportedLDAPVersion"));

        var schema = Search(d, LdapIdentity.Anonymous, Request("cn=subschema", LdapScope.BaseObject));
        Assert.NotEmpty(Assert.Single(schema.Entries).Get("attributeTypes")!.Values);
    }

    [Fact]
    public void Anonymous_searches_need_the_setting()
    {
        var closed = Search(LdapTestData.Directory(), LdapIdentity.Anonymous, Request(LdapTestData.Base, LdapScope.WholeSubtree));
        Assert.Equal(LdapResultCode.InsufficientAccessRights, closed.Result.Code);
        Assert.Empty(closed.Entries);

        var open = Search(LdapTestData.Directory(LdapTestData.Settings(anonymous: true)), LdapIdentity.Anonymous, Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=inetOrgPerson)"));
        Assert.Equal(3, open.Entries.Count);
    }

    [Fact]
    public void A_disabled_or_unknown_account_may_not_search()
    {
        var d = LdapTestData.Directory();
        Assert.Equal(LdapResultCode.InsufficientAccessRights, Search(d, new LdapIdentity("disabled"), Request(LdapTestData.Base, LdapScope.WholeSubtree)).Result.Code);
        Assert.Equal(LdapResultCode.InsufficientAccessRights, Search(d, new LdapIdentity("nobody"), Request(LdapTestData.Base, LdapScope.WholeSubtree)).Result.Code);
    }

    [Fact]
    public void The_scopes_walk_the_tree()
    {
        var d = LdapTestData.Directory();
        Assert.Equal(LdapTestData.Base, Assert.Single(Search(d, Starface, Request(LdapTestData.Base, LdapScope.BaseObject)).Entries).Dn);

        var one = Search(d, Starface, Request(LdapTestData.Base, LdapScope.SingleLevel)).Entries.Select(e => e.Dn).ToList();
        Assert.Equal([$"ou=staff,{LdapTestData.Base}", $"ou=customers,{LdapTestData.Base}"], one);

        var sub = Search(d, Starface, Request(LdapTestData.Base, LdapScope.WholeSubtree)).Entries;
        Assert.Equal(6, sub.Count);

        var customers = Search(d, Starface, Request($"OU=Customers, {LdapTestData.Base.ToUpperInvariant()}", LdapScope.SingleLevel)).Entries;
        Assert.Equal(["Bob Builder", "Carla Müller"], customers.Select(e => e.First("cn")));

        var ann = Search(d, Starface, Request($"uid={LdapTestData.Ann},ou=staff,{LdapTestData.Base}", LdapScope.BaseObject));
        Assert.Equal("Ann Example", Assert.Single(ann.Entries).First("cn"));

        // an empty base searches the tree under the base DN (a telephone without a base still finds its contacts)
        Assert.Equal(3, Search(d, Starface, Request(string.Empty, LdapScope.WholeSubtree, "(objectClass=person)")).Entries.Count);
    }

    [Fact]
    public void An_account_sees_only_its_address_books()
    {
        var d = LdapTestData.Directory();
        var mine = Search(d, Phone, Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)")).Entries;
        Assert.Equal(["Bob Builder", "Carla Müller"], mine.Select(e => e.First("cn")));

        var other = Search(d, Phone, Request($"ou=staff,{LdapTestData.Base}", LdapScope.WholeSubtree));
        Assert.Equal(LdapResultCode.NoSuchObject, other.Result.Code);
        Assert.Equal(LdapTestData.Base, other.Result.MatchedDn);

        var annDirect = Search(d, Phone, Request($"uid={LdapTestData.Ann},ou=staff,{LdapTestData.Base}", LdapScope.BaseObject));
        Assert.Equal(LdapResultCode.NoSuchObject, annDirect.Result.Code);
    }

    [Fact]
    public void A_base_that_does_not_exist_or_is_no_DN()
    {
        var d = LdapTestData.Directory();
        var missing = Search(d, Starface, Request($"uid=x,ou=customers,{LdapTestData.Base}", LdapScope.BaseObject));
        Assert.Equal(LdapResultCode.NoSuchObject, missing.Result.Code);
        Assert.Equal($"ou=customers,{LdapTestData.Base}", missing.Result.MatchedDn);
        Assert.Equal(LdapResultCode.NoSuchObject, Search(d, Starface, Request("dc=other,dc=org", LdapScope.WholeSubtree)).Result.Code);
        Assert.Equal(LdapResultCode.InvalidDnSyntax, Search(d, Starface, Request("not a dn", LdapScope.WholeSubtree)).Result.Code);
    }

    [Fact]
    public void The_size_limit_gives_the_first_entries_and_says_so()
    {
        var d = LdapTestData.Directory(extra: 10);
        var limited = Search(d, Starface, Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)"), max: 5);
        Assert.Equal(LdapResultCode.SizeLimitExceeded, limited.Result.Code);
        Assert.Equal(5, limited.Entries.Count);

        var exact = Search(d, Starface, Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)"), max: 13);
        Assert.Equal(LdapResultCode.Success, exact.Result.Code);
        Assert.Equal(13, exact.Entries.Count);
    }

    [Fact]
    public void The_attribute_selection_follows_the_request()
    {
        var d = LdapTestData.Directory();
        var ann = d.Find(LdapDn.Normalize($"uid={LdapTestData.Ann},ou=staff,{LdapTestData.Base}")!)!;
        string[] Names(params string[] requested) => [.. new LdapAttributeSelection(requested).ToResult(ann, false).Attributes.Select(a => a.Type)];

        var all = Names();
        Assert.Contains("cn", all);
        Assert.Contains("telephoneNumber", all);
        Assert.DoesNotContain("entryUUID", all);
        Assert.Equal(all, Names("*"));
        Assert.Empty(Names("1.1"));
        Assert.Equal(["cn", "mobile"], Names("CN", "mobileTelephoneNumber").Order());
        var operational = Names("+");
        Assert.Contains("entryUUID", operational);
        Assert.DoesNotContain("cn", operational);
        Assert.Contains("entryUUID", Names("*", "+"));
        Assert.Contains("cn", Names("*", "+"));
        Assert.Equal(["cn"], Names("cn", "1.1"));

        var typesOnly = new LdapAttributeSelection(["cn", "mail"]).ToResult(ann, true);
        Assert.All(typesOnly.Attributes, a => Assert.Empty(a.Values));
    }

    [Fact]
    public void Paged_results_come_in_pages_and_end_with_an_empty_cookie()
    {
        var d = LdapTestData.Directory(extra: 25);
        var paged = new LdapPagedSearches(4);
        var request = Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)");
        var seen = new List<string>();
        byte[] cookie = [];
        var pages = 0;
        do
        {
            var page = paged.Next(d, Starface, request, 10, cookie, 1000, Minute, CancellationToken.None);
            Assert.Equal(LdapResultCode.Success, page.Result.Code);
            Assert.Equal(28, page.Total);
            seen.AddRange(page.Entries.Select(e => e.Dn));
            cookie = page.Cookie;
            pages++;
        }
        while (cookie.Length > 0);

        Assert.Equal(3, pages);
        Assert.Equal(28, seen.Distinct().Count());
        Assert.Equal(0, paged.Open);
    }

    [Fact]
    public void A_page_is_never_larger_than_the_size_limit_of_the_settings()
    {
        var d = LdapTestData.Directory(extra: 25);
        var page = new LdapPagedSearches(4).Next(d, Starface, Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)"), 100, [], 7, Minute, CancellationToken.None);
        Assert.Equal(7, page.Entries.Count);
        Assert.NotEmpty(page.Cookie);
    }

    [Fact]
    public void A_wrong_or_used_cookie_is_refused_and_page_size_zero_ends_the_search()
    {
        var d = LdapTestData.Directory(extra: 25);
        var paged = new LdapPagedSearches(4);
        var request = Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)");
        var first = paged.Next(d, Starface, request, 10, [], 1000, Minute, CancellationToken.None);

        var other = Request(LdapTestData.Base, LdapScope.WholeSubtree, "(cn=Ann*)");
        Assert.Equal(LdapResultCode.UnwillingToPerform, paged.Next(d, Starface, other, 10, first.Cookie, 1000, Minute, CancellationToken.None).Result.Code);

        // the cookie was taken by the refused request: it is gone
        Assert.Equal(LdapResultCode.UnwillingToPerform, paged.Next(d, Starface, request, 10, first.Cookie, 1000, Minute, CancellationToken.None).Result.Code);
        Assert.Equal(LdapResultCode.UnwillingToPerform, paged.Next(d, Starface, request, 10, [1, 2, 3], 1000, Minute, CancellationToken.None).Result.Code);

        var again = paged.Next(d, Starface, request, 10, [], 1000, Minute, CancellationToken.None);
        var end = paged.Next(d, Starface, request, 0, again.Cookie, 1000, Minute, CancellationToken.None);
        Assert.Equal(LdapResultCode.Success, end.Result.Code);
        Assert.Empty(end.Entries);
        Assert.Empty(end.Cookie);
        Assert.Equal(0, paged.Open);
    }

    [Fact]
    public void The_size_limit_of_the_client_counts_for_the_whole_paged_search()
    {
        var d = LdapTestData.Directory(extra: 25);
        var paged = new LdapPagedSearches(4);
        var request = Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)", sizeLimit: 15);
        var first = paged.Next(d, Starface, request, 10, [], 1000, Minute, CancellationToken.None);
        Assert.Equal(10, first.Entries.Count);
        var last = paged.Next(d, Starface, request, 10, first.Cookie, 1000, Minute, CancellationToken.None);
        Assert.Equal(5, last.Entries.Count);
        Assert.Equal(LdapResultCode.SizeLimitExceeded, last.Result.Code);
        Assert.Empty(last.Cookie);
    }

    [Fact]
    public void The_oldest_paged_search_goes_when_too_many_are_open()
    {
        var d = LdapTestData.Directory(extra: 25);
        var paged = new LdapPagedSearches(2);
        var request = Request(LdapTestData.Base, LdapScope.WholeSubtree, "(objectClass=person)");
        var a = paged.Next(d, Starface, request, 5, [], 1000, Minute, CancellationToken.None);
        paged.Next(d, Starface, request, 5, [], 1000, Minute, CancellationToken.None);
        paged.Next(d, Starface, request, 5, [], 1000, Minute, CancellationToken.None);
        Assert.Equal(2, paged.Open);
        Assert.Equal(LdapResultCode.UnwillingToPerform, paged.Next(d, Starface, request, 5, a.Cookie, 1000, Minute, CancellationToken.None).Result.Code);
    }

    [Theory]
    [InlineData("starface", "starface")]
    [InlineData("STARFACE", "starface")]
    [InlineData("cn=starface,dc=matcontacthub,dc=local", "starface")]
    [InlineData("CN=Starface, DC=MatContactHub, DC=Local", "starface")]
    [InlineData("uid=starface,dc=matcontacthub,dc=local", "starface")]
    [InlineData("cn=starface", "starface")]
    [InlineData("cn=starface,dc=other,dc=org", null)]
    [InlineData("o=starface,dc=matcontacthub,dc=local", null)]
    [InlineData("cn=starface,ou=x,dc=matcontacthub,dc=local", null)]
    public void Bind_names_in_every_accepted_form(string bindName, string? account)
        => Assert.Equal(account, LdapSession.AccountNameOf(bindName, LdapTestData.Base));

    [Fact]
    public void The_bind_guard_refuses_an_address_after_too_many_failures_until_the_window_passed()
    {
        var time = new ManualTime();
        var guard = new LdapBindGuard(3, TimeSpan.FromMinutes(5), time);
        Assert.False(guard.IsBlocked("10.0.0.1"));
        for (var i = 1; i <= 2; i++)
        {
            Assert.True(guard.TryBegin("10.0.0.1"));
            Assert.Equal(i, guard.Failed("10.0.0.1"));
        }

        Assert.False(guard.IsBlocked("10.0.0.1"));
        Assert.True(guard.TryBegin("10.0.0.1"));
        Assert.Equal(3, guard.Failed("10.0.0.1"));
        Assert.True(guard.IsBlocked("10.0.0.1"));
        Assert.False(guard.TryBegin("10.0.0.1"));
        Assert.False(guard.IsBlocked("10.0.0.2"));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(guard.IsBlocked("10.0.0.1"));
        Assert.True(guard.TryBegin("10.0.0.1"));
    }

    [Fact]
    public void The_bind_guard_counts_attempts_that_are_still_being_checked()
    {
        // binds at the same time on several connections: each one reserves its attempt before the password is checked
        var guard = new LdapBindGuard(3, TimeSpan.FromMinutes(5), new ManualTime());
        Assert.True(guard.TryBegin("10.0.0.1"));
        Assert.True(guard.TryBegin("10.0.0.1"));
        Assert.True(guard.TryBegin("10.0.0.1"));
        Assert.False(guard.TryBegin("10.0.0.1"));
        Assert.True(guard.IsBlocked("10.0.0.1"));

        // a success gives its attempt back, a failure keeps it
        guard.Succeeded("10.0.0.1");
        Assert.Equal(1, guard.Failed("10.0.0.1"));
        Assert.True(guard.TryBegin("10.0.0.1"));
        Assert.False(guard.TryBegin("10.0.0.1"));

        // many at once never get more than the limit through
        var parallel = new LdapBindGuard(10, TimeSpan.FromMinutes(5), new ManualTime());
        var granted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (parallel.TryBegin("10.0.0.9"))
            {
                Interlocked.Increment(ref granted);
            }
        });
        Assert.Equal(10, granted);
    }

    [Fact]
    public void The_query_log_keeps_the_last_500_for_administrators_only()
    {
        var log = new LdapQueryLog();
        for (var i = 0; i < 600; i++)
        {
            log.Add(new LdapLogEntry(DateTime.UtcNow, "10.0.0.1", "starface", "search", LdapTestData.Base, "sub", $"(cn=n{i})", i % 2, LdapResultCode.Success, 1, false));
        }

        Assert.Null(log.Read(MatContactHub.Services.Actor.ForPerson(5, false, "x"), null, null, 1, 25));
        var page = log.Read(MatContactHub.Services.Actor.System("t"), null, null, 1, 25)!;
        Assert.Equal(500, page.Total);
        Assert.Equal("(cn=n599)", page.Items[0].Filter);
        Assert.Equal(250, log.Read(MatContactHub.Services.Actor.System("t"), null, "empty", 1, 25)!.Matching);
        Assert.Equal(1, log.Read(MatContactHub.Services.Actor.System("t"), "n123)", null, 1, 25)!.Matching);
        Assert.True(log.Clear(MatContactHub.Services.Actor.System("t")));
        Assert.Empty(log.Snapshot());
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
