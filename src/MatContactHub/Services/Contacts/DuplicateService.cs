using MatContactHub.Data;

namespace MatContactHub.Services.Contacts;

/// <summary>Why contacts are taken for the same person.</summary>
public enum DuplicateReason
{
    /// <summary>The same telephone number (compared in E.164 with <see cref="PhoneNormalizer"/>).</summary>
    Phone = 1,

    /// <summary>The same e-mail address (without case).</summary>
    Email = 2,

    /// <summary>The same name and company.</summary>
    NameAndCompany = 3,
}

/// <summary>A group of contacts that look like the same person, and why.</summary>
public sealed record DuplicateGroup(DuplicateReason Reason, string Key, IReadOnlyList<ContactRow> Contacts);

/// <summary>
/// The duplicates (module C owns this class; FOUNDATION STUB: finds nothing yet). Groups by normalised telephone number, e-mail address, name and company among the
/// contacts the actor may see, without the pairs of <see cref="DuplicateIgnore"/>; merges (into a manual contact) and marks pairs as "not a duplicate".
/// </summary>
public static class DuplicateService
{
    /// <summary>The groups of duplicates among the contacts the actor may see.</summary>
    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        return Task.FromResult<IReadOnlyList<DuplicateGroup>>([]);
    }

    /// <summary>Marks two contacts as "not a duplicate" (the actor must see both).</summary>
    public static Task<Outcome<bool>> IgnoreAsync(AppDbContext db, Actor actor, long contactId, long otherContactId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        return Task.FromResult(Outcome<bool>.Fail(Localization.Text.T("Common.NotYet")));
    }

    /// <summary>Merges <paramref name="mergedIds"/> into <paramref name="keepId"/> (the actor must be an editor of every address book involved). Answers the kept id.</summary>
    public static Task<Outcome<long>> MergeAsync(AppDbContext db, Actor actor, long keepId, IReadOnlyList<long> mergedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        return Task.FromResult(Outcome<long>.Fail(Localization.Text.T("Common.NotYet")));
    }
}
