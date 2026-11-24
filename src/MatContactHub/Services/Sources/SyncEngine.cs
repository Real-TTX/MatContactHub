using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services.Contacts;
using Microsoft.EntityFrameworkCore;

namespace MatContactHub.Services.Sources;

/// <summary>
/// The sync of one data source (module B). It reads the records of the connector, maps them (<see cref="FieldMapper"/>), skips a record without a key (counted
/// in <see cref="SyncRun.Skipped"/> with a sample of the reasons in the message), keeps the last of records with the same key (a warning), compares
/// <see cref="Contact.SyncHash"/> (unchanged: nothing is written), upserts by (DataSourceId, ExternalKey) – a softly deleted contact comes back as the same row,
/// with its Token –, and with <see cref="DataSource.DeleteMissing"/> deletes (softly) the contacts of this source that were not seen, but only when the read ended
/// without an error, was not cut at a limit, gave usable records and skipped at most half of what it read (a key field that is gone upstream would else delete
/// every contact); a record that was read with a key but skipped (no value, a key too long) counts as seen, its contact stays. It saves in batches of <see cref="BatchSize"/>, calls <see cref="DirectoryChanges.Bump"/>
/// after its bulk changes, and never touches <see cref="Contact.Hidden"/> nor a manual contact. <see cref="Contact.LastSyncDate"/> is the time the sync last
/// wrote a contact (an unchanged one is not written again, so the table does not churn).
/// </summary>
public static class SyncEngine
{
    /// <summary>The contacts written per SaveChanges.</summary>
    public const int BatchSize = 500;

    /// <summary>The longest message of a run.</summary>
    public const int MessageLimit = 4000;

    /// <summary>How many examples of a reason a message names.</summary>
    private const int Samples = 5;

    /// <summary>The data sources that are due at <paramref name="now"/>: switched on, synchronised by time and their next run has come.</summary>
    public static Expression<Func<DataSource, bool>> DueAt(DateTime now)
        => s => s.Enabled && s.IntervalMinutes > 0 && s.NextRunDate != null && s.NextRunDate <= now;

    /// <summary>Is the data source due at <paramref name="now"/> (the same rule as <see cref="DueAt"/>)?</summary>
    public static bool IsDue(DataSource source, DateTime now) => DueAt(now).Compile()(source);

    /// <summary>The ids of the data sources that are due (the scheduler, as the app itself).</summary>
    public static async Task<IReadOnlyList<long>> DueAsync(AppDbContext db, Actor actor, DateTime now, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return [];
        }

