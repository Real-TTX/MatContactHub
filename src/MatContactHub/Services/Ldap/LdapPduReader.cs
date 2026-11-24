namespace MatContactHub.Services.Ldap;

/// <summary>Why a frame could not be read.</summary>
public enum LdapFrameError
{
    /// <summary>The first byte is not the tag of an LDAPMessage (a universal SEQUENCE).</summary>
    NotASequence,

    /// <summary>An indefinite length (LDAP allows definite lengths only, RFC 4511 5.1) or a length of more than four bytes.</summary>
    BadLength,

    /// <summary>Larger than the limit of the server.</summary>
    TooLarge,
}

/// <summary>A frame that cannot be read: the connection is closed with a notice of disconnection.</summary>
public sealed class LdapFrameException(LdapFrameError error, string message) : Exception(message)
{
    public LdapFrameError Error { get; } = error;
}

/// <summary>
/// Reads the frames of LDAP messages from a stream: the tag (0x30), the length (short or long form, at most four bytes, never indefinite) and exactly that many bytes.
/// It never reads past the end of a message, so the stream can change under it (StartTLS puts TLS on the same socket between two messages).
/// </summary>
public static class LdapPduReader
{
    /// <summary>The next message (its complete bytes), or null when the stream ended between two messages.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var first = new byte[1];
        if (await stream.ReadAsync(first, ct) == 0)
        {
            return null;
        }

        return await ReadRestAsync(stream, first[0], maxBytes, ct);
    }

    /// <summary>The rest of a message whose first byte was read already (the session waits for that byte as long as the connection may be idle).</summary>
    public static async Task<byte[]> ReadRestAsync(Stream stream, byte first, int maxBytes, CancellationToken ct)
    {
        if (first != 0x30)
        {
            throw new LdapFrameException(LdapFrameError.NotASequence, $"A message starts with 0x30, not 0x{first:x2}.");
        }

        var head = new byte[6];
        head[0] = first;
        await stream.ReadExactlyAsync(head.AsMemory(1, 1), ct);
        int length;
        var headLength = 2;
        if (head[1] < 0x80)
        {
            length = head[1];
        }
        else
        {
            var count = head[1] & 0x7f;
            if (count == 0)
            {
                throw new LdapFrameException(LdapFrameError.BadLength, "An indefinite length is not allowed in LDAP.");
            }

            if (count > 4)
            {
                throw new LdapFrameException(LdapFrameError.BadLength, "The length has more than four bytes.");
            }

            await stream.ReadExactlyAsync(head.AsMemory(2, count), ct);
            long value = 0;
            for (var i = 0; i < count; i++)
            {
                value = (value << 8) | head[2 + i];
            }

            headLength = 2 + count;
            if (value + headLength > maxBytes)
            {
                throw new LdapFrameException(LdapFrameError.TooLarge, $"The message has {value + headLength} bytes, the limit is {maxBytes}.");
            }

            length = (int)value;
        }

        if (length + headLength > maxBytes)
        {
            throw new LdapFrameException(LdapFrameError.TooLarge, $"The message has {length + headLength} bytes, the limit is {maxBytes}.");
        }

        var pdu = new byte[headLength + length];
        head.AsSpan(0, headLength).CopyTo(pdu);
        await stream.ReadExactlyAsync(pdu.AsMemory(headLength, length), ct);
        return pdu;
    }
}
