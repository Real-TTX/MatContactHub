using System.Security.Cryptography;
using MatContactHub.Data;
using Microsoft.AspNetCore.DataProtection;

namespace MatContactHub.Services.Sources;

/// <summary>
/// What the connectors need from the app (a singleton): the protector of the secrets, the data folder (the uploaded CSV files lie in <c>&lt;data&gt;/sources</c>,
/// the folder resolved like the app does: the setting <c>DataDirectory</c>, else <c>/data</c>) and the HTTP clients (one that checks TLS certificates, one that
/// does not, for a source whose page says so). The tests make one with a fake handler.
/// </summary>
public sealed class SourceEnvironment
{
    /// <summary>The named HTTP client of the sources.</summary>
    public const string HttpClientName = "sources";

    /// <summary>The named HTTP client that accepts any TLS certificate (only for a source with <see cref="RestSettings.IgnoreTlsErrors"/>).</summary>
    public const string InsecureHttpClientName = "sources-insecure";

    private readonly IDataProtector _protector;
    private readonly Func<bool, HttpClient> _http;

    public SourceEnvironment(IDataProtectionProvider protection, string dataDirectory, Func<bool, HttpClient> http)
    {
        _protector = protection.CreateProtector("MatContactHub.Sources.Secrets");
        DataDirectory = dataDirectory;
        _http = http;
    }

    /// <summary>The environment of the running app.</summary>
    public static SourceEnvironment From(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IHttpClientFactory>();
        return new SourceEnvironment(
            services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<IConfiguration>()["DataDirectory"] ?? "/data",
            insecure => factory.CreateClient(insecure ? InsecureHttpClientName : HttpClientName));
    }

    public string DataDirectory { get; }

    /// <summary>The folder of the uploaded CSV files.</summary>
    public string SourcesDirectory => Path.Combine(DataDirectory, "sources");

    /// <summary>The uploaded CSV file of a data source.</summary>
    public string CsvPath(Guid token) => Path.Combine(SourcesDirectory, token.ToString("D") + ".csv");

    /// <summary>An HTTP client; <paramref name="ignoreTlsErrors"/> = one that accepts any certificate.</summary>
    public HttpClient Http(bool ignoreTlsErrors) => _http(ignoreTlsErrors);

    /// <summary>The secrets protected for <see cref="DataSource.SecretProtected"/>; null when there is none.</summary>
    public string? Protect(SourceSecrets secrets) => secrets.IsEmpty ? null : _protector.Protect(secrets.ToJson());

    /// <summary>The secrets of <see cref="DataSource.SecretProtected"/>; empty when there are none or the keys that protected them are gone.</summary>
    public SourceSecrets Unprotect(string? protectedSecrets)
    {
        if (string.IsNullOrEmpty(protectedSecrets))
        {
            return new SourceSecrets();
        }

        try
        {
            return SourceSecrets.FromJson(_protector.Unprotect(protectedSecrets));
        }
        catch (CryptographicException)
        {
            return new SourceSecrets();
        }
    }
}