        return await db.DataSources.AsNoTracking().Where(DueAt(now)).OrderBy(s => s.NextRunDate).Select(s => s.Id).ToListAsync(ct);
    }

    /// <summary>The ids of every data source that is switched on (Sync all).</summary>
    public static async Task<IReadOnlyList<long>> EnabledAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return [];
        }

        return await db.DataSources.AsNoTracking().Where(s => s.Enabled).OrderBy(s => s.Name).Select(s => s.Id).ToListAsync(ct);
    }

    /// <summary>
    /// The runs that are still "Running" from a process that ended (the start of the app): they failed. Answers how many there were.
    /// </summary>
    public static async Task<int> FailStaleRunsAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var message = Text.T("Sources.Run.Interrupted");
        var runs = await db.SyncRuns.Where(r => r.State == SyncState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, SyncState.Failed).SetProperty(r => r.EndDate, now).SetProperty(r => r.Message, message), ct);
        await db.DataSources.IgnoreQueryFilters().Where(s => s.LastRunState == SyncState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunState, SyncState.Failed).SetProperty(x => x.LastError, message), ct);
        return runs;
    }

    /// <summary>Runs the sync of a data source now and answers the finished run; null when the actor may not or the source does not exist.</summary>
    public static async Task<SyncRun?> RunAsync(AppDbContext db, Actor actor, SourceEnvironment env, long dataSourceId, SyncTrigger trigger, CancellationToken ct = default)
    {
        if (!actor.IsAdmin || await db.DataSources.Include(s => s.Mappings).FirstOrDefaultAsync(s => s.Id == dataSourceId, ct) is not { } source)
        {
            return null;
        }

        var country = (await SettingsStore.LoadAsync(db, ct)).DefaultCountry;
        var run = new SyncRun { DataSourceId = source.Id, Trigger = trigger, State = SyncState.Running, StartDate = DateTime.UtcNow };
        db.SyncRuns.Add(run);
        source.LastRunState = SyncState.Running;
        await db.SaveChangesAsync(ct);

        var counts = new Counts();
        var log = new RunLog();
        var fields = new FieldCollector();
        var state = SyncState.Succeeded;
        string? error = null;
        var job = new Job(source.Id, source.AddressBookId, source.KeyField, source.DeleteMissing,
            [.. source.Mappings.OrderBy(m => m.SortOrder).Select(m => new MappingRule(m.TargetField, m.SourceExpression, m.Transform))]);
        var records = new Dictionary<string, MappedRecord>(StringComparer.Ordinal);

        // the keys of every record read with a key, also of those skipped: their contacts are not "missing"
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = false;

        // 1. read and map
        try
        {
            if (string.IsNullOrWhiteSpace(job.KeyField))
            {
                throw new SourceException(Text.T("Sources.Error.NoKey"));
            }

            var connector = Connectors.Create(source, env);
            await foreach (var record in connector.ReadAsync(ct))
            {
                counts.Read++;
                fields.Add(record);
                var mapped = FieldMapper.Map(record, job.KeyField, job.Rules, country);
                if (mapped.Key is null)
                {
                    counts.Skipped++;
                    log.Add("NoKey", counts.Read.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }

                seen.Add(mapped.Key);
                if (mapped.Key.Length > FieldMapper.MaxKeyLength)
                {
                    counts.Skipped++;
                    log.Add("KeyTooLong", counts.Read.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }

                if (!FieldMapper.HasAnyValue(mapped))
                {
                    counts.Skipped++;
                    log.Add("Empty", mapped.Key);
                    continue;
                }

                if (!records.TryAdd(mapped.Key, mapped))
                {
                    // the last record with a key wins
                    records[mapped.Key] = mapped;
                    log.Add("Duplicate", mapped.Key);
                }
            }

            complete = !connector.Truncated;
            if (connector.Truncated)
            {
                log.Note(Text.T("Sources.Run.Truncated", counts.Read));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state = SyncState.Cancelled;
        }
        catch (Exception ex)
        {
            state = SyncState.Failed;
            error = MessageOf(ex);
        }

        // 2. write: also what was read before an error (only a cancel writes nothing)
        if (state != SyncState.Cancelled)
        {
            try
            {
                await WriteAsync(db, job, records, counts, log, ct);
                if (job.DeleteMissing && state == SyncState.Succeeded)
                {
                    if (!complete)
                    {
                        log.Note(Text.T("Sources.Run.NoDeleteTruncated"));
                    }
                    else
                    {
                        await DeleteMissingAsync(db, job, seen, records.Count, counts, log, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                state = SyncState.Cancelled;
            }
            catch (Exception ex)
            {
                state = SyncState.Failed;
                error ??= MessageOf(ex);
            }
        }

        // 3. the run and the data source
        db.ChangeTracker.Clear();
        var end = DateTime.UtcNow;
        var finished = await db.SyncRuns.FirstAsync(r => r.Id == run.Id, CancellationToken.None);
        finished.State = state;
        finished.EndDate = end;
        finished.RecordsRead = counts.Read;
        finished.Created = counts.Created;
        finished.Updated = counts.Updated;
        finished.Unchanged = counts.Unchanged;
        finished.Deleted = counts.Deleted;
        finished.Skipped = counts.Skipped;
        if (state == SyncState.Cancelled)
        {
            log.Note(Text.T("Sources.Run.Cancelled"));
        }

        finished.Message = log.Compose(error);
        var stored = await db.DataSources.IgnoreQueryFilters().FirstAsync(s => s.Id == job.SourceId, CancellationToken.None);
        stored.LastRunDate = end;
        stored.LastRunState = state;
        stored.LastError = state == SyncState.Succeeded ? null : Cut(finished.Message ?? Text.T("Sources.State." + state), 2000);
        stored.NextRunDate = stored.IntervalMinutes > 0 ? end.AddMinutes(stored.IntervalMinutes) : null;
        if (fields.Names.Count > 0)
        {
            stored.DiscoveredFields = FieldsJson(fields.Names);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        if (trigger == SyncTrigger.Manual || state == SyncState.Failed)
        {
            await ActivityLog.AddAsync(db, actor, "sync", stored.Name, Text.T("Sources.Run.Summary", Text.T("Sources.State." + state), counts.Created, counts.Updated, counts.Deleted),
                stored.Id, CancellationToken.None);
        }

        return finished;
    }

    /// <summary>Creates, updates and revives the contacts of the records.</summary>
    private static async Task WriteAsync(AppDbContext db, Job job, Dictionary<string, MappedRecord> records, Counts counts, RunLog log, CancellationToken ct)
    {
        var existing = await db.Contacts.IgnoreQueryFilters().AsNoTracking().Where(c => c.DataSourceId == job.SourceId && c.ExternalKey != null)
            .Select(c => new Existing(c.Id, c.ExternalKey!, c.SyncHash, c.UpdateState == SoftEntity.Deleted))
            .ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, ct);
        job.Existing = existing;
        var now = DateTime.UtcNow;
        var changes = new List<(long Id, MappedRecord Record, string Hash, bool Revive)>();
        var pending = 0;
        foreach (var (key, mapped) in records)
        {
            ct.ThrowIfCancellationRequested();
            var hash = FieldMapper.Hash(job.AddressBookId, mapped.Values);
            if (existing.TryGetValue(key, out var known))
            {
                if (!known.Deleted && known.Hash == hash)
                {
                    counts.Unchanged++;
                }
                else
                {
                    changes.Add((known.Id, mapped, hash, known.Deleted));
                }

                continue;
            }

            var contact = new Contact { AddressBookId = job.AddressBookId, DataSourceId = job.SourceId, ExternalKey = key, SyncHash = hash, LastSyncDate = now };
            FieldMapper.Write(contact, mapped.Values);
            db.Contacts.Add(contact);
            counts.Created++;
            if (++pending >= BatchSize)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        foreach (var batch in changes.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var ids = batch.Select(b => b.Id).ToList();
            var contacts = await db.Contacts.IgnoreQueryFilters().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            foreach (var (id, mapped, hash, revive) in batch)
            {
                var contact = contacts[id];
                FieldMapper.Write(contact, mapped.Values);
                contact.AddressBookId = job.AddressBookId;
                contact.SyncHash = hash;
                contact.LastSyncDate = now;
                if (revive)
                {
                    // the record is back: the same contact (and Token) as before; Hidden stays as it was
                    contact.UpdateState = SoftEntity.Updated;
                    counts.Created++;
                    log.Add("Revived", contact.ExternalKey ?? string.Empty);
                }
                else
                {
                    counts.Updated++;
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Deletes (softly) the contacts of the source whose key was not read (<paramref name="seen"/>). Nothing when nothing usable was read or more than half of the
    /// records were skipped: an empty answer or a key field that is gone is more likely a fault than the end of every contact.
    /// </summary>
    private static async Task DeleteMissingAsync(AppDbContext db, Job job, HashSet<string> seen, int usable, Counts counts, RunLog log, CancellationToken ct)
    {
        var missing = job.Existing.Values.Where(e => !e.Deleted && !seen.Contains(e.Key)).Select(e => e.Id).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        if (counts.Read == 0)
        {
            log.Note(Text.T("Sources.Run.NoDeleteEmpty", missing.Count));
            return;
        }

        if (usable == 0 || counts.Skipped * 2 > counts.Read)
        {
            log.Note(Text.T("Sources.Run.NoDeleteSkipped", counts.Skipped, counts.Read, missing.Count));
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var batch in missing.Chunk(BatchSize * 2))
        {
            ct.ThrowIfCancellationRequested();
            counts.Deleted += await db.Contacts.Where(c => batch.Contains(c.Id) && c.DataSourceId == job.SourceId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdateState, SoftEntity.Deleted).SetProperty(c => c.UpdateDate, now), ct);
        }

        // a bulk change past the change tracker: the LDAP snapshot and the directory API must know
        DirectoryChanges.Bump();
    }

    /// <summary>The fields of a source as stored in <see cref="DataSource.DiscoveredFields"/>.</summary>
    public static string FieldsJson(IEnumerable<string> fields) => JsonSerializer.Serialize(fields.Take(500).ToList());

    /// <summary>The fields of <see cref="DataSource.DiscoveredFields"/>.</summary>
    public static IReadOnlyList<string> FieldsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The message of a failure for the person: the worded one of a connector, else the type and the text (never a secret: the exceptions of the
    /// connectors carry no connection string or token).</summary>
    public static string MessageOf(Exception ex) => ex switch
    {
        SourceException => ex.Message,
        AggregateException { InnerException: { } inner } => MessageOf(inner),
        DbUpdateException { InnerException: { } inner } => Text.T("Sources.Error.Database", inner.Message),
        _ => Text.T("Sources.Error.Unexpected", ex.GetType().Name, ex.Message),
    };

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>What one run works on (the data source is not tracked while the contacts are written in batches).</summary>
    private sealed record Job(long SourceId, long AddressBookId, string KeyField, bool DeleteMissing, IReadOnlyList<MappingRule> Rules)
    {
        public Dictionary<string, Existing> Existing { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record Existing(long Id, string Key, string? Hash, bool Deleted);

    private sealed class Counts
    {
        public int Read;
        public int Created;
        public int Updated;
        public int Unchanged;
        public int Deleted;
        public int Skipped;
    }

    /// <summary>The warnings of a run: a count and a few examples per reason, and notes; composed into the message, capped.</summary>
    private sealed class RunLog
    {
        private readonly Dictionary<string, (int Count, List<string> Samples)> _reasons = new(StringComparer.Ordinal);
        private readonly List<string> _notes = [];

        public void Add(string reason, string sample)
        {
            if (!_reasons.TryGetValue(reason, out var entry))
            {
                entry = (0, []);
            }

            if (entry.Samples.Count < Samples)
            {
                entry.Samples.Add(sample);
            }

            _reasons[reason] = (entry.Count + 1, entry.Samples);
        }

        public void Note(string note) => _notes.Add(note);

        public string? Compose(string? error)
        {
            var sb = new StringBuilder();
            if (error is not null)
            {
                sb.AppendLine(error);
            }

            foreach (var (reason, (count, samples)) in _reasons)
            {
                sb.AppendLine(Text.T("Sources.Run." + reason, count, string.Join(", ", samples) + (count > samples.Count ? ", …" : string.Empty)));
            }

            foreach (var note in _notes)
            {
                sb.AppendLine(note);
            }

            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : Cut(text, MessageLimit);
        }
    }
}
