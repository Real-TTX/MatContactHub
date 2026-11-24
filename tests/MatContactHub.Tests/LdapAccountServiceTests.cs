using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Ldap;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Tests;

/// <summary>The business layer of the LDAP module: the accounts, the settings and the certificate, for administrators only.</summary>
public sealed class LdapAccountServiceTests
{
    private static readonly Actor Admin = Actor.System("test");
    private static readonly Actor Person = Actor.ForPerson(7, false, "someone@example.com");
    private static readonly PasswordService Passwords = LdapTestData.Passwords;

    private static LdapAccountForm Form(string name, bool all = true, params long[] books) => new(name, "a description", true, all, books);

    [DbFact]
    public async Task Only_administrators_see_and_change_accounts()
    {
        await using var t = await TestDatabase.CreateAsync();
        Assert.Null(await LdapAccountService.ListAsync(t.Db, Person, new LdapAccountCriteria(null, null, "name", false, 1, 25)));
        Assert.True((await LdapAccountService.SaveAsync(t.Db, Person, Passwords, null, Form("starface"), "long enough password")).NotAllowed);
        var id = (await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("starface"), "long enough password")).Value;
        Assert.Null(await LdapAccountService.GetAsync(t.Db, Person, id));
        Assert.True((await LdapAccountService.DeleteAsync(t.Db, Person, id)).NotAllowed);
        Assert.Null(await LdapAccountService.AddressBooksAsync(t.Db, Person));
        Assert.True((await LdapSettingsService.SaveAsync(t.Db, Person, await LdapSettingsService.LoadAsync(t.Db))).NotAllowed);
    }

    [DbFact]
    public async Task The_name_is_kept_in_small_letters_unique_and_the_password_hashed()
    {
        await using var t = await TestDatabase.CreateAsync();
        var saved = await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form(" StarFace "), "long enough password");
        Assert.True(saved.Ok);
        var account = await t.Db.LdapAccounts.SingleAsync();
        Assert.Equal("starface", account.Name);
        Assert.NotEqual("long enough password", account.PasswordHash);
        Assert.True(Passwords.Verify(account.PasswordHash, "long enough password"));

        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("STARFACE"), "long enough password")).Ok);
        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("star face"), "long enough password")).Ok);
        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("müller"), "long enough password")).Ok);
        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("yealink"), null)).Ok);
        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("yealink"), "short")).Ok);
        Assert.Equal(1, await t.Db.LdapAccounts.CountAsync());

        // a save without a password keeps the old one, a new one replaces it
        var hash = account.PasswordHash;
        Assert.True((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, saved.Value, Form("starface"), null)).Ok);
        t.Db.ChangeTracker.Clear();
        Assert.Equal(hash, (await t.Db.LdapAccounts.SingleAsync()).PasswordHash);
        Assert.True((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, saved.Value, Form("starface"), "another long password")).Ok);
        t.Db.ChangeTracker.Clear();
        Assert.True(Passwords.Verify((await t.Db.LdapAccounts.SingleAsync()).PasswordHash, "another long password"));

        // deleted: the name is free again
        Assert.True((await LdapAccountService.DeleteAsync(t.Db, Admin, saved.Value)).Ok);
        Assert.True((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("starface"), "long enough password")).Ok);
        Assert.Contains(await t.Db.Activities.Select(a => a.Detail).ToListAsync(), d => d == ActivityLog.Text("Activity.Detail.Deleted"));
    }

    [DbFact]
    public async Task An_account_with_chosen_address_books_needs_at_least_one()
    {
        await using var t = await TestDatabase.CreateAsync();
        var book = new AddressBook { Name = "Customers", LdapName = "customers", LdapEnabled = true };
        var other = new AddressBook { Name = "Staff", LdapName = "staff" };
        t.Db.AddressBooks.AddRange(book, other);
        await t.Db.SaveChangesAsync();

        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("phone", false), "long enough password")).Ok);
        Assert.False((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("phone", false, 999), "long enough password")).Ok);
        var id = (await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, null, Form("phone", false, book.Id, 999), "long enough password")).Value;
        Assert.Equal([book.Id], (await LdapAccountService.GetAsync(t.Db, Admin, id))!.Form.AddressBookIds);

        Assert.True((await LdapAccountService.SaveAsync(t.Db, Admin, Passwords, id, Form("phone", false, other.Id), null)).Ok);
        Assert.Equal([other.Id], (await LdapAccountService.GetAsync(t.Db, Admin, id))!.Form.AddressBookIds);

        var list = (await LdapAccountService.ListAsync(t.Db, Admin, new LdapAccountCriteria("pho", "enabled", "name", false, 1, 25)))!;
        Assert.Equal(1, list.Matching);
        Assert.Equal(1, list.Items[0].AddressBooks);
        Assert.Equal(2, (await LdapAccountService.AddressBooksAsync(t.Db, Admin))!.Count);
    }

    [DbFact]
    public async Task The_settings_check_their_values()
    {
        await using var t = await TestDatabase.CreateAsync();
        var s = await LdapSettingsService.LoadAsync(t.Db);
        Assert.False((await LdapSettingsService.SaveAsync(t.Db, Admin, s with { BaseDn = "not a dn" })).Ok);
        Assert.False((await LdapSettingsService.SaveAsync(t.Db, Admin, s with { SizeLimit = 0 })).Ok);
        Assert.False((await LdapSettingsService.SaveAsync(t.Db, Admin, s with { DefaultCountry = "XX" })).Ok);
        var saved = await LdapSettingsService.SaveAsync(t.Db, Admin, s with { BaseDn = " o=Example ", SizeLimit = 50, DefaultCountry = "at" });
        Assert.True(saved.Ok);
        Assert.Equal("o=Example", saved.Value!.BaseDn);
        Assert.Equal("AT", saved.Value.DefaultCountry);
    }

    [DbFact]
    public async Task An_uploaded_PFX_is_used_with_its_password_kept_protected()
    {
        await using var t = await TestDatabase.CreateAsync();
        var folder = Path.Combine(Path.GetTempPath(), "mch-ldap-" + Guid.NewGuid().ToString("N")[..8]);
        var protection = new EphemeralDataProtectionProvider();
        try
        {
            var first = await LdapCertificateService.DescribeAsync(t.Db, Admin, protection, folder);
            Assert.NotNull(first);
            Assert.Equal(LdapCertificateSource.SelfSigned, first.Source);
            Assert.True(first.SelfSigned);
            Assert.Contains("localhost", first.Names);
            Assert.Null(await LdapCertificateService.DescribeAsync(t.Db, Person, protection, folder));

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=ldap.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var pfx = cert.Export(X509ContentType.Pkcs12, "pfx-password");

            Assert.False((await LdapCertificateService.UploadAsync(t.Db, Admin, protection, folder, pfx, "wrong")).Ok);
            Assert.False((await LdapCertificateService.UploadAsync(t.Db, Admin, protection, folder, cert.Export(X509ContentType.Cert), null)).Ok);
            Assert.True((await LdapCertificateService.UploadAsync(t.Db, Person, protection, folder, pfx, "pfx-password")).NotAllowed);

            var uploaded = await LdapCertificateService.UploadAsync(t.Db, Admin, protection, folder, pfx, "pfx-password");
            Assert.True(uploaded.Ok, uploaded.Error);
            Assert.Equal(LdapCertificateSource.Uploaded, uploaded.Value!.Source);
            Assert.Equal("CN=ldap.example.com", uploaded.Value.Subject);
            var s = await SettingsStore.LoadAsync(t.Db);
            Assert.Equal(cert.Thumbprint, s.LdapCertificateThumbprint);
            Assert.NotNull(s.LdapCertificatePasswordProtected);
            Assert.DoesNotContain("pfx-password", s.LdapCertificatePasswordProtected);
            Assert.True(File.Exists(Path.Combine(folder, s.LdapCertificateFile!)));
            Assert.Contains("BEGIN CERTIFICATE", await LdapCertificateService.ExportPemAsync(t.Db, Admin, protection, folder));

            var file = s.LdapCertificateFile!;
            var back = await LdapCertificateService.UseSelfSignedAsync(t.Db, Admin, protection, folder);
            Assert.True(back.Ok);
            Assert.Equal(first.Thumbprint, back.Value!.Thumbprint);
            Assert.False(File.Exists(Path.Combine(folder, file)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
