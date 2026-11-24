using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.AspNetCore.DataProtection;

namespace MatContactHub.Services.Ldap;

/// <summary>What the tab Certificate shows of the certificate in force.</summary>
public sealed record LdapCertificateInfo(LdapCertificateSource Source, string Subject, string Issuer, string Thumbprint, string Sha256, DateTime NotBeforeUtc,
    DateTime NotAfterUtc, IReadOnlyList<string> Names, string Key, bool SelfSigned, string? Problem)
{
    public bool Expired => NotAfterUtc < DateTime.UtcNow;
}

/// <summary>The certificate in force and why it is not the uploaded one (an uploaded file that cannot be read: the self-signed one stands in).</summary>
public sealed record LdapCertificateLoad(X509Certificate2 Certificate, string? Problem);

/// <summary>
/// The certificate of LDAPS and StartTLS, in the folder <c>/data/ldap</c> (<see cref="LdapSettings.Folder"/> in the data folder). Either self-signed, made at the
/// first start (the PFX is protected with DataProtection in <c>selfsigned.pfx.protected</c>), or a PFX an administrator uploads (the file as it is, its password
/// protected with DataProtection in <c>SystemSetting.LdapCertificatePasswordProtected</c>). The columns Thumbprint, Subject and Expires of <see cref="SystemSetting"/>
/// name the certificate in force; a change of them (they are part of the directory's tables) tells the server to load the certificate again.
/// The changes are for administrators only (checked here); <see cref="LoadAsync"/> is for the server itself.
/// </summary>
public static class LdapCertificateService
{
    public const string SelfSignedFile = "selfsigned.pfx.protected";

    /// <summary>The largest PFX accepted.</summary>
    public const int MaxUploadBytes = 100 * 1024;

    private const string Purpose = "MatContactHub.Ldap.Certificate";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The folder of the certificate: <c>&lt;DataDirectory&gt;/ldap</c>.</summary>
    public static string FolderOf(IConfiguration configuration) => Path.Combine(configuration["DataDirectory"] ?? "/data", LdapSettings.Folder);

    /// <summary>
    /// The certificate in force: the uploaded one when the settings say so and it can be read, else the self-signed one (made when there is none). The columns of
    /// the settings are brought up to date.
    /// </summary>
    public static async Task<LdapCertificateLoad> LoadAsync(AppDbContext db, IDataProtectionProvider protection, string folder, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await LoadLockedAsync(db, protection, folder, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The certificate in force for the page (administrators; null for anybody else).</summary>
    public static async Task<LdapCertificateInfo?> DescribeAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, string folder, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var load = await LoadAsync(db, protection, folder, ct);
        using var cert = load.Certificate;
        return Describe(cert, (await SettingsStore.LoadAsync(db, ct)).LdapCertificateSource, load.Problem);
    }

    /// <summary>The public certificate in PEM (for the trust store of a telephone); administrators only.</summary>
    public static async Task<string?> ExportPemAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, string folder, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        using var cert = (await LoadAsync(db, protection, folder, ct)).Certificate;
        return cert.ExportCertificatePem() + "\n";
    }

