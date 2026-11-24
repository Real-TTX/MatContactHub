using System.Net;
using System.Security.Cryptography.X509Certificates;
using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Ldap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatContactHub.Tests;

/// <summary>A small directory for the tests of the LDAP server: two address books, a few contacts, three accounts (all books, one book, disabled).</summary>
internal static class LdapTestData
{
    public const string Base = "dc=matcontacthub,dc=local";
    public const string StarfacePassword = "starface-secret";
    public const string PhonePassword = "phone-secret-1";

    public static readonly Guid Ann = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Carla = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly PasswordService Passwords = new();

    private static readonly Lazy<string> StarfaceHash = new(() => Passwords.Hash(StarfacePassword));
    private static readonly Lazy<string> PhoneHash = new(() => Passwords.Hash(PhonePassword));
    private static readonly Lazy<X509Certificate2> SelfSigned = new(() => LdapCertificateService.CreateSelfSigned(["localhost", "127.0.0.1"]));

    public static X509Certificate2 Certificate => SelfSigned.Value;

    public static LdapSettings Settings(bool anonymous = false, int sizeLimit = 1000, NumberFormat format = NumberFormat.AsStored, bool startTls = true)
        => new(true, startTls, true, Base, anonymous, format, "DE", sizeLimit, LdapCertificateSource.SelfSigned);

    public static LdapContactRow Contact(Guid token, long book, string? first, string? last, string? company = null, string? business = null, string? mobile = null,
        string? home = null, string? mail = null, string? department = null, string? city = null, string? country = null)
        => new(token, book, string.Join(' ', new[] { first, last }.Where(x => x is not null)) is { Length: > 0 } n ? n : company ?? string.Empty, first, last, company,
            department, null, mail, null, null, business, null, mobile, home, null, null, null, null, city, null, country, null, null,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc));

    /// <summary>The directory: staff (1) with Ann, customers (2) with Bob, Carla and <paramref name="extra"/> people more.</summary>
    public static LdapDirectory Directory(LdapSettings? settings = null, int extra = 0)
    {
        var contacts = new List<LdapContactRow>
        {
            Contact(Ann, 1, "Ann", "Example", "Example GmbH", business: "+49 30 1234567", mobile: "0171 2345678", mail: "ann@example.com", department: "Sales",
                city: "Berlin", country: "DE"),
            Contact(Bob, 2, "Bob", "Builder", "Builder AG", business: "030 9876543", mail: "bob@builder.example", country: "Germany"),
            Contact(Carla, 2, "Carla", "Müller", home: "+43 1 5551234", mobile: "0049 160 1112223"),
        };
        for (var i = 0; i < extra; i++)
        {
            contacts.Add(Contact(Guid.NewGuid(), 2, "Person", $"{i:000}", business: $"+49 89 1000{i:000}"));
        }

        return LdapDirectoryBuilder.Build(1, settings ?? Settings(),
            [new LdapBookRow(1, "Staff", "staff", null), new LdapBookRow(2, "Customers", "customers", "Our customers")],
            contacts,
            [
                new LdapAccountInfo(1, "starface", StarfaceHash.Value, true, true, new HashSet<long>()),
                new LdapAccountInfo(2, "phone", PhoneHash.Value, true, false, new HashSet<long> { 2 }),
                new LdapAccountInfo(3, "disabled", StarfaceHash.Value, false, true, new HashSet<long>()),
            ]);
    }
}

/// <summary>The server around the sessions, without the app: a directory and a certificate set by the test.</summary>
internal sealed class TestLdapContext : ILdapServerContext
{
    public TestLdapContext(LdapDirectory directory, LdapServerOptions? options = null)
    {
        Directory = directory;
        Options = options ?? new LdapServerOptions { Port = 0, SecurePort = 0, Address = IPAddress.Loopback, BindFailureDelay = TimeSpan.Zero };
        Guard = new LdapBindGuard(Options.BindFailureLimit, Options.BindFailureWindow);
    }

    public LdapServerOptions Options { get; }

    public LdapDirectory Directory { get; set; }

    public X509Certificate2? Certificate { get; set; } = LdapTestData.Certificate;

    public PasswordService Passwords => LdapTestData.Passwords;

    public LdapBindGuard Guard { get; }

    public LdapQueryLog Log { get; } = new();

    public ILogger Logger => NullLogger.Instance;

    public List<string> Bounds { get; } = [];

    public List<string> Failures { get; } = [];

    public void Bound(LdapAccountInfo account, string client)
    {
        lock (Bounds)
        {
            Bounds.Add(account.Name);
        }
    }

    public void BindFailed(string client, string name, int failures)
    {
        lock (Failures)
        {
            Failures.Add(name);
        }
    }
}
