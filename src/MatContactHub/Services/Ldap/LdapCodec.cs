using System.Formats.Asn1;
using System.Numerics;
using System.Text;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// The BER encoding of LDAPv3 (RFC 4511, section 5: definite lengths; <c>System.Formats.Asn1</c>). <see cref="Decode"/> reads one complete message (the frame is read
/// by <see cref="LdapPduReader"/>) and throws nothing but <see cref="LdapProtocolException"/> for whatever bytes it gets; <see cref="Encode"/> writes one.
/// Both directions of every operation the server knows are here (requests and responses), so a message can be checked by a round trip.
/// </summary>
public static class LdapCodec
{
    private static readonly Asn1Tag ControlsTag = new(TagClass.ContextSpecific, 0, true);

    private static Asn1Tag App(LdapOperation op, bool constructed = true) => new(TagClass.Application, (int)op, constructed);

    private static Asn1Tag Context(int number, bool constructed = false) => new(TagClass.ContextSpecific, number, constructed);

    // ---------------------------------------------------------------- decode

    /// <summary>Reads one LDAP message. Throws <see cref="LdapProtocolException"/> when the bytes are not one.</summary>
    public static LdapMessage Decode(ReadOnlyMemory<byte> pdu)
    {
        try
        {
            var outer = new AsnReader(pdu, AsnEncodingRules.BER);
            var message = outer.ReadSequence();
            outer.ThrowIfNotEmpty();
            if (!message.TryReadInt32(out var id) || id < 0)
            {
                throw new LdapProtocolException("The message ID is not an integer from 0 to 2147483647.");
            }

            var op = ReadOp(message);
            var controls = new List<LdapControl>();
            if (message.HasData && message.PeekTag().HasSameClassAndValue(ControlsTag))
            {
                var list = message.ReadSequence(ControlsTag);
                while (list.HasData)
                {
                    var c = list.ReadSequence();
                    var oid = Utf8(c.ReadOctetString());
                    var critical = false;
                    if (c.HasData && c.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
                    {
                        critical = c.ReadBoolean();
                    }

                    byte[]? value = c.HasData ? c.ReadOctetString() : null;
                    c.ThrowIfNotEmpty();
                    controls.Add(new LdapControl(oid, critical, value));
                }
            }

            // what may follow in a later version of the protocol is ignored (RFC 4511, 4: extensibility)
            return new LdapMessage(id, op, controls);
        }
        catch (LdapProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is AsnContentException or InvalidOperationException or ArgumentException or OverflowException or FormatException
                                       or DecoderFallbackException or InvalidCastException)
        {
            throw new LdapProtocolException("The message is not valid BER: " + ex.Message, ex);
        }
    }

    private static LdapOp ReadOp(AsnReader message)
    {
        var tag = message.PeekTag();
        if (tag.TagClass != TagClass.Application)
        {
            throw new LdapProtocolException("The operation is not an APPLICATION tag.");
        }

        var op = (LdapOperation)tag.TagValue;
        switch (op)
        {
            case LdapOperation.BindRequest:
            {
                var r = message.ReadSequence(App(op));
                var version = ReadInt(r);
                var name = Utf8(r.ReadOctetString());
                var auth = r.PeekTag();
                if (auth.HasSameClassAndValue(Context(0)))
                {
                    var password = r.ReadOctetString(Context(0));
                    return new BindRequest(version, name, password);
                }

                if (auth.HasSameClassAndValue(Context(3, true)))
                {
                    var sasl = r.ReadSequence(Context(3, true));
                    var mechanism = Utf8(sasl.ReadOctetString());
                    return new BindRequest(version, name, [], mechanism.Length == 0 ? "?" : mechanism);
                }

                throw new LdapProtocolException("Unknown authentication choice.");
            }

            case LdapOperation.UnbindRequest:
                message.ReadNull(App(op, false));
                return new UnbindRequest();

            case LdapOperation.SearchRequest:
            {
                var r = message.ReadSequence(App(op));
                var baseDn = Utf8(r.ReadOctetString());
                var scope = ReadEnum<LdapScope>(r, 2);
                var deref = ReadEnum<LdapDerefAliases>(r, 3);
                var sizeLimit = ReadInt(r);
                var timeLimit = ReadInt(r);
                var typesOnly = r.ReadBoolean();
                var nodes = 0;
                var filter = ReadFilter(r, 0, ref nodes);
                var attributes = new List<string>();
                var list = r.ReadSequence();
                while (list.HasData)
                {
                    attributes.Add(Utf8(list.ReadOctetString()));
                }

                if (sizeLimit < 0 || timeLimit < 0)
                {
                    throw new LdapProtocolException("A limit is negative.");
                }

                return new SearchRequest(baseDn, scope, deref, sizeLimit, timeLimit, typesOnly, filter, attributes);
            }

            case LdapOperation.AbandonRequest:
            {
                if (!message.TryReadInt32(out var id, App(op, false)) || id < 0)
                {
                    throw new LdapProtocolException("The abandoned message ID is not valid.");
                }

                return new AbandonRequest(id);
            }

            case LdapOperation.ExtendedRequest:
            {
                var r = message.ReadSequence(App(op));
                var name = Utf8(r.ReadOctetString(Context(0)));
                byte[]? value = r.HasData && r.PeekTag().HasSameClassAndValue(Context(1)) ? r.ReadOctetString(Context(1)) : null;
                return new ExtendedRequest(name, value);
            }

            case LdapOperation.ModifyRequest or LdapOperation.AddRequest or LdapOperation.DelRequest or LdapOperation.ModifyDnRequest or LdapOperation.CompareRequest:
                // the directory is read-only: the content is skipped, the tag tells which response is due
                message.ReadEncodedValue();
                return new WriteRequest(op);

            // the responses (a client reads them; the tests decode what the server wrote)
            case LdapOperation.BindResponse:
            {
                var r = message.ReadSequence(App(op));
                return new BindResponse(ReadResult(r));
            }

            case LdapOperation.SearchResultDone:
            {
                var r = message.ReadSequence(App(op));
                return new SearchResultDone(ReadResult(r));
            }

            case LdapOperation.SearchResultEntry:
            {
                var r = message.ReadSequence(App(op));
                var dn = Utf8(r.ReadOctetString());
                var attributes = new List<LdapAttribute>();
                var list = r.ReadSequence();
                while (list.HasData)
                {
                    var a = list.ReadSequence();
                    var type = Utf8(a.ReadOctetString());
                    var set = a.ReadSetOf(skipSortOrderValidation: true);
                    var values = new List<string>();
                    while (set.HasData)
                    {
                        values.Add(Utf8(set.ReadOctetString()));
                    }

                    attributes.Add(new LdapAttribute(type, values));
                }

                return new SearchResultEntry(dn, attributes);
            }

            case LdapOperation.ExtendedResponse:
            {
                var r = message.ReadSequence(App(op));
                var result = ReadResult(r);
                string? name = null;
                byte[]? value = null;
                if (r.HasData && r.PeekTag().HasSameClassAndValue(Context(10)))
                {
                    name = Utf8(r.ReadOctetString(Context(10)));
                }

                if (r.HasData && r.PeekTag().HasSameClassAndValue(Context(11)))
                {
                    value = r.ReadOctetString(Context(11));
                }

                return new ExtendedResponse(result, name, value);
            }

            case LdapOperation.ModifyResponse or LdapOperation.AddResponse or LdapOperation.DelResponse or LdapOperation.ModifyDnResponse or LdapOperation.CompareResponse:
            {
                var r = message.ReadSequence(App(op));
                return new WriteResponse(op, ReadResult(r));
            }

            default:
                throw new LdapProtocolException($"Unknown operation {tag.TagValue}.");
        }
    }

    private static LdapResult ReadResult(AsnReader r)
    {
        var code = (LdapResultCode)ReadEnumValue(r);
        var matched = Utf8(r.ReadOctetString());
        var message = Utf8(r.ReadOctetString());
        return new LdapResult(code, matched, message);
    }

    /// <summary>Reads a filter (RFC 4511, 4.5.1.7), with a limit on the depth and the number of parts (no stack overflow from a hostile message).</summary>
    private static LdapFilter ReadFilter(AsnReader r, int depth, ref int nodes)
    {
        if (depth > LdapFilter.MaxDepth || ++nodes > LdapFilter.MaxNodes)
        {
            throw new LdapProtocolException("The filter is nested too deeply or has too many parts.");
        }

        var tag = r.PeekTag();
        if (tag.TagClass != TagClass.ContextSpecific)
        {
            throw new LdapProtocolException("A filter must be a context-specific tag.");
        }

        switch (tag.TagValue)
        {
            case 0:
            case 1:
            {
                var set = r.ReadSetOf(skipSortOrderValidation: true, expectedTag: Context(tag.TagValue, true));
                var list = new List<LdapFilter>();
                while (set.HasData)
                {
                    list.Add(ReadFilter(set, depth + 1, ref nodes));
                }

                return tag.TagValue == 0 ? new LdapFilter.And(list) : new LdapFilter.Or(list);
            }

            case 2:
            {
                var inner = r.ReadSequence(Context(2, true));
                var filter = ReadFilter(inner, depth + 1, ref nodes);
                inner.ThrowIfNotEmpty();
                return new LdapFilter.Not(filter);
            }

            case 3:
            case 5:
            case 6:
            case 8:
            {
                var ava = r.ReadSequence(Context(tag.TagValue, true));
                var attribute = Utf8(ava.ReadOctetString());
                var value = Utf8(ava.ReadOctetString());
                return tag.TagValue switch
                {
                    3 => new LdapFilter.Equality(attribute, value),
                    5 => new LdapFilter.GreaterOrEqual(attribute, value),
                    6 => new LdapFilter.LessOrEqual(attribute, value),
                    _ => new LdapFilter.Approx(attribute, value),
                };
            }

            case 4:
            {
                var sub = r.ReadSequence(Context(4, true));
                var attribute = Utf8(sub.ReadOctetString());
                var parts = sub.ReadSequence();
                string? initial = null;
                string? final = null;
                var any = new List<string>();
                var count = 0;
                while (parts.HasData)
                {
                    var partTag = parts.PeekTag();
                    var value = Utf8(parts.ReadOctetString(partTag));
                    switch (partTag.TagClass == TagClass.ContextSpecific ? partTag.TagValue : -1)
                    {
                        case 0 when count == 0:
                            initial = value;
                            break;
                        case 1 when final is null:
                            any.Add(value);
                            break;
                        case 2 when final is null:
                            final = value;
                            break;
                        default:
                            throw new LdapProtocolException("The parts of a substring filter are not in order.");
                    }

                    count++;
                }

                if (count == 0)
                {
                    throw new LdapProtocolException("A substring filter needs a part.");
                }

                return new LdapFilter.Substrings(attribute, initial, any, final);
            }

            case 7:
                return new LdapFilter.Present(Utf8(r.ReadOctetString(Context(7))));

            case 9:
            {
                var m = r.ReadSequence(Context(9, true));
                string? rule = null;
                string? type = null;
                if (m.HasData && m.PeekTag().HasSameClassAndValue(Context(1)))
                {
                    rule = Utf8(m.ReadOctetString(Context(1)));
                }

                if (m.HasData && m.PeekTag().HasSameClassAndValue(Context(2)))
                {
                    type = Utf8(m.ReadOctetString(Context(2)));
                }

                var value = Utf8(m.ReadOctetString(Context(3)));
                var dn = m.HasData && m.ReadBoolean(Context(4));
                return new LdapFilter.Extensible(rule, type, value, dn);
            }

            default:
                throw new LdapProtocolException($"Unknown filter choice {tag.TagValue}.");
        }
    }

    private static int ReadInt(AsnReader r)
    {
        if (!r.TryReadInt32(out var value))
        {
            throw new LdapProtocolException("An integer is out of range.");
        }

        return value;
    }

    private static int ReadEnumValue(AsnReader r)
    {
        var bytes = r.ReadEnumeratedBytes();
        var value = new BigInteger(bytes.Span, isUnsigned: false, isBigEndian: true);
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new LdapProtocolException("An enumeration is out of range.");
        }

        return (int)value;
    }

