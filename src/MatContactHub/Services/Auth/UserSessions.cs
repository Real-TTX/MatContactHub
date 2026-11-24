using MatContactHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Auth;

/// <summary>
/// The sessions of a person as business: ending them (a new password, an account an administrator locked, the sign-out at MatIdentity) and the ID token of
/// MatIdentity a session keeps. The cookie and the cache of the sessions are <see cref="SessionService"/>.
/// </summary>
public static class UserSessions
{
    /// <summary>
    /// Ends every session of a person, except one (the current one, after a change of the password): the person themselves or an administrator. False when the
    /// actor may not.
    /// </summary>
    public static async Task<bool> RevokeAllAsync(AppDbContext db, Actor actor, long userId, long? exceptSessionId = null, CancellationToken ct = default)
    {
        if (actor.UserId != userId && !actor.IsAdmin)
        {
            return false;
        }

        await db.Sessions.Where(s => s.UserId == userId && s.Id != exceptSessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);
        SessionService.Invalidate();
        return true;
    }

    /// <summary>The ID token of MatIdentity a session of the actor started with; null for a local sign-in or a session of somebody else.</summary>
    public static async Task<string?> IdentityTokenAsync(AppDbContext db, Actor actor, long? sessionId, CancellationToken ct = default)
        => sessionId is { } id && actor.UserId is { } userId
            ? await db.Sessions.AsNoTracking().Where(s => s.Id == id && s.UserId == userId).Select(s => s.IdentityToken).FirstOrDefaultAsync(ct)
            : null;

    /// <summary>
    /// The back-channel logout of MatIdentity (MatIdentity acts, with a logout token MatPlatform checked): with <paramref name="identitySessionId"/> the sessions
    /// that started with that session of MatIdentity end (only those of the person of <paramref name="subject"/> when it is named too); with
    /// <paramref name="subject"/> alone every session of the account linked with it. An unknown subject or session ends nothing. The next request of an ended
    /// session is signed out (<see cref="SessionService.ValidateAsync"/>). Answers how many ended.
    /// </summary>
    public static async Task<int> EndIdentitySessionsAsync(AppDbContext db, string? subject, string? identitySessionId, CancellationToken ct = default)
    {
        var sessions = db.Sessions.Where(s => !s.IsRevoked);
        if (!string.IsNullOrEmpty(identitySessionId))
        {
            sessions = sessions.Where(s => s.IdentitySessionId == identitySessionId);
            if (!string.IsNullOrEmpty(subject))
            {
                sessions = sessions.Where(s => s.User!.ExternalSubject == subject);
            }
        }
        else if (!string.IsNullOrEmpty(subject))
        {
            sessions = sessions.Where(s => s.User!.ExternalSubject == subject);
        }
        else
        {
            return 0;
        }

        var ended = await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);
        SessionService.Invalidate();
        return ended;
    }
}
