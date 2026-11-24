using System.Globalization;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services;

/// <summary>The numbers of the installation (the platform tiles, the admin page System). Only how many, never what is in a contact.</summary>
public sealed record InstanceSnapshot(
    int Users,
    int Admins,
    int Contacts,
    int ManualContacts,
    int AddressBooks,
    int LdapAddressBooks,
    int DataSources,
    int SourcesFailing,
    int SyncRunsFailed7Days,
    DateTime? LastSyncUtc,
    DateTime? LastActivityUtc);

/// <summary>
/// The settings of the system (admin page System and the platform contract). <see cref="PublicUrl"/> is the address in force: the value of <c>MAT_PUBLIC_URL</c>
/// while it is set (then <see cref="PublicUrlFixed"/>), else the stored one.
/// </summary>
public sealed record SystemSettings(bool RegistrationOpen, string? PublicUrl, bool PublicUrlFixed, string DefaultCountry, int SyncRunDays);

/// <summary>What the housekeeping removed.</summary>
public sealed record CleanUpResult(int Sessions, int SyncRuns);

/// <summary>A change of the settings of the system: <c>null</c> leaves a value as it is.</summary>
public sealed record SettingsChange(bool? RegistrationOpen = null, string? PublicUrl = null, string? DefaultCountry = null, int? SyncRunDays = null);

/// <summary>
/// The installation as a whole: its numbers, the settings of the system (registration, public address, the default country of the telephone numbers, how long the
/// sync history is kept), the state of the database and the housekeeping. The admin page System, the platform contract (<c>Platform/MatContactHubPlatform</c>) and the
/// background housekeeping go through it, each with its actor: the numbers, the settings and the housekeeping are for administrators (a person with the role, or
/// the app itself: the platform API, the housekeeping), and every change of a setting goes to the activity log in the name of whoever made it. A broker such as
/// Mattiverse may set the public address (<c>MAT_PUBLIC_URL</c>): then it wins and the stored value stays as it is.
/// </summary>
public static class InstanceService
{
    /// <summary>The public address in force: <c>MAT_PUBLIC_URL</c>, else the stored one; null = the address of the request.</summary>
    public static string? PublicUrlOf(SystemSetting s, MatPlatformSettings platform) => platform.AppPublicUrl ?? NormalizePublicUrl(s.PublicUrl);

    /// <summary>The folder of the files of the app (keys, LDAP certificate, CSV files of the sources).</summary>
    public static string DataDirectoryOf(IConfiguration configuration) => configuration["DataDirectory"] ?? "/data";

    /// <summary>Can the database be reached (the health check)?</summary>
    public static async Task<bool> CanConnectAsync(AppDbContext db, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Can the data folder be written (the keys, the LDAP certificate, the CSV files)?</summary>
    public static bool CanWriteFiles(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var probe = Path.Combine(dataDirectory, $".probe-{Guid.NewGuid():n}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The numbers of the installation; null unless the actor is an administrator.</summary>
    public static async Task<InstanceSnapshot?> SnapshotAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var weekAgo = DateTime.UtcNow.AddDays(-7);
        return new InstanceSnapshot(
            Users: await db.Users.CountAsync(u => u.IsActive, ct),
            Admins: await db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct),
            Contacts: await db.Contacts.CountAsync(ct),
            ManualContacts: await db.Contacts.CountAsync(c => c.DataSourceId == null, ct),
            AddressBooks: await db.AddressBooks.CountAsync(ct),
            LdapAddressBooks: await db.AddressBooks.CountAsync(a => a.LdapEnabled, ct),
            DataSources: await db.DataSources.CountAsync(ct),
            SourcesFailing: await db.DataSources.CountAsync(s => s.Enabled && s.LastRunState == SyncState.Failed, ct),
            SyncRunsFailed7Days: await db.SyncRuns.CountAsync(r => r.State == SyncState.Failed && r.StartDate >= weekAgo, ct),
            LastSyncUtc: await db.SyncRuns.MaxAsync(r => (DateTime?)r.EndDate, ct),
            LastActivityUtc: await db.Activities.MaxAsync(a => (DateTime?)a.OccurredDate, ct));
    }

    /// <summary>Where the data is: <c>PostgreSQL host:port/database</c> (for the admin page; never in the platform API).</summary>
    public static string DatabaseName(AppDbContext db)
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        return $"PostgreSQL {cs.Host}:{cs.Port}/{cs.Database}";
    }

