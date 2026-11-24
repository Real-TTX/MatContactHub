using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatContactHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services;

/// <summary>
/// The log of the administration (Administration → Activity): what was created, changed, deleted, synchronised. Never a secret. The action names the kind of the item
/// (<see cref="Actions"/>); <c>TargetId</c> is its id, so the activity page links to it.
/// <para>
/// The words of an entry are worded in the language of whoever reads the log, not of whoever acted: a text of the catalog is stored as its key and its values
/// (<see cref="Text"/>, <c>{"k":"Activity.Detail.Created","a":[]}</c>) and worded when it is shown (<see cref="Show"/>); names, addresses and numbers are stored as
/// they are.
/// </para>
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

    /// <summary>
    /// A text of the catalog for <see cref="AddAsync"/> (the target or the detail): its key and its values, worded when the log is read. A value may be such a text
    /// itself (<c>Text("Sources.Run.Summary", Text("Sources.State.Failed"), 1, 2, 0)</c>).
    /// </summary>
    public static string Text(string key, params object?[] values)
        => new JsonObject { ["k"] = key, ["a"] = new JsonArray([.. values.Select(ValueNode)]) }.ToJsonString(Plain);

    /// <summary>The words of a target or a detail in the language of the reader: a <see cref="Text"/> is worded, anything else is shown as it was stored.</summary>
    public static string? Show(string? stored)
    {
        if (stored is null || !stored.StartsWith(Marker, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            return JsonNode.Parse(stored) is JsonObject text ? Words(text) : stored;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // cut at the length of the column, say: shown as it is
            return stored;
        }
    }

    private const string Marker = "{\"k\":";

    // names with umlauts stay readable in the column (and the search of the log finds them)
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonNode? ValueNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static string Words(JsonObject text)
    {
        var values = (text["a"] as JsonArray ?? []).Select(v => v switch
        {
            JsonValue n when n.GetValueKind() == JsonValueKind.Number => (object)n.GetValue<long>(),
            JsonValue s when s.GetValueKind() == JsonValueKind.String => Show(s.GetValue<string>()) ?? string.Empty,
            _ => string.Empty,
        });
        return Localization.Text.T(text["k"]!.GetValue<string>(), [.. values]);
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

        // worded in the language of the reader
        return ([.. rows.Select(r => r with { Target = Show(r.Target), Detail = Show(r.Detail) })], total);
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}

public sealed record ActivityRow(long Id, DateTime Date, string Action, string? Target, string? Detail, string? Who, long? TargetId);
