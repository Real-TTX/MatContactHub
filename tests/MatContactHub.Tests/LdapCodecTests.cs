using System.Text;
using MatContactHub.Services.Ldap;

namespace MatContactHub.Tests;

/// <summary>The BER of LDAPv3: every message survives a round trip, long-form lengths are read, broken or hostile bytes never throw anything but a protocol error.</summary>
public sealed class LdapCodecTests
{
    private static LdapMessage RoundTrip(LdapMessage message) => LdapCodec.Decode(LdapCodec.Encode(message));

    [Fact]
    public void A_bind_request_survives_a_round_trip()
    {
        var back = RoundTrip(new LdapMessage(7, new BindRequest(3, "cn=starface,dc=matcontacthub,dc=local", Encoding.UTF8.GetBytes("Gehëim"))));
        Assert.Equal(7, back.MessageId);
        var bind = Assert.IsType<BindRequest>(back.Op);
        Assert.Equal(3, bind.Version);
        Assert.Equal("cn=starface,dc=matcontacthub,dc=local", bind.Name);
        Assert.Equal("Gehëim", Encoding.UTF8.GetString(bind.Password));
        Assert.Null(bind.SaslMechanism);
    }

    [Fact]
    public void A_sasl_bind_is_read_as_sasl()
    {
        var bind = Assert.IsType<BindRequest>(RoundTrip(new LdapMessage(1, new BindRequest(3, string.Empty, [], "EXTERNAL"))).Op);
        Assert.Equal("EXTERNAL", bind.SaslMechanism);
    }

    [Fact]
    public void A_search_request_with_every_kind_of_filter_survives_a_round_trip()
    {
        var filter = LdapFilter.Parse("(&(objectClass=person)(|(cn=Ann*)(sn=*ex*mp*le)(mail=*@example.com))(!(title=boss))(givenName>=a)(givenName<=z)(telephoneNumber=*)(cn~=ann)(cn:caseExactMatch:=Ann)(:dn:2.5.13.5:=x))");
        var request = new SearchRequest("ou=staff,dc=matcontacthub,dc=local", LdapScope.WholeSubtree, LdapDerefAliases.Always, 50, 10, true, filter,
            ["cn", "telephoneNumber", "+"]);
        var back = Assert.IsType<SearchRequest>(RoundTrip(new LdapMessage(2, request, [new LdapControl(LdapOids.PagedResults, true, LdapCodec.EncodePagedResults(10, [1, 2]))])).Op);
        Assert.Equal(request.BaseDn, back.BaseDn);
        Assert.Equal(LdapScope.WholeSubtree, back.Scope);
        Assert.Equal(LdapDerefAliases.Always, back.Deref);
        Assert.Equal(50, back.SizeLimit);
        Assert.Equal(10, back.TimeLimit);
        Assert.True(back.TypesOnly);
        Assert.Equal(filter.ToString(), back.Filter.ToString());
        Assert.Equal(["cn", "telephoneNumber", "+"], back.Attributes);
    }

    [Fact]
    public void Controls_survive_a_round_trip()
    {
        var back = RoundTrip(new LdapMessage(3, new SearchRequest(string.Empty, LdapScope.BaseObject, LdapDerefAliases.Never, 0, 0, false, new LdapFilter.Present("objectClass"), []),
            [new LdapControl(LdapOids.PagedResults, true, LdapCodec.EncodePagedResults(25, [9, 8, 7])), new LdapControl("1.2.3.4", false, null)]));
        Assert.Equal(2, back.Controls.Count);
        Assert.True(back.Controls[0].Critical);
        var (size, cookie) = LdapCodec.DecodePagedResults(back.Controls[0].Value);
        Assert.Equal(25, size);
        Assert.Equal([9, 8, 7], cookie);
        Assert.Equal("1.2.3.4", back.Controls[1].Oid);
        Assert.False(back.Controls[1].Critical);
        Assert.Null(back.Controls[1].Value);
    }

