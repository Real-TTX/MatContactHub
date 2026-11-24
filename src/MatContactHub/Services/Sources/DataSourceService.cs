using MatContactHub.Data;
using MatContactHub.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Sources;

/// <summary>A line of the list of data sources.</summary>
public sealed record DataSourceRow(long Id, string Name, DataSourceKind Kind, string AddressBook, bool Enabled, int IntervalMinutes, DateTime? LastRunDate,
    SyncState? LastRunState, DateTime? NextRunDate, int Contacts);

/// <summary>
/// What the edit page of a data source shows and sends (FOUNDATION STUB: the tab General; module B adds Connection, Mapping, Preview and Runs). Secrets are never
/// part of a form that goes to the browser.
/// </summary>
public sealed record DataSourceForm(string Name, DataSourceKind Kind, long AddressBookId, bool Enabled, int IntervalMinutes, string KeyField, bool DeleteMissing);

/// <summary>
/// The data sources (module B owns this class; FOUNDATION STUB: list, a simple save of the general settings, delete). Administrators only: everybody else gets
/// nothing (null / <see cref="Outcome{T}.Denied"/>).
/// </summary>
public static class DataSourceService
{
    public static async Task<IReadOnlyList<DataSourceRow>> ListAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return [];
        }

        return await db.DataSources.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new DataSourceRow(s.Id, s.Name, s.Kind, s.AddressBook!.Name, s.Enabled, s.IntervalMinutes, s.LastRunDate, s.LastRunState, s.NextRunDate,
                db.Contacts.Count(c => c.DataSourceId == s.Id)))
            .ToListAsync(ct);
    }

    public static async Task<DataSourceForm?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        return await db.DataSources.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new DataSourceForm(s.Name, s.Kind, s.AddressBookId, s.Enabled, s.IntervalMinutes, s.KeyField, s.DeleteMissing))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes a data source; administrators only. Answers its id.</summary>
    public static async Task<Outcome<long>> SaveAsync(AppDbContext db, Actor actor, long? id, DataSourceForm form, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<long>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length is 0 or > 120)
        {
            return Outcome<long>.Fail(Text.T("Sources.Name.Invalid"));
        }

        if (!await db.AddressBooks.AnyAsync(a => a.Id == form.AddressBookId, ct))
        {
            return Outcome<long>.Fail(Text.T("Sources.AddressBook.Required"));
        }

        if (form.IntervalMinutes is < 0 or > 10080)
        {
            return Outcome<long>.Fail(Text.T("Sources.Interval.Invalid"));
        }

        DataSource? source;
        if (id is { } existing)
        {
            source = await db.DataSources.FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (source is null)
            {
                return Outcome<long>.Denied();
            }
        }
        else
        {
            source = new DataSource { Kind = form.Kind };
            db.DataSources.Add(source);
        }

        source.Name = name;
        source.AddressBookId = form.AddressBookId;
        source.Enabled = form.Enabled;
        source.IntervalMinutes = form.IntervalMinutes;
        source.KeyField = form.KeyField.Trim();
        source.DeleteMissing = form.DeleteMissing;
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "source", source.Name, id is null ? "created" : "changed", source.Id, ct);
        return Outcome<long>.Success(source.Id);
    }

    /// <summary>Deletes (softly) a data source and its contacts; administrators only.</summary>
    public static async Task<Outcome<bool>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Outcome<bool>.Denied();
        }

        db.DataSources.Remove(source);
        foreach (var contact in await db.Contacts.Where(c => c.DataSourceId == id).ToListAsync(ct))
        {
            db.Contacts.Remove(contact);
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "source", source.Name, "deleted", source.Id, ct);
        return Outcome<bool>.Success(true);
    }
}
