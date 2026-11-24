using System.Net;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// The limits and the ports of the LDAP server process (the section <c>Ldap</c> of the configuration; nothing of it is set in the compose files: the defaults are the
/// container's). <c>Ldap__Port=7391</c> moves the plain port while the app runs outside a container, <c>0</c> lets the system choose a free one (the tests).
/// What an administrator changes in the interface (on or off, StartTLS, LDAPS, base DN …) is <see cref="LdapSettings"/>, not this.
/// </summary>
public sealed record LdapServerOptions
{
    /// <summary>LDAP and StartTLS (10389 in the container).</summary>
    public int Port { get; init; } = LdapSettings.Port;

    /// <summary>LDAPS (10636 in the container).</summary>
    public int SecurePort { get; init; } = LdapSettings.SecurePort;

    /// <summary>The address the listeners bind to: every address (IPv6 and IPv4) by default.</summary>
    public IPAddress Address { get; init; } = IPAddress.IPv6Any;

    /// <summary>The most connections at the same time (all clients).</summary>
    public int MaxConnections { get; init; } = 500;

    /// <summary>The most connections at the same time from one address.</summary>
    public int MaxConnectionsPerAddress { get; init; } = 64;

    /// <summary>The largest request (one LDAP message) in bytes: a search of a telephone is a few hundred bytes.</summary>
    public int MaxPduBytes { get; init; } = 256 * 1024;

    /// <summary>A connection without an operation for this long is closed (with a notice of disconnection).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A request that has started must arrive completely within this time.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An answer the client does not take within this time ends the connection.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The TLS handshake of LDAPS and StartTLS.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The longest a search may take (the client may ask for less); a search of the in-memory directory takes milliseconds.</summary>
    public TimeSpan MaxTimeLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The most operations one connection may have running at the same time.</summary>
    public int MaxOperationsPerConnection { get; init; } = 16;

    /// <summary>
    /// The most searches the whole server runs at the same time (they work on the thread pool the web pages use too); the others wait for a slot up to
    /// <see cref="SearchQueueTimeout"/> and are then answered busy.
    /// </summary>
    public int MaxConcurrentSearches { get; init; } = Math.Max(2, Environment.ProcessorCount * 2);

    /// <summary>How long a search waits for a slot of <see cref="MaxConcurrentSearches"/> before it is answered busy.</summary>
    public TimeSpan SearchQueueTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The most open paged searches (cookies) per connection; the oldest is dropped.</summary>
    public int MaxPagedSearchesPerConnection { get; init; } = 8;

    /// <summary>Failed binds from one address within <see cref="BindFailureWindow"/> before its binds are refused until the window has passed.</summary>
    public int BindFailureLimit { get; init; } = 10;

    public TimeSpan BindFailureWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The pause before the answer to a failed bind (slows down guessing).</summary>
    public TimeSpan BindFailureDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The options of the configuration (section <c>Ldap</c>), the defaults where nothing is set.</summary>
    public static LdapServerOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Ldap");
        var d = new LdapServerOptions();
        int Int(string key, int fallback, int min, int max) => int.TryParse(section[key], out var v) && v >= min && v <= max ? v : fallback;
        TimeSpan Seconds(string key, TimeSpan fallback) => double.TryParse(section[key], System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? TimeSpan.FromSeconds(v) : fallback;

        return new LdapServerOptions
        {
            Port = Int("Port", d.Port, 0, 65535),
            SecurePort = Int("SecurePort", d.SecurePort, 0, 65535),
            Address = IPAddress.TryParse(section["Address"], out var address) ? address : d.Address,
            MaxConnections = Int("MaxConnections", d.MaxConnections, 1, 100000),
            MaxConnectionsPerAddress = Int("MaxConnectionsPerAddress", d.MaxConnectionsPerAddress, 1, 100000),
            MaxPduBytes = Int("MaxPduBytes", d.MaxPduBytes, 1024, 64 * 1024 * 1024),
            IdleTimeout = Seconds("IdleTimeoutSeconds", d.IdleTimeout),
            ReadTimeout = Seconds("ReadTimeoutSeconds", d.ReadTimeout),
            WriteTimeout = Seconds("WriteTimeoutSeconds", d.WriteTimeout),
            HandshakeTimeout = Seconds("HandshakeTimeoutSeconds", d.HandshakeTimeout),
            MaxTimeLimit = Seconds("MaxTimeLimitSeconds", d.MaxTimeLimit),
            MaxOperationsPerConnection = Int("MaxOperationsPerConnection", d.MaxOperationsPerConnection, 1, 1000),
            MaxConcurrentSearches = Int("MaxConcurrentSearches", d.MaxConcurrentSearches, 1, 10000),
            SearchQueueTimeout = Seconds("SearchQueueTimeoutSeconds", d.SearchQueueTimeout),
            MaxPagedSearchesPerConnection = Int("MaxPagedSearchesPerConnection", d.MaxPagedSearchesPerConnection, 1, 1000),
            BindFailureLimit = Int("BindFailureLimit", d.BindFailureLimit, 1, 100000),
            BindFailureWindow = Seconds("BindFailureWindowSeconds", d.BindFailureWindow),
            BindFailureDelay = double.TryParse(section["BindFailureDelaySeconds"], System.Globalization.CultureInfo.InvariantCulture, out var delay) && delay >= 0
                ? TimeSpan.FromSeconds(delay) : d.BindFailureDelay,
        };
    }
}
