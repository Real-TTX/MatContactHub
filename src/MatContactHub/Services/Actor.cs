using MatContactHub.Data;
using MatContactHub.Services.Auth;

namespace MatContactHub.Services;

/// <summary>
/// Who does something in the business layer: a signed-in person (<see cref="UserId"/>, <see cref="IsAdmin"/>), an app bound through <c>contacts.directory</c> or the
/// platform API (<see cref="Consumer"/>), or the app itself (the scheduler, the housekeeping: <see cref="System"/>). Every static business class takes one and checks it.
/// </summary>
public sealed record Actor(long? UserId, bool IsAdmin, string? Consumer, string Name)
{
    public static readonly Actor Anonymous = new(null, false, null, "anonymous");

    public bool IsAnonymous => UserId is null && Consumer is null && !IsAdmin;

    /// <summary>The signed-in person of the request (anonymous without one).</summary>
    public static Actor ForUser(CurrentUser me) => me.Snapshot is { } s ? new Actor(s.Id, s.Role == UserRole.Admin, null, s.Email) : Anonymous;

    public static Actor ForPerson(long userId, bool admin, string name) => new(userId, admin, null, name);

    /// <summary>An app bound through <c>contacts.directory</c>: it reads the address books with <c>AppsEnabled</c>, nothing else.</summary>
    public static Actor ForConsumer(string consumer) => new(null, false, consumer, consumer);

    /// <summary>The app itself (the scheduler, the housekeeping) or the platform API: may do everything.</summary>
    public static Actor System(string name) => new(null, true, null, name);
}