    [Fact]
    public void Responses_survive_a_round_trip()
    {
        var entry = Assert.IsType<SearchResultEntry>(RoundTrip(new LdapMessage(4, new SearchResultEntry("uid=1,ou=a,dc=b",
            [new LdapAttribute("cn", ["Ann Example"]), new LdapAttribute("mail", ["a@example.com", "b@example.com"]), new LdapAttribute("sn", [])]))).Op);
        Assert.Equal("uid=1,ou=a,dc=b", entry.Dn);
        Assert.Equal(["a@example.com", "b@example.com"], entry.Attributes[1].Values);
        Assert.Empty(entry.Attributes[2].Values);

        var done = Assert.IsType<SearchResultDone>(RoundTrip(new LdapMessage(4, new SearchResultDone(new LdapResult(LdapResultCode.SizeLimitExceeded, "dc=b", "more")))).Op);
        Assert.Equal(LdapResultCode.SizeLimitExceeded, done.Result.Code);
        Assert.Equal("dc=b", done.Result.MatchedDn);
        Assert.Equal("more", done.Result.Message);

        var bind = Assert.IsType<BindResponse>(RoundTrip(new LdapMessage(1, new BindResponse(new LdapResult(LdapResultCode.InvalidCredentials)))).Op);
        Assert.Equal(LdapResultCode.InvalidCredentials, bind.Result.Code);

        var ext = Assert.IsType<ExtendedResponse>(RoundTrip(new LdapMessage(0, new ExtendedResponse(LdapResult.Ok, LdapOids.StartTls, [1]))).Op);
        Assert.Equal(LdapOids.StartTls, ext.Name);
        Assert.Equal([1], ext.Value);

        var write = Assert.IsType<WriteResponse>(RoundTrip(new LdapMessage(9, new WriteResponse(LdapOperation.ModifyResponse, new LdapResult(LdapResultCode.UnwillingToPerform)))).Op);
        Assert.Equal(LdapOperation.ModifyResponse, write.Response);
    }

    [Theory]
    [InlineData(LdapOperation.ModifyRequest)]
    [InlineData(LdapOperation.AddRequest)]
    [InlineData(LdapOperation.DelRequest)]
    [InlineData(LdapOperation.ModifyDnRequest)]
    [InlineData(LdapOperation.CompareRequest)]
    public void Write_requests_are_recognised_by_their_tag(LdapOperation op)
    {
        var write = Assert.IsType<WriteRequest>(RoundTrip(new LdapMessage(5, new WriteRequest(op))).Op);
        Assert.Equal(op + 1, write.Response);
    }

    [Fact]
    public void Unbind_abandon_and_extended_requests_survive_a_round_trip()
    {
        Assert.IsType<UnbindRequest>(RoundTrip(new LdapMessage(9, new UnbindRequest())).Op);
        Assert.Equal(42, Assert.IsType<AbandonRequest>(RoundTrip(new LdapMessage(10, new AbandonRequest(42))).Op).MessageId);
        var x = Assert.IsType<ExtendedRequest>(RoundTrip(new LdapMessage(11, new ExtendedRequest(LdapOids.StartTls, null))).Op);
        Assert.Equal(LdapOids.StartTls, x.Name);
        Assert.Null(x.Value);
    }

    [Fact]
    public void Long_form_lengths_are_read()
    {
        // a bind request as some clients write it: every length in four bytes (0x84)
        var name = Encoding.UTF8.GetBytes("starface");
        var password = Encoding.UTF8.GetBytes("pw");
        byte[] L(int n) => [0x84, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];
        var version = new byte[] { 0x02, 0x01, 0x03 };
        var nameTlv = new byte[] { 0x04 }.Concat(L(name.Length)).Concat(name).ToArray();
        var pwTlv = new byte[] { 0x80 }.Concat(L(password.Length)).Concat(password).ToArray();
        var bindBody = version.Concat(nameTlv).Concat(pwTlv).ToArray();
        var bind = new byte[] { 0x60 }.Concat(L(bindBody.Length)).Concat(bindBody).ToArray();
        var id = new byte[] { 0x02, 0x01, 0x05 };
        var body = id.Concat(bind).ToArray();
        var pdu = new byte[] { 0x30 }.Concat(L(body.Length)).Concat(body).ToArray();

        var message = LdapCodec.Decode(pdu);
        Assert.Equal(5, message.MessageId);
        Assert.Equal("starface", Assert.IsType<BindRequest>(message.Op).Name);
    }

