using System.Text.Json;
using System.Text.Json.Serialization;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Auth;

/// <summary>
/// The data of a person under the GDPR: the export of everything that is about them (Art. 15 and 20, a JSON file), and the erasure of the account (Art. 17).
/// The person does both for themselves; an administrator erases an account through <see cref="Accounts.UserAdmin"/>.
/// <para>
/// The export holds the account and its settings, the roles in the address books, the activity of the person and the sessions. No password hash, no token.
/// </para>
/// <para>
/// The erasure deletes for good: the account, its sessions, roles and activity. The contacts a person created stay in their address books: they belong to the
/// address book, not to the account. The last active administrator is never erased (nobody could run the installation).
/// </para>
/// </summary>
public static class AccountDataService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The export as JSON (UTF-8); null unless the actor is the person.</summary>
    public static async Task<byte[]?> ExportAsync(AppDbContext db, Actor actor, long userId, CancellationToken ct = default)
    {
        if (actor.UserId != userId || await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) is not { } user)
        {
            return null;
        }

        var activity = await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredDate).Take(5000)
            .Select(a => new { a.OccurredDate, a.Action, a.Target, a.Detail }).ToListAsync(ct);
        var export = new
        {
            Format = "MatContactHub export 1",
            ExportedAt = DateTime.UtcNow,
            Account = new
            {
                user.Email,
                user.DisplayName,
                user.Role,
                user.TimeZone,
                user.Language,
                user.Theme,
                user.Scheme,
                user.CreateDate,
                user.LastLoginDate,
                LinkedWithMatIdentity = user.ExternalSubject != null,
            },
            Roles = await db.AddressBookAccess.AsNoTracking().Where(a => a.UserId == userId)
                .Select(a => new { AddressBook = a.AddressBook!.Name, a.Role, Since = a.CreateDate }).ToListAsync(ct),

            // the texts of the log in the language of the person
            Activity = activity.Select(a => new { a.OccurredDate, a.Action, Target = ActivityLog.Show(a.Target), Detail = ActivityLog.Show(a.Detail) }),
            Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new { s.CreateDate, s.LastSeenDate, s.ExpiresDate, s.IsRevoked, s.UserAgent, s.IpAddress }).ToListAsync(ct),
        };

        return JsonSerializer.SerializeToUtf8Bytes(export, Json);
    }

    /// <summary>
    /// Erases the account for good (see the class): the person themselves (after their password, which the page asks) or an administrator. Denied for anybody
    /// else; refused for the last active administrator.
    /// </summary>
    public static async Task<Outcome<bool>> EraseAsync(AppDbContext db, Actor actor, long userId, CancellationToken ct = default)
    {
        if (actor.UserId != userId && !actor.IsAdmin)
        {
            return Outcome<bool>.Denied();
        }

        if (await IsLastAdminAsync(db, userId, ct))
        {
            return Outcome<bool>.Fail(Text.T("Data.Delete.LastAdmin"));
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
            await db.Activities.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
            await db.AddressBookAccess.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
            await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        // past the change tracker: the cached sessions and the directory (the roles) learn it now
        SessionService.Invalidate();
        DirectoryChanges.Bump();
        return Outcome<bool>.Success(true);
    }

    /// <summary>Is the person the last active administrator (who must not go, or nobody can run the installation)?</summary>
    public static async Task<bool> IsLastAdminAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin && u.IsActive, ct)
           && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && u.IsActive, ct);
}
