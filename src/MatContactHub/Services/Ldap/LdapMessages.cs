namespace MatContactHub.Services.Ldap;

/// <summary>The result codes of LDAPv3 (RFC 4511, appendix A) that the server answers.</summary>
public enum LdapResultCode
{
    Success = 0,
    OperationsError = 1,
    ProtocolError = 2,
    TimeLimitExceeded = 3,
    SizeLimitExceeded = 4,
    CompareFalse = 5,
    CompareTrue = 6,
    AuthMethodNotSupported = 7,
    StrongerAuthRequired = 8,
    UnavailableCriticalExtension = 12,
    ConfidentialityRequired = 13,
    NoSuchAttribute = 16,
    InappropriateMatching = 18,
    NoSuchObject = 32,
    InvalidDnSyntax = 34,
    InappropriateAuthentication = 48,
    InvalidCredentials = 49,
    InsufficientAccessRights = 50,
    Busy = 51,
    Unavailable = 52,
    UnwillingToPerform = 53,
    Other = 80,
}

/// <summary>The protocol operations: the number of the APPLICATION tag of each (RFC 4511, 4.2 to 4.14).</summary>
public enum LdapOperation
{
    BindRequest = 0,
    BindResponse = 1,
    UnbindRequest = 2,
    SearchRequest = 3,
    SearchResultEntry = 4,
    SearchResultDone = 5,
    ModifyRequest = 6,
    ModifyResponse = 7,
    AddRequest = 8,
    AddResponse = 9,
    DelRequest = 10,
    DelResponse = 11,
    ModifyDnRequest = 12,
    ModifyDnResponse = 13,
    CompareRequest = 14,
    CompareResponse = 15,
    AbandonRequest = 16,
    SearchResultReference = 19,
    ExtendedRequest = 23,
    ExtendedResponse = 24,
    IntermediateResponse = 25,
}

public enum LdapScope
{
    BaseObject = 0,
    SingleLevel = 1,
    WholeSubtree = 2,
}

public enum LdapDerefAliases
{
    Never = 0,
    InSearching = 1,
    FindingBaseObject = 2,
    Always = 3,
}

/// <summary>The object identifiers the server knows.</summary>
public static class LdapOids
{
    /// <summary>The simple paged results control (RFC 2696).</summary>
    public const string PagedResults = "1.2.840.113556.1.4.319";

    /// <summary>ManageDsaIT (RFC 3296): there are no referrals here, so it changes nothing and is accepted.</summary>
    public const string ManageDsaIt = "2.16.840.1.113730.3.4.2";

    /// <summary>The extended operation StartTLS (RFC 4511, 4.14).</summary>
    public const string StartTls = "1.3.6.1.4.1.1466.20037";

    /// <summary>The extended operation "Who am I?" (RFC 4532).</summary>
    public const string WhoAmI = "1.3.6.1.4.1.4203.1.11.3";

    /// <summary>The unsolicited notice of disconnection (RFC 4511, 4.4.1).</summary>
    public const string NoticeOfDisconnection = "1.3.6.1.4.1.1466.20036";

    /// <summary>The feature "all operational attributes" ("+", RFC 3673).</summary>
    public const string AllOperationalAttributes = "1.3.6.1.4.1.4203.1.5.1";
}

/// <summary>A control of a message (RFC 4511, 4.1.11).</summary>
public sealed record LdapControl(string Oid, bool Critical, byte[]? Value);

/// <summary>The answer of an operation: a code, the DN that matched as far as it exists, a message for people.</summary>
public sealed record LdapResult(LdapResultCode Code, string MatchedDn = "", string Message = "")
{
    public static readonly LdapResult Ok = new(LdapResultCode.Success);
}

/// <summary>An attribute of an entry with its values (strings: every attribute of this directory is text).</summary>
public sealed record LdapAttribute(string Type, IReadOnlyList<string> Values);

/// <summary>One protocol operation (a request or a response).</summary>
public abstract record LdapOp
{
    public abstract LdapOperation Operation { get; }
}

/// <summary>A simple bind (<see cref="Password"/>) or a SASL bind (<see cref="SaslMechanism"/>, which the server refuses).</summary>
public sealed record BindRequest(int Version, string Name, byte[] Password, string? SaslMechanism = null) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.BindRequest;
}

public sealed record UnbindRequest : LdapOp
{
    public override LdapOperation Operation => LdapOperation.UnbindRequest;
}

public sealed record SearchRequest(string BaseDn, LdapScope Scope, LdapDerefAliases Deref, int SizeLimit, int TimeLimit, bool TypesOnly, LdapFilter Filter,
    IReadOnlyList<string> Attributes) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.SearchRequest;
}

public sealed record AbandonRequest(int MessageId) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.AbandonRequest;
}

public sealed record ExtendedRequest(string Name, byte[]? Value) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.ExtendedRequest;
}

/// <summary>Modify, Add, Delete, ModifyDN or Compare: the directory is read-only, the server answers unwillingToPerform (only the tag is read).</summary>
public sealed record WriteRequest(LdapOperation Request) : LdapOp
{
    public override LdapOperation Operation => Request;

    /// <summary>The response that belongs to the request (ModifyRequest → ModifyResponse …).</summary>
    public LdapOperation Response => Request + 1;
}

public sealed record BindResponse(LdapResult Result) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.BindResponse;
}

public sealed record SearchResultEntry(string Dn, IReadOnlyList<LdapAttribute> Attributes) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.SearchResultEntry;
}

public sealed record SearchResultDone(LdapResult Result) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.SearchResultDone;
}

public sealed record ExtendedResponse(LdapResult Result, string? Name = null, byte[]? Value = null) : LdapOp
{
    public override LdapOperation Operation => LdapOperation.ExtendedResponse;
}

/// <summary>The answer to a <see cref="WriteRequest"/> (an LDAPResult under the tag of the response).</summary>
public sealed record WriteResponse(LdapOperation Response, LdapResult Result) : LdapOp
{
    public override LdapOperation Operation => Response;
}

/// <summary>An LDAP message: the id, the operation, the controls.</summary>
public sealed record LdapMessage(int MessageId, LdapOp Op, IReadOnlyList<LdapControl> Controls)
{
    public LdapMessage(int messageId, LdapOp op) : this(messageId, op, [])
    {
    }
}

/// <summary>A message that is not valid LDAP (the server answers with a notice of disconnection, protocolError, and closes).</summary>
public sealed class LdapProtocolException(string message, Exception? inner = null) : Exception(message, inner);