    /// <summary>Makes a new self-signed certificate and uses it (administrators only).</summary>
    public static async Task<Outcome<LdapCertificateInfo>> RegenerateAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, string folder,
        CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<LdapCertificateInfo>.Denied();
        }

        await Gate.WaitAsync(ct);
        try
        {
            var s = await SettingsStore.LoadAsync(db, ct);
            Directory.CreateDirectory(folder);
            var created = CreateSelfSigned(NamesOf(s));
            await File.WriteAllBytesAsync(Path.Combine(folder, SelfSignedFile), protection.CreateProtector(Purpose).Protect(created.Export(X509ContentType.Pkcs12)), ct);
            created.Dispose();
            s.LdapCertificateSource = LdapCertificateSource.SelfSigned;
            var load = await LoadLockedAsync(db, protection, folder, ct);
            using var cert = load.Certificate;
            await ActivityLog.AddAsync(db, actor, "ldap", "LDAP", "certificate regenerated", null, ct);
            return Outcome<LdapCertificateInfo>.Success(Describe(cert, s.LdapCertificateSource, load.Problem));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Goes back to the self-signed certificate (administrators only); the uploaded file is removed.</summary>
    public static async Task<Outcome<LdapCertificateInfo>> UseSelfSignedAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, string folder,
        CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<LdapCertificateInfo>.Denied();
        }

        await Gate.WaitAsync(ct);
        try
        {
            var s = await SettingsStore.LoadAsync(db, ct);
            var old = s.LdapCertificateFile;
            s.LdapCertificateSource = LdapCertificateSource.SelfSigned;
            s.LdapCertificateFile = null;
            s.LdapCertificatePasswordProtected = null;
            var load = await LoadLockedAsync(db, protection, folder, ct);
            using var cert = load.Certificate;
            DeleteQuietly(folder, old);
            await ActivityLog.AddAsync(db, actor, "ldap", "LDAP", "certificate self-signed", null, ct);
            return Outcome<LdapCertificateInfo>.Success(Describe(cert, s.LdapCertificateSource, load.Problem));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Uses an uploaded PFX (administrators only): it must open with the password, carry a private key and not be expired.</summary>
    public static async Task<Outcome<LdapCertificateInfo>> UploadAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, string folder, byte[] pfx,
        string? password, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<LdapCertificateInfo>.Denied();
        }

        if (pfx.Length == 0 || pfx.Length > MaxUploadBytes)
        {
            return Outcome<LdapCertificateInfo>.Fail(Text.T("Ldap.Certificate.Upload.Size", MaxUploadBytes / 1024));
        }

        X509Certificate2 cert;
        try
        {
            cert = Open(pfx, password ?? string.Empty);
        }
        catch (CryptographicException)
        {
            return Outcome<LdapCertificateInfo>.Fail(Text.T("Ldap.Certificate.Upload.Unreadable"));
        }

        using (cert)
        {
            if (!cert.HasPrivateKey)
            {
                return Outcome<LdapCertificateInfo>.Fail(Text.T("Ldap.Certificate.Upload.NoKey"));
            }

            if (cert.NotAfter.ToUniversalTime() < DateTime.UtcNow)
            {
                return Outcome<LdapCertificateInfo>.Fail(Text.T("Ldap.Certificate.Upload.Expired"));
            }
        }

        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(folder);
            var file = $"uploaded-{DateTime.UtcNow:yyyyMMddHHmmssfff}.pfx";
            await File.WriteAllBytesAsync(Path.Combine(folder, file), pfx, ct);
            var s = await SettingsStore.LoadAsync(db, ct);
            var old = s.LdapCertificateFile;
            s.LdapCertificateSource = LdapCertificateSource.Uploaded;
            s.LdapCertificateFile = file;
            s.LdapCertificatePasswordProtected = string.IsNullOrEmpty(password) ? null : protection.CreateProtector(Purpose).Protect(password);
            var load = await LoadLockedAsync(db, protection, folder, ct);
            using var inForce = load.Certificate;
            if (old != file)
            {
                DeleteQuietly(folder, old);
            }

            await ActivityLog.AddAsync(db, actor, "ldap", "LDAP", "certificate uploaded: " + inForce.Subject, null, ct);
            return Outcome<LdapCertificateInfo>.Success(Describe(inForce, s.LdapCertificateSource, load.Problem));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A self-signed certificate for the server (RSA 2048, SHA-256, five years; the names as subject alternative names, the first as CN).</summary>
    public static X509Certificate2 CreateSelfSigned(IReadOnlyList<string> names)
    {
        using var rsa = RSA.Create(2048);
        var first = names.FirstOrDefault() ?? "localhost";
        var request = new CertificateRequest(new X500DistinguishedName($"CN={first.Replace(",", string.Empty, StringComparison.Ordinal)}, O={Ui.AppName}"), rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        // the key of CreateSelfSigned is ephemeral (Windows cannot use it for TLS): once through PKCS#12, like the file it is kept in
        return Open(created.Export(X509ContentType.Pkcs12), string.Empty);
    }

    /// <summary>The details of a certificate.</summary>
    public static LdapCertificateInfo Describe(X509Certificate2 cert, LdapCertificateSource source, string? problem)
    {
        var names = new List<string>();
        foreach (var ext in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(ext.EnumerateDnsNames());
            names.AddRange(ext.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }

        var key = cert.GetRSAPublicKey() is { } rsa ? $"RSA {rsa.KeySize}" : cert.GetECDsaPublicKey() is { } ec ? $"ECDSA {ec.KeySize}" : cert.PublicKey.Oid.FriendlyName ?? "?";
        return new LdapCertificateInfo(source, cert.Subject, cert.Issuer, cert.Thumbprint, cert.GetCertHashString(HashAlgorithmName.SHA256), cert.NotBefore.ToUniversalTime(),
            cert.NotAfter.ToUniversalTime(), names, key, cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData), problem);
    }

    private static async Task<LdapCertificateLoad> LoadLockedAsync(AppDbContext db, IDataProtectionProvider protection, string folder, CancellationToken ct)
    {
        var s = await SettingsStore.LoadAsync(db, ct);
        var protector = protection.CreateProtector(Purpose);
        X509Certificate2? cert = null;
        string? problem = null;
        if (s.LdapCertificateSource == LdapCertificateSource.Uploaded)
        {
            try
            {
                var file = s.LdapCertificateFile is { } name ? Path.Combine(folder, Path.GetFileName(name)) : throw new FileNotFoundException();
                var password = s.LdapCertificatePasswordProtected is { } p ? protector.Unprotect(p) : string.Empty;
                cert = Open(await File.ReadAllBytesAsync(file, ct), password);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
            {
                problem = Text.T("Ldap.Certificate.Problem");
            }
        }

        if (cert is null)
        {
            var path = Path.Combine(folder, SelfSignedFile);
            if (File.Exists(path))
            {
                try
                {
                    cert = Open(protector.Unprotect(await File.ReadAllBytesAsync(path, ct)), string.Empty);
                }
                catch (CryptographicException)
                {
                    // made with other keys of DataProtection (a volume that lost its keys): a new one is made below
                    cert = null;
                }
            }

            if (cert is null)
            {
                Directory.CreateDirectory(folder);
                cert = CreateSelfSigned(NamesOf(s));
                await File.WriteAllBytesAsync(path, protector.Protect(cert.Export(X509ContentType.Pkcs12)), ct);
            }
        }

        var expires = cert.NotAfter.ToUniversalTime();
        if (s.LdapCertificateThumbprint != cert.Thumbprint || s.LdapCertificateSubject != Cut(cert.Subject, 500) || s.LdapCertificateExpires != expires
            || db.Entry(s).State == Microsoft.EntityFrameworkCore.EntityState.Modified)
        {
            s.LdapCertificateThumbprint = cert.Thumbprint;
            s.LdapCertificateSubject = Cut(cert.Subject, 500);
            s.LdapCertificateExpires = expires;
            await db.SaveChangesAsync(ct);
        }

        return new LdapCertificateLoad(cert, problem);
    }

    /// <summary>The names of the self-signed certificate: the host of the public address, the name of the machine, localhost.</summary>
    private static List<string> NamesOf(SystemSetting s)
    {
        var names = new List<string>();
        if (Uri.TryCreate(s.PublicUrl, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Host))
        {
            names.Add(url.Host);
        }

        var host = Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(host) && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
        {
            names.Add(host.ToLowerInvariant());
        }

        names.Add("localhost");
        return names;
    }

    private static X509Certificate2 Open(byte[] pfx, string password)
        => X509CertificateLoader.LoadPkcs12(pfx, password, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);

    private static void DeleteQuietly(string folder, string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        try
        {
            File.Delete(Path.Combine(folder, Path.GetFileName(file)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