    /// <summary>The settings of the system; null unless the actor is an administrator.</summary>
    public static async Task<SystemSettings?> SettingsAsync(AppDbContext db, Actor actor, MatPlatformSettings platform, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var s = await SettingsStore.LoadAsync(db, ct);
        return new SystemSettings(s.RegistrationOpen, PublicUrlOf(s, platform), platform.AppPublicUrl is not null, s.DefaultCountry, s.SyncRunDays);
    }

    /// <summary>
    /// Saves the settings of the system (administrators); a <c>null</c> leaves a value as it is, an empty address clears it. An address that <c>MAT_PUBLIC_URL</c>
    /// sets is left as it is. Every setting that changes goes to the activity log (the setting and its new value) in the name of the actor. Answers the field and
    /// the message of the first wrong value (nothing is saved then), a message without a field when the actor may not, else <c>null</c>.
    /// </summary>
    public static async Task<FieldError?> SaveSettingsAsync(AppDbContext db, Actor actor, MatPlatformSettings platform, SettingsChange change, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return new FieldError(string.Empty, Text.T("Common.NotAllowed"));
        }

        var publicUrl = platform.AppPublicUrl is null ? change.PublicUrl : null;
        string? url = null;
        if (publicUrl is not null)
        {
            url = NormalizePublicUrl(publicUrl);
            if (url is not null && !IsPublicUrl(url))
            {
                return new FieldError("PublicUrl", Text.T("System.PublicUrl.Invalid"));
            }
        }

        var country = change.DefaultCountry?.Trim().ToUpperInvariant();
        if (country is not null && !Contacts.PhoneNormalizer.IsKnownCountry(country))
        {
            return new FieldError("DefaultCountry", Text.T("System.DefaultCountry.Invalid"));
        }

        if (change.SyncRunDays is < 1 or > 3650)
        {
            return new FieldError("SyncRunDays", Text.T("System.Range", 1, 3650));
        }

        var s = await SettingsStore.LoadAsync(db, ct);

        // what really changes: the name of the setting (a text of the catalog) and its new value
        var changed = new List<(string Setting, string Value)>();
        if (change.RegistrationOpen is { } open && open != s.RegistrationOpen)
        {
            s.RegistrationOpen = open;
            changed.Add(("System.RegistrationOpen", ActivityLog.Text(open ? "Activity.Detail.On" : "Activity.Detail.Off")));
        }

        if (publicUrl is not null && url != s.PublicUrl)
        {
            s.PublicUrl = url;
            changed.Add(("System.PublicUrl", url ?? ActivityLog.Text("Activity.Detail.Cleared")));
        }

        if (country is not null && country != s.DefaultCountry)
        {
            s.DefaultCountry = country;
            changed.Add(("System.DefaultCountry", country));
        }

        if (change.SyncRunDays is { } days && days != s.SyncRunDays)
        {
            s.SyncRunDays = days;
            changed.Add(("System.SyncRunDays", days.ToString(CultureInfo.InvariantCulture)));
        }

        await db.SaveChangesAsync(ct);
        foreach (var (setting, value) in changed)
        {
            await ActivityLog.AddAsync(db, actor, "setting", ActivityLog.Text(setting), value, null, ct);
        }

        return null;
    }

    /// <summary>The address without spaces and the last slash; <c>null</c> when empty.</summary>
    public static string? NormalizePublicUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>
    /// The housekeeping (administrators, and the app itself every hour): ended sessions go; finished sync runs older than <see cref="SystemSetting.SyncRunDays"/> go
    /// from the history. Null when the actor may not.
    /// </summary>
    public static async Task<CleanUpResult?> CleanUpAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var sessions = await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        var s = await SettingsStore.LoadAsync(db, ct);
        var before = now.AddDays(-Math.Max(1, s.SyncRunDays));
        var runs = await db.SyncRuns.Where(r => r.State != SyncState.Running && r.StartDate < before).ExecuteDeleteAsync(ct);
        return new CleanUpResult(sessions, runs);
    }
}