    [Fact]
    public async Task The_frame_reader_takes_long_form_lengths_and_refuses_indefinite_and_oversized_ones()
    {
        var pdu = LdapCodec.Encode(new LdapMessage(1, new UnbindRequest()));
        var longForm = new byte[] { 0x30, 0x82, 0x00, (byte)(pdu.Length - 2) }.Concat(pdu.Skip(2)).ToArray();
        Assert.Equal(longForm, await LdapPduReader.ReadAsync(new MemoryStream(longForm), 1024, CancellationToken.None));

        var indefinite = new byte[] { 0x30, 0x80, 0x02, 0x01, 0x01, 0x42, 0x00, 0x00, 0x00 };
        var e1 = await Assert.ThrowsAsync<LdapFrameException>(() => LdapPduReader.ReadAsync(new MemoryStream(indefinite), 1024, CancellationToken.None));
        Assert.Equal(LdapFrameError.BadLength, e1.Error);

        var huge = new byte[] { 0x30, 0x84, 0x7f, 0xff, 0xff, 0xff };
        var e2 = await Assert.ThrowsAsync<LdapFrameException>(() => LdapPduReader.ReadAsync(new MemoryStream(huge), 1024, CancellationToken.None));
        Assert.Equal(LdapFrameError.TooLarge, e2.Error);

        var notLdap = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
        var e3 = await Assert.ThrowsAsync<LdapFrameException>(() => LdapPduReader.ReadAsync(new MemoryStream(notLdap), 1024, CancellationToken.None));
        Assert.Equal(LdapFrameError.NotASequence, e3.Error);

        Assert.Null(await LdapPduReader.ReadAsync(new MemoryStream([]), 1024, CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => LdapPduReader.ReadAsync(new MemoryStream(pdu[..^1]), 1024, CancellationToken.None));
    }

    [Fact]
    public void A_filter_nested_too_deeply_is_a_protocol_error()
    {
        LdapFilter filter = new LdapFilter.Present("cn");
        for (var i = 0; i < 100; i++)
        {
            filter = new LdapFilter.Not(filter);
        }

        var pdu = LdapCodec.Encode(new LdapMessage(1, new SearchRequest(LdapTestData.Base, LdapScope.WholeSubtree, LdapDerefAliases.Never, 0, 0, false, filter, [])));
        Assert.Throws<LdapProtocolException>(() => LdapCodec.Decode(pdu));
    }

    [Fact]
    public void Malformed_messages_are_protocol_errors_and_nothing_else()
    {
        List<byte[]> samples =
        [
            [],
            [0x30],
            [0x30, 0x00],
            [0x30, 0x03, 0x02, 0x01],
            [0x30, 0x05, 0x02, 0x01, 0x01, 0x7f, 0x00],
            [0x30, 0x06, 0x02, 0x01, 0x00, 0x42, 0x01, 0x00],
            [0x30, 0x0a, 0x02, 0x09, 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
            [0x04, 0x00],
        ];

        // every valid message cut at every byte, and with every byte flipped
        var valid = new[]
        {
            LdapCodec.Encode(new LdapMessage(1, new BindRequest(3, "cn=a,dc=b", [1, 2, 3]))),
            LdapCodec.Encode(new LdapMessage(2, new SearchRequest(LdapTestData.Base, LdapScope.WholeSubtree, LdapDerefAliases.Never, 10, 5, false,
                LdapFilter.Parse("(&(|(cn=a*b*c)(sn>=x))(!(mail=*))(o~=y)(cn:1.2:=z))"), ["cn", "1.1"]),
                [new LdapControl(LdapOids.PagedResults, true, LdapCodec.EncodePagedResults(5, [1]))])),
        };
        foreach (var pdu in valid)
        {
            for (var cut = 0; cut < pdu.Length; cut++)
            {
                samples.Add(pdu[..cut]);
            }

            for (var i = 0; i < pdu.Length; i++)
            {
                foreach (var flip in new byte[] { 0x01, 0x80, 0xff, 0x20 })
                {
                    var copy = (byte[])pdu.Clone();
                    copy[i] ^= flip;
                    samples.Add(copy);
                }
            }
        }

        var random = new Random(4511);
        for (var i = 0; i < 5000; i++)
        {
            var bytes = new byte[random.Next(1, 64)];
            random.NextBytes(bytes);
            bytes[0] = 0x30;
            samples.Add(bytes);
        }

        foreach (var sample in samples)
        {
            try
            {
                LdapCodec.Decode(sample);
            }
            catch (LdapProtocolException)
            {
            }
        }
    }
}
