using MatContactHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services;

/// <summary>
/// The log of the administration (Administration → Activity): what was created, changed, deleted, synchronised. Never a secret. The action names the kind of the item
/// (<see cref="Actions"/>); <c>TargetId</c> is its id, so the activity page links to it.
/// </summary>
public static class ActivityLog
{
    /// <summary>The actions of the log (the filter of the activity page; the texts are <c>Activity.Action.&lt;action&gt;</c>).</summary>
    public static readonly string[] Actions = ["contact", "addressbook", "source", "sync", "ldap", "user", "setting"];

    public static async Task AddAsync(AppDbContext db, Actor actor, string action, string? target, string? detail, long? targetId, CancellationToken ct)
    {
        db.Activities.Add(new Activity
        {
            UserId = actor.UserId,
            Consumer = actor.Consumer ?? (actor.UserId is null ? actor.Name : null),
            Action = action,
            Target = Cut(target, 255),
            Detail = Cut(detail, 1000),
            TargetId = targetId,
            OccurredDate = DateTime.UtcNow,
            CreateUserId = actor.UserId,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The address of the item of an entry, or null (it may not have a page).</summary>
    public static string? LinkOf(string action, long? targetId) => targetId is not { } id ? null : action switch
    {
        "contact" => $"/Contacts/Edit?id={id}",
        "addressbook" => $"/AddressBooks/Edit?id={id}",
        "source" or "sync" => $"/Sources/Edit?id={id}",
        "user" => $"/Admin/Users/Edit?id={id}",
        _ => null,
    };

    /// <summary>The log for administrators (null for anybody else), newest first.</summary>
    public static async Task<(IReadOnlyList<ActivityRow> Rows, int Total)?> AllAsync(AppDbContext db, long adminId, string? search, string? action, int page, int pageSize,
        CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive, ct))
        {
            return null;
        }

        var query = db.Activities.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(a => EF.Functions.ILike(a.Target ?? string.Empty, pattern) || EF.Functions.ILike(a.Detail ?? string.Empty, pattern)
                || EF.Functions.ILike(a.Consumer ?? string.Empty, pattern)
                || db.Users.IgnoreQueryFilters().Any(u => u.Id == a.UserId && EF.Functions.ILike(u.DisplayName, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .Select(a => new ActivityRow(a.Id, a.OccurredDate, a.Action, a.Target, a.Detail,
                db.Users.IgnoreQueryFilters().Where(u => u.Id == a.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? a.Consumer, a.TargetId))
            .ToListAsync(ct);
        return (rows, total);
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}

public sealed record ActivityRow(long Id, DateTime Date, string Action, string? Target, string? Detail, string? Who, long? TargetId);