    private static T ReadEnum<T>(AsnReader r, int max) where T : struct, Enum
    {
        var value = ReadEnumValue(r);
        if (value < 0 || value > max)
        {
            throw new LdapProtocolException($"{typeof(T).Name} {value} is not valid.");
        }

        return (T)(object)value;
    }

    /// <summary>Text of the protocol: UTF-8 (a byte sequence that is not UTF-8 becomes the replacement character, never an exception).</summary>
    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // ---------------------------------------------------------------- encode

    /// <summary>Writes one LDAP message (definite lengths).</summary>
    public static byte[] Encode(LdapMessage message)
    {
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteInteger(message.MessageId);
            WriteOp(w, message.Op);
            if (message.Controls.Count > 0)
            {
                using (w.PushSequence(ControlsTag))
                {
                    foreach (var c in message.Controls)
                    {
                        using (w.PushSequence())
                        {
                            w.WriteOctetString(Encoding.UTF8.GetBytes(c.Oid));
                            if (c.Critical)
                            {
                                w.WriteBoolean(true);
                            }

                            if (c.Value is not null)
                            {
                                w.WriteOctetString(c.Value);
                            }
                        }
                    }
                }
            }
        }

        return w.Encode();
    }

    private static void WriteOp(AsnWriter w, LdapOp op)
    {
        switch (op)
        {
            case BindRequest b:
                using (w.PushSequence(App(LdapOperation.BindRequest)))
                {
                    w.WriteInteger(b.Version);
                    WriteString(w, b.Name);
                    if (b.SaslMechanism is null)
                    {
                        w.WriteOctetString(b.Password, Context(0));
                    }
                    else
                    {
                        using (w.PushSequence(Context(3, true)))
                        {
                            WriteString(w, b.SaslMechanism);
                        }
                    }
                }

                break;
            case UnbindRequest:
                w.WriteNull(App(LdapOperation.UnbindRequest, false));
                break;
            case SearchRequest s:
                using (w.PushSequence(App(LdapOperation.SearchRequest)))
                {
                    WriteString(w, s.BaseDn);
                    w.WriteEnumeratedValue(s.Scope);
                    w.WriteEnumeratedValue(s.Deref);
                    w.WriteInteger(s.SizeLimit);
                    w.WriteInteger(s.TimeLimit);
                    w.WriteBoolean(s.TypesOnly);
                    WriteFilter(w, s.Filter);
                    using (w.PushSequence())
                    {
                        foreach (var a in s.Attributes)
                        {
                            WriteString(w, a);
                        }
                    }
                }

                break;
            case AbandonRequest a:
                w.WriteInteger(a.MessageId, App(LdapOperation.AbandonRequest, false));
                break;
            case ExtendedRequest x:
                using (w.PushSequence(App(LdapOperation.ExtendedRequest)))
                {
                    w.WriteOctetString(Encoding.UTF8.GetBytes(x.Name), Context(0));
                    if (x.Value is not null)
                    {
                        w.WriteOctetString(x.Value, Context(1));
                    }
                }

                break;
            case WriteRequest q:
                // only for tests: an empty sequence under the tag of the request (DelRequest is a primitive DN)
                if (q.Request == LdapOperation.DelRequest)
                {
                    w.WriteOctetString([], App(LdapOperation.DelRequest, false));
                }
                else
                {
                    using (w.PushSequence(App(q.Request)))
                    {
                    }
                }

                break;
            case BindResponse b:
                using (w.PushSequence(App(LdapOperation.BindResponse)))
                {
                    WriteResult(w, b.Result);
                }

                break;
            case SearchResultEntry e:
                using (w.PushSequence(App(LdapOperation.SearchResultEntry)))
                {
                    WriteString(w, e.Dn);
                    using (w.PushSequence())
                    {
                        foreach (var a in e.Attributes)
                        {
                            using (w.PushSequence())
                            {
                                WriteString(w, a.Type);
                                // a SET OF: BER keeps the order it is written in (DER would sort it)
                                using (w.PushSetOf())
                                {
                                    foreach (var v in a.Values)
                                    {
                                        WriteString(w, v);
                                    }
                                }
                            }
                        }
                    }
                }

                break;
            case SearchResultDone d:
                using (w.PushSequence(App(LdapOperation.SearchResultDone)))
                {
                    WriteResult(w, d.Result);
                }

                break;
            case ExtendedResponse x:
                using (w.PushSequence(App(LdapOperation.ExtendedResponse)))
                {
                    WriteResult(w, x.Result);
                    if (x.Name is not null)
                    {
                        w.WriteOctetString(Encoding.UTF8.GetBytes(x.Name), Context(10));
                    }

                    if (x.Value is not null)
                    {
                        w.WriteOctetString(x.Value, Context(11));
                    }
                }

                break;
            case WriteResponse r:
                using (w.PushSequence(App(r.Response)))
                {
                    WriteResult(w, r.Result);
                }

                break;
            default:
                throw new ArgumentException($"{op.GetType().Name} cannot be encoded.", nameof(op));
        }
    }

    private static void WriteResult(AsnWriter w, LdapResult result)
    {
        w.WriteEnumeratedValue(result.Code);
        WriteString(w, result.MatchedDn);
        WriteString(w, result.Message);
    }

    private static void WriteFilter(AsnWriter w, LdapFilter filter)
    {
        switch (filter)
        {
            case LdapFilter.And a:
                using (w.PushSetOf(Context(0, true)))
                {
                    a.Filters.ToList().ForEach(f => WriteFilter(w, f));
                }

                break;
            case LdapFilter.Or o:
                using (w.PushSetOf(Context(1, true)))
                {
                    o.Filters.ToList().ForEach(f => WriteFilter(w, f));
                }

                break;
            case LdapFilter.Not n:
                using (w.PushSequence(Context(2, true)))
                {
                    WriteFilter(w, n.Filter);
                }

                break;
            case LdapFilter.Equality e:
                WriteAva(w, 3, e.Attribute, e.Value);
                break;
            case LdapFilter.Substrings s:
                using (w.PushSequence(Context(4, true)))
                {
                    WriteString(w, s.Attribute);
                    using (w.PushSequence())
                    {
                        if (s.Initial is not null)
                        {
                            w.WriteOctetString(Encoding.UTF8.GetBytes(s.Initial), Context(0));
                        }

                        foreach (var any in s.Any)
                        {
                            w.WriteOctetString(Encoding.UTF8.GetBytes(any), Context(1));
                        }

                        if (s.Final is not null)
                        {
                            w.WriteOctetString(Encoding.UTF8.GetBytes(s.Final), Context(2));
                        }
                    }
                }

                break;
            case LdapFilter.GreaterOrEqual g:
                WriteAva(w, 5, g.Attribute, g.Value);
                break;
            case LdapFilter.LessOrEqual l:
                WriteAva(w, 6, l.Attribute, l.Value);
                break;
            case LdapFilter.Present p:
                w.WriteOctetString(Encoding.UTF8.GetBytes(p.Attribute), Context(7));
                break;
            case LdapFilter.Approx x:
                WriteAva(w, 8, x.Attribute, x.Value);
                break;
            case LdapFilter.Extensible m:
                using (w.PushSequence(Context(9, true)))
                {
                    if (m.MatchingRule is not null)
                    {
                        w.WriteOctetString(Encoding.UTF8.GetBytes(m.MatchingRule), Context(1));
                    }

                    if (m.Attribute is not null)
                    {
                        w.WriteOctetString(Encoding.UTF8.GetBytes(m.Attribute), Context(2));
                    }

                    w.WriteOctetString(Encoding.UTF8.GetBytes(m.Value), Context(3));
                    if (m.DnAttributes)
                    {
                        w.WriteBoolean(true, Context(4));
                    }
                }

                break;
        }
    }

    private static void WriteAva(AsnWriter w, int choice, string attribute, string value)
    {
        using (w.PushSequence(Context(choice, true)))
        {
            WriteString(w, attribute);
            WriteString(w, value);
        }
    }

    private static void WriteString(AsnWriter w, string value) => w.WriteOctetString(Encoding.UTF8.GetBytes(value));

    // ---------------------------------------------------------------- the paged results control (RFC 2696)

    /// <summary>The value of the paged results control: <c>SEQUENCE { size INTEGER, cookie OCTET STRING }</c>.</summary>
    public static (int Size, byte[] Cookie) DecodePagedResults(byte[]? value)
    {
        if (value is null)
        {
            throw new LdapProtocolException("The paged results control has no value.");
        }

        try
        {
            var outer = new AsnReader(value, AsnEncodingRules.BER);
            var seq = outer.ReadSequence();
            if (!seq.TryReadInt32(out var size) || size < 0)
            {
                throw new LdapProtocolException("The page size is not valid.");
            }

            var cookie = seq.ReadOctetString();
            return (size, cookie);
        }
        catch (Exception ex) when (ex is AsnContentException or InvalidOperationException or ArgumentException)
        {
            throw new LdapProtocolException("The paged results control is not valid BER.", ex);
        }
    }

    public static byte[] EncodePagedResults(int size, byte[] cookie)
    {
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteInteger(size);
            w.WriteOctetString(cookie);
        }

        return w.Encode();
    }
}
