using System.Net.Sockets;
using System.Text;
using MatContactHub.Services.Ldap;
using Novell.Directory.Ldap;
using Novell.Directory.Ldap.Controls;
using LdapClient = Novell.Directory.Ldap.LdapConnection;
using LdapControl = Novell.Directory.Ldap.LdapControl;
using LdapEntry = Novell.Directory.Ldap.LdapEntry;
using LdapMessage = MatContactHub.Services.Ldap.LdapMessage;

namespace MatContactHub.Tests;

/// <summary>
/// The LDAP server over the network, with a real client (Novell.Directory.Ldap.NETStandard) on a port the system chose: binds, searches by name and by number,
/// paging, StartTLS and LDAPS with the self-signed certificate, and bytes that are not LDAP (the server stays up).
/// </summary>
public sealed class LdapServerTests : IAsyncLifetime
{
    private readonly TestLdapContext _ctx = new(LdapTestData.Directory(extra: 30));
    private LdapServer _server = null!;

    private int Port => _server.PlainPort!.Value;

    private int SecurePort => _server.SecurePort!.Value;

    public Task InitializeAsync()
    {
        _server = new LdapServer(_ctx);
        _server.Apply(true, true);
        Assert.Null(_server.PlainError);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.StopAsync();

    private async Task<LdapClient> ConnectAsync(bool bind = true)
    {
        var client = new LdapClient();
        await client.ConnectAsync("127.0.0.1", Port);
        if (bind)
        {
            await client.BindAsync($"cn=starface,{LdapTestData.Base}", LdapTestData.StarfacePassword);
        }

        return client;
    }

    private static async Task<List<LdapEntry>> AllAsync(ILdapSearchResults results)
    {
        var list = new List<LdapEntry>();
        while (await results.HasMoreAsync())
        {
            list.Add(await results.NextAsync());
        }

        return list;
    }

    private static Task<List<LdapEntry>> SearchAsync(LdapClient client, string filter, string? baseDn = null, string[]? attributes = null)
        => client.SearchAsync(baseDn ?? LdapTestData.Base, LdapClient.ScopeSub, filter, attributes, false).ContinueWith(t => AllAsync(t.Result)).Unwrap();

    [Fact]
    public async Task A_bind_with_the_right_password_works_in_every_name_form()
    {
        foreach (var name in new[] { "starface", "STARFACE", $"cn=starface,{LdapTestData.Base}", $"uid=starface,{LdapTestData.Base}" })
        {
            using var client = await ConnectAsync(bind: false);
            await client.BindAsync(name, LdapTestData.StarfacePassword);
            Assert.True(client.Bound);
        }
    }

    [Fact]
    public async Task A_wrong_password_an_unknown_or_a_disabled_account_is_refused()
    {
        foreach (var (name, password) in new[] { ("starface", "wrong"), ("nobody", "whatever"), ("disabled", LdapTestData.StarfacePassword) })
        {
            using var client = await ConnectAsync(bind: false);
            var e = await Assert.ThrowsAsync<LdapException>(() => client.BindAsync(name, password));
            Assert.Equal(LdapException.InvalidCredentials, e.ResultCode);
        }

        Assert.Contains("disabled", _ctx.Failures);
    }

    [Fact]
    public async Task Anonymous_is_refused_unless_the_settings_allow_it()
    {
        using (var client = await ConnectAsync(bind: false))
        {
            var e = await Assert.ThrowsAsync<LdapException>(() => SearchAsync(client, "(objectClass=person)"));
            Assert.Equal(LdapException.InsufficientAccessRights, e.ResultCode);

            // the root DSE is open to everybody
            var root = await client.SearchAsync(string.Empty, LdapClient.ScopeBase, "(objectClass=*)", null, false);
            var dse = Assert.Single(await AllAsync(root));
            Assert.Equal(LdapTestData.Base, dse.Get("namingContexts").StringValue);
        }

        _ctx.Directory = LdapTestData.Directory(LdapTestData.Settings(anonymous: true));
        using (var client = await ConnectAsync(bind: false))
        {
            Assert.Equal(3, (await SearchAsync(client, "(objectClass=person)")).Count);
        }
    }

    [Fact]
    public async Task Searches_by_name_find_the_contact()
    {
        using var client = await ConnectAsync();
        var byName = await SearchAsync(client, "(|(cn=*ann*)(sn=ann*)(givenName=ann*))", attributes: ["cn", "telephoneNumber", "mobile"]);
        var ann = Assert.Single(byName);
        Assert.Equal($"uid={LdapTestData.Ann},ou=staff,{LdapTestData.Base}", ann.Dn);
        Assert.Equal("Ann Example", ann.Get("cn").StringValue);
        Assert.Equal("+49 30 1234567", ann.Get("telephoneNumber").StringValue);
        Assert.Null(ann.GetOrDefault("mail", null!));

        var umlaut = Assert.Single(await SearchAsync(client, "(sn=MÜLLER)"));
        Assert.Equal("Carla Müller", umlaut.Get("cn").StringValue);
    }

    [Theory]
    [InlineData("+49301234567")]
    [InlineData("0049301234567")]
    [InlineData("0301234567")]
    [InlineData("301234567")]
    public async Task Searches_by_number_find_the_caller_in_every_form(string number)
    {
        using var client = await ConnectAsync();
        var found = await SearchAsync(client, $"(|(telephoneNumber=*{number})(mobile=*{number}))", attributes: ["cn"]);
        Assert.Equal("Ann Example", Assert.Single(found).Get("cn").StringValue);
    }

    [Fact]
    public async Task The_size_limit_answers_sizeLimitExceeded_after_the_first_entries()
    {
        using var client = await ConnectAsync();
        var constraints = new LdapSearchConstraints { MaxResults = 0 };
        var results = await client.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, "(objectClass=person)", ["cn"], false,
            new LdapSearchConstraints { MaxResults = 1000, BatchSize = 0 });
        Assert.Equal(33, (await AllAsync(results)).Count);

        _ctx.Directory = LdapTestData.Directory(LdapTestData.Settings(sizeLimit: 10), extra: 30);
        var limited = await client.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, "(objectClass=person)", ["cn"], false, constraints);
        var got = new List<LdapEntry>();
        var e = await Assert.ThrowsAsync<LdapException>(async () =>
        {
            while (await limited.HasMoreAsync())
            {
                got.Add(await limited.NextAsync());
            }
        });
        Assert.Equal(LdapException.SizeLimitExceeded, e.ResultCode);
        Assert.Equal(10, got.Count);
    }

    [Fact]
    public async Task Paged_results_work_across_requests_on_one_connection()
    {
        using var client = await ConnectAsync();
        var seen = new HashSet<string>();
        byte[] cookie = [];
        var pages = 0;
        do
        {
            var constraints = new LdapSearchConstraints();
            constraints.SetControls(new SimplePagedResultsControl(10, cookie));
            var results = await client.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, "(objectClass=person)", ["cn"], false, constraints);
            foreach (var entry in await AllAsync(results))
            {
                Assert.True(seen.Add(entry.Dn));
            }

            var control = results.ResponseControls?.OfType<SimplePagedResultsControl>().FirstOrDefault()
                          ?? throw new InvalidOperationException("No paged results control in the answer.");
            cookie = control.Cookie ?? [];
            pages++;
        }
        while (cookie.Length > 0 && pages < 10);

        Assert.Equal(4, pages);
        Assert.Equal(33, seen.Count);
    }

    [Fact]
    public async Task An_unknown_critical_control_is_refused_and_an_unknown_optional_one_ignored()
    {
        using var client = await ConnectAsync();
        var critical = new LdapSearchConstraints();
        critical.SetControls(new LdapControl("1.2.3.4.5", true, null));
        var e = await Assert.ThrowsAsync<LdapException>(async () =>
            await AllAsync(await client.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, "(cn=Ann*)", null, false, critical)));
        Assert.Equal(LdapException.UnavailableCriticalExtension, e.ResultCode);

        var optional = new LdapSearchConstraints();
        optional.SetControls(new LdapControl("1.2.3.4.5", false, null));
        Assert.Single(await AllAsync(await client.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, "(cn=Ann*)", null, false, optional)));
    }

    [Fact]
    public async Task Write_operations_are_refused()
    {
        using var client = await ConnectAsync();
        var e = await Assert.ThrowsAsync<LdapException>(() => client.DeleteAsync($"uid={LdapTestData.Ann},ou=staff,{LdapTestData.Base}"));
        Assert.Equal(LdapException.UnwillingToPerform, e.ResultCode);

        // the connection goes on
        Assert.Single(await SearchAsync(client, "(cn=Ann*)"));
    }

    [Fact]
    public async Task StartTls_encrypts_the_connection_with_the_self_signed_certificate()
    {
        using var client = new LdapClient(new LdapConnectionOptions().ConfigureRemoteCertificateValidationCallback((_, cert, _, _) => cert is not null));
        await client.ConnectAsync("127.0.0.1", Port);
        await client.StartTlsAsync();
        Assert.True(client.Tls);
        await client.BindAsync("starface", LdapTestData.StarfacePassword);
        Assert.Single(await SearchAsync(client, "(telephoneNumber=+49301234567)"));
    }

    [Fact]
    public async Task Ldaps_works_with_the_self_signed_certificate()
    {
        using var client = new LdapClient(new LdapConnectionOptions().UseSsl().ConfigureRemoteCertificateValidationCallback((_, cert, _, _) =>
            cert?.GetCertHashString() == LdapTestData.Certificate.GetCertHashString()));
        await client.ConnectAsync("127.0.0.1", SecurePort);
        await client.BindAsync($"cn=starface,{LdapTestData.Base}", LdapTestData.StarfacePassword);
        Assert.Single(await SearchAsync(client, "(mobile=*1712345678)"));
    }

    [Fact]
    public async Task StartTls_is_refused_when_it_is_off()
    {
        _ctx.Directory = LdapTestData.Directory(LdapTestData.Settings(startTls: false));
        using var client = new LdapClient();
        await client.ConnectAsync("127.0.0.1", Port);
        await Assert.ThrowsAsync<LdapException>(() => client.StartTlsAsync());
    }

    [Fact]
    public async Task Bytes_that_are_not_LDAP_close_that_connection_and_the_server_goes_on()
    {
        var garbage = new[]
        {
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"),
            [0x30, 0x80, 0x00, 0x00],
            [0x30, 0x84, 0x7f, 0xff, 0xff, 0xff, 0x00],
            [0x30, 0x03, 0x02, 0x01, 0x01],
            [0x30, 0x05, 0x02, 0x01, 0x00, 0x42, 0x00],
            [0x30, 0x06, 0x02, 0x01, 0x01, 0x7f, 0x01, 0x00],
        };
        var random = new Random(1466);
        foreach (var bytes in garbage.Concat(Enumerable.Range(0, 20).Select(_ =>
                 {
                     var b = new byte[random.Next(2, 200)];
                     random.NextBytes(b);
                     b[0] = 0x30;
                     b[1] = (byte)(b.Length - 2 < 128 ? b.Length - 2 : 0x7f);
                     return b;
                 })))
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync("127.0.0.1", Port);
            await socket.SendAsync(bytes, SocketFlags.None);
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                // a notice of disconnection, then the end of the stream
                while (await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token) > 0)
                {
                }
            }
            catch (SocketException)
            {
            }
        }

        using var client = await ConnectAsync();
        Assert.Single(await SearchAsync(client, "(cn=Ann*)"));
    }

    [Fact]
    public async Task A_notice_of_disconnection_answers_a_message_that_is_not_LDAP()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("127.0.0.1", Port);
        await socket.SendAsync(new byte[] { 0x30, 0x06, 0x02, 0x01, 0x01, 0x7f, 0x01, 0x00 }, SocketFlags.None);
        using var stream = new NetworkStream(socket);
        var pdu = await LdapPduReader.ReadAsync(stream, 4096, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var notice = LdapCodec.Decode(pdu!);
        Assert.Equal(0, notice.MessageId);
        var response = Assert.IsType<ExtendedResponse>(notice.Op);
        Assert.Equal(LdapOids.NoticeOfDisconnection, response.Name);
        Assert.Equal(LdapResultCode.ProtocolError, response.Result.Code);
        Assert.Null(await LdapPduReader.ReadAsync(stream, 4096, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
    }

    [Fact]
    public async Task Several_searches_run_at_once_and_an_abandoned_one_says_nothing_more()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("127.0.0.1", Port);
        using var stream = new NetworkStream(socket);
        async Task Send(LdapMessage m) => await stream.WriteAsync(LdapCodec.Encode(m));

        await Send(new LdapMessage(1, new BindRequest(3, "starface", Encoding.UTF8.GetBytes(LdapTestData.StarfacePassword))));
        var bound = LdapCodec.Decode((await LdapPduReader.ReadAsync(stream, 1 << 20, CancellationToken.None))!);
        Assert.Equal(LdapResultCode.Success, Assert.IsType<BindResponse>(bound.Op).Result.Code);

        var filter = LdapFilter.Parse("(objectClass=person)");
        await Send(new LdapMessage(2, new SearchRequest(LdapTestData.Base, LdapScope.WholeSubtree, LdapDerefAliases.Never, 0, 0, false, filter, ["cn"])));
        await Send(new LdapMessage(3, new SearchRequest(LdapTestData.Base, LdapScope.WholeSubtree, LdapDerefAliases.Never, 0, 0, false, LdapFilter.Parse("(cn=Ann*)"), ["1.1"])));
        await Send(new LdapMessage(4, new AbandonRequest(99)));
        await Send(new LdapMessage(5, new ExtendedRequest(LdapOids.WhoAmI, null)));

        var done = new Dictionary<int, LdapResultCode>();
        var entries = new Dictionary<int, int>();
        string? whoAmI = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (done.Count < 2 || whoAmI is null)
        {
            var m = LdapCodec.Decode((await LdapPduReader.ReadAsync(stream, 1 << 20, timeout.Token))!);
            switch (m.Op)
            {
                case SearchResultEntry e:
                    entries[m.MessageId] = entries.GetValueOrDefault(m.MessageId) + 1;
                    if (m.MessageId == 3)
                    {
                        Assert.Empty(e.Attributes);
                    }

                    break;
                case SearchResultDone d:
                    done[m.MessageId] = d.Result.Code;
                    break;
                case ExtendedResponse x:
                    whoAmI = Encoding.UTF8.GetString(x.Value ?? []);
                    break;
            }
        }

        Assert.Equal(33, entries[2]);
        Assert.Equal(1, entries[3]);
        Assert.Equal($"dn:cn=starface,{LdapTestData.Base}", whoAmI);
        await Send(new LdapMessage(6, new UnbindRequest()));
        Assert.Null(await LdapPduReader.ReadAsync(stream, 1 << 20, timeout.Token));
    }

    [Fact]
    public async Task Too_many_failed_binds_from_one_address_are_refused_even_with_the_right_password()
    {
        var ctx = new TestLdapContext(LdapTestData.Directory(), new LdapServerOptions
        {
            Port = 0, SecurePort = 0, Address = System.Net.IPAddress.Loopback, BindFailureDelay = TimeSpan.Zero, BindFailureLimit = 3,
        });
        var server = new LdapServer(ctx);
        server.Apply(true, false);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                using var bad = new LdapClient();
                await bad.ConnectAsync("127.0.0.1", server.PlainPort!.Value);
                await Assert.ThrowsAsync<LdapException>(() => bad.BindAsync("starface", "wrong"));
            }

            using var good = new LdapClient();
            await good.ConnectAsync("127.0.0.1", server.PlainPort!.Value);
            var e = await Assert.ThrowsAsync<LdapException>(() => good.BindAsync("starface", LdapTestData.StarfacePassword));
            Assert.Equal(LdapException.InvalidCredentials, e.ResultCode);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Failed_binds_at_the_same_time_on_many_connections_do_not_pass_the_limit()
    {
        var ctx = new TestLdapContext(LdapTestData.Directory(), Limits(o => o with { BindFailureLimit = 3, BindFailureDelay = TimeSpan.FromMilliseconds(200) }));
        var server = new LdapServer(ctx);
        server.Apply(true, false);
        try
        {
            var clients = new List<LdapClient>();
            for (var i = 0; i < 12; i++)
            {
                var client = new LdapClient();
                await client.ConnectAsync("127.0.0.1", server.PlainPort!.Value);
                clients.Add(client);
            }

            await Task.WhenAll(clients.Select(c => Assert.ThrowsAsync<LdapException>(() => c.BindAsync("starface", "wrong"))));
            clients.ForEach(c => c.Dispose());

            // only the reserved attempts had their password checked and counted
            Assert.Equal(3, ctx.Failures.Count);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task A_search_without_a_free_slot_of_the_server_is_answered_busy()
    {
        var ctx = new TestLdapContext(LdapTestData.Directory(),
            Limits(o => o with { MaxConcurrentSearches = 1, SearchQueueTimeout = TimeSpan.FromMilliseconds(200) }));
        var server = new LdapServer(ctx);
        server.Apply(true, false);
        try
        {
            using var client = new LdapClient();
            await client.ConnectAsync("127.0.0.1", server.PlainPort!.Value);
            await client.BindAsync("starface", LdapTestData.StarfacePassword);

            // another search holds the only slot
            await ctx.Searches.WaitAsync();
            var e = await Assert.ThrowsAsync<LdapException>(() => SearchAsync(client, "(cn=Ann*)", attributes: ["cn"]));
            Assert.Equal(LdapException.Busy, e.ResultCode);

            ctx.Searches.Release();
            Assert.Single(await SearchAsync(client, "(cn=Ann*)", attributes: ["cn"]));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>A server of its own with other limits; the test talks to its plain port.</summary>
    private static async Task WithServerAsync(LdapServerOptions options, Func<int, Task> test)
    {
        var server = new LdapServer(new TestLdapContext(LdapTestData.Directory(), options));
        server.Apply(true, false);
        try
        {
            await test(server.PlainPort!.Value);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static LdapServerOptions Limits(Func<LdapServerOptions, LdapServerOptions> change)
        => change(new LdapServerOptions { Port = 0, SecurePort = 0, Address = System.Net.IPAddress.Loopback, BindFailureDelay = TimeSpan.Zero });

    private static async Task<ExtendedResponse> NoticeAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var message = LdapCodec.Decode((await LdapPduReader.ReadAsync(stream, 1 << 20, timeout.Token))!);
        Assert.Equal(0, message.MessageId);
        var notice = Assert.IsType<ExtendedResponse>(message.Op);
        Assert.Equal(LdapOids.NoticeOfDisconnection, notice.Name);
        Assert.Null(await LdapPduReader.ReadAsync(stream, 1 << 20, timeout.Token));
        return notice;
    }

    [Fact]
    public async Task An_idle_connection_is_closed_with_a_notice()
        => await WithServerAsync(Limits(o => o with { IdleTimeout = TimeSpan.FromMilliseconds(300) }), async port =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync("127.0.0.1", port);
            using var stream = new NetworkStream(socket);
            Assert.Equal(LdapResultCode.Unavailable, (await NoticeAsync(stream)).Result.Code);
        });

    [Fact]
    public async Task A_message_larger_than_the_limit_is_refused()
        => await WithServerAsync(Limits(o => o with { MaxPduBytes = 1024 }), async port =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync("127.0.0.1", port);
            using var stream = new NetworkStream(socket);
            var big = new BindRequest(3, new string('x', 2000), Encoding.UTF8.GetBytes("pw"));
            await stream.WriteAsync(LdapCodec.Encode(new LdapMessage(1, big)));
            Assert.Equal(LdapResultCode.ProtocolError, (await NoticeAsync(stream)).Result.Code);
        });

    [Fact]
    public async Task A_message_that_never_ends_runs_into_the_read_timeout()
        => await WithServerAsync(Limits(o => o with { ReadTimeout = TimeSpan.FromMilliseconds(300) }), async port =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync("127.0.0.1", port);
            using var stream = new NetworkStream(socket);
            await stream.WriteAsync(new byte[] { 0x30, 0x20, 0x02, 0x01 });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Null(await LdapPduReader.ReadAsync(stream, 1 << 20, timeout.Token));
        });

    [Fact]
    public async Task Connections_over_the_limit_of_an_address_are_refused_busy()
        => await WithServerAsync(Limits(o => o with { MaxConnectionsPerAddress = 2 }), async port =>
        {
            using var first = new LdapClient();
            await first.ConnectAsync("127.0.0.1", port);
            using var second = new LdapClient();
            await second.ConnectAsync("127.0.0.1", port);
            await second.BindAsync("starface", LdapTestData.StarfacePassword);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync("127.0.0.1", port);
            using var stream = new NetworkStream(socket);
            Assert.Equal(LdapResultCode.Busy, (await NoticeAsync(stream)).Result.Code);

            // the two connections still work
            var results = await second.SearchAsync(LdapTestData.Base, LdapClient.ScopeSub, "(cn=Ann*)", null, false);
            Assert.Single(await AllAsync(results));
        });

    [Fact]
    public async Task The_query_log_has_the_searches()
    {
        using var client = await ConnectAsync();
        await SearchAsync(client, "(cn=Ann*)");
        var entry = _ctx.Log.Snapshot().First(e => e.Operation == "search");
        Assert.Equal("(cn=Ann*)", entry.Filter);
        Assert.Equal(1, entry.Results);
        Assert.Equal("starface", entry.BindName);
        Assert.Equal("127.0.0.1", entry.Client);
        Assert.Contains(_ctx.Log.Snapshot(), e => e.Operation == "bind" && e.Code == LdapResultCode.Success);
    }
}
