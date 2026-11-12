using System.Globalization;
using DbDataKit.DataModel;
using DbDataKit.Runner.Kit.Formats;
using DbDataKit.Store;

namespace DbDataKit.Runner.Kit.Landing;

/// <summary>Where a landing into DbDataStore's tables was refused: the first record that breaks a rule (when it can be told from the others) and every rule it broke, in the Store's own shape.</summary>
/// <param name="FirstRecord">The number (from 1) of the first record of the batch that was refused.</param>
/// <param name="LastRecord">The last record of that batch: nothing of the records from <paramref name="FirstRecord"/> to here was written.</param>
/// <param name="BadRecord">The first record of the batch that breaks a rule alone, or null when each is fine alone and they break a rule together (two with one key).</param>
/// <param name="Violations">The rules broken (the rule's name, the attribute and the Store's sentence): of the bad record when one was found, else of the batch.</param>
public sealed record StoreRefusal(long FirstRecord, long LastRecord, long? BadRecord, IReadOnlyList<RuleViolation> Violations);

/// <summary>What a landing into the Store did.</summary>
/// <param name="Inserted">New rows.</param>
/// <param name="Updated">Existing rows whose values differed.</param>
/// <param name="Unchanged">Rows the Store already held with these values: not written, no new version, no audit row.</param>
/// <param name="Through">How many records are settled: every record up to here is in the Store (a refused batch stops it). A rerun gives the same records again and settles the rest.</param>
/// <param name="Refused">Why it stopped, or null when every record landed.</param>
public sealed record StoreLanding(long Inserted, long Updated, long Unchanged, long Through, StoreRefusal? Refused)
{
    public bool Complete => Refused is null;
}

public sealed record StoreLandOptions
{
    /// <summary>Records in one write: one transaction of the Store, so all of the batch or none. A smaller batch means a finer "last good point".</summary>
    public int BatchRecords { get; init; } = 200;
}

/// <summary>
/// Lands records into an entity of a DbDataStore model through the Store's own write path, <see cref="ModelWriter"/> (plan 27a IX11, in process: the library that holds the model and a connection to the database it lives in, no server between): every rule, version check, history and audit row
/// of a write by a person applies, and the actor is the integration's. Rows are matched by the entity's key: a key the Store does not have is inserted, one it has with other values is updated (only the attributes the record names), one it has with the same values is left alone, so
/// **landing the same records again changes nothing**. A batch is one transaction. A batch the Store refuses stops the landing at the last good point: the records before it are in, the refusal names the rule and the first bad record, nothing after it was tried, and a rerun with the same
/// records (fixed or not) settles the rest. A refused write never shows a value of the record beyond what the Store's own sentence says.
/// </summary>
public sealed class StoreLander
{
    private readonly ModelWriter _writer;
    private readonly ModelEntity _entity;
    private readonly WriteActor _actor;
    private readonly StoreLandOptions _o;

    public StoreLander(ModelWriter writer, DbDataKit.DataModel.DataModel model, string entity, WriteActor actor, StoreLandOptions? options = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentNullException.ThrowIfNull(model);
        _entity = model.Entities.FirstOrDefault(e => e.Name == entity) ?? throw new ArgumentException($"The model '{model.Name}' has no entity '{entity}'.", nameof(entity));
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _o = options ?? new StoreLandOptions();
        if (_o.BatchRecords < 1) throw new ArgumentOutOfRangeException(nameof(options), "A batch holds at least one record.");
    }

    /// <summary>Lands records given as attribute values (typed as the Store reads them: numbers, <see cref="DateTimeOffset"/>, text).</summary>
    public async Task<StoreLanding> LandAsync(IAsyncEnumerable<IReadOnlyDictionary<string, object?>> records, CancellationToken ct = default)
    {
        long inserted = 0, updated = 0, unchanged = 0, number = 0, through = 0;
        var batch = new List<(long Number, RowChange? Change, int Kind)>();
        await foreach (var record in records.WithCancellation(ct).ConfigureAwait(false))
        {
            number++;
            var (change, kind) = await ChangeFor(record, ct).ConfigureAwait(false);
            batch.Add((number, change, kind));
            if (batch.Count(b => b.Change is not null) >= _o.BatchRecords && await FlushAsync().ConfigureAwait(false) is { } refused)
                return new StoreLanding(inserted, updated, unchanged, through, refused);
        }

        var last = await FlushAsync().ConfigureAwait(false);
        return new StoreLanding(inserted, updated, unchanged, through, last);

        async Task<StoreRefusal?> FlushAsync()
        {
            if (batch.Count == 0) return null;
            var writes = batch.Where(b => b.Change is not null).ToList();
            if (writes.Count > 0)
            {
                try
                {
                    await _writer.ApplyAsync([.. writes.Select(w => w.Change!)], _actor, ct).ConfigureAwait(false);
                }
                catch (WriteRefusedException ex)
                {
                    var (bad, violations) = await FindBadAsync([.. writes.Select(w => (w.Number, w.Change!))], ex.Violations, ct).ConfigureAwait(false);
                    return new StoreRefusal(batch[0].Number, batch[^1].Number, bad, violations);
                }
            }

            inserted += writes.Count(w => w.Kind == 1);
            updated += writes.Count(w => w.Kind == 2);
            unchanged += batch.Count - writes.Count;
            through = batch[^1].Number;
            batch.Clear();
            return null;
        }
    }

    /// <summary>Lands the rows of a reader of <c>DbDataKit.Runner.Kit.Formats</c>: each column named like an attribute is passed as the file gave it (the Store refuses a value that is not of the attribute's type, naming the rule); other columns are left out.</summary>
    public Task<StoreLanding> LandAsync(IAsyncEnumerable<Row> rows, CancellationToken ct = default) => LandAsync(AsRecords(rows, ct), ct);

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> AsRecords(IAsyncEnumerable<Row> rows, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>();
            foreach (var attribute in _entity.Attributes)
                if (row.Has(attribute.Name)) values[attribute.Name] = row[attribute.Name];
            yield return values;
        }
    }

    private async Task<(RowChange? Change, int Kind)> ChangeFor(IReadOnlyDictionary<string, object?> record, CancellationToken ct)
    {
        var key = new Dictionary<string, object?>();
        foreach (var name in _entity.Key)
        {
            if (!record.TryGetValue(name, out var value) || value is null) return (RowChange.Insert(_entity.Name, record), 1);     // the Store names the missing key as a rule
            key[name] = value;
        }

        StoredRow? existing;
        try { existing = await _writer.GetAsync(_entity.Name, key, ct).ConfigureAwait(false); }
        catch (WriteRefusedException) { return (RowChange.Insert(_entity.Name, record), 1); }                                      // a key of the wrong type: the write says which rule
        if (existing is null) return (RowChange.Insert(_entity.Name, record), 1);
        var changes = new Dictionary<string, object?>();
        foreach (var (name, value) in record)
        {
            if (_entity.Key.Contains(name)) continue;
            if (!existing.Values.TryGetValue(name, out var stored) || !Same(stored, value)) changes[name] = value;
        }

        return changes.Count == 0 ? (null, 0) : (RowChange.Update(_entity.Name, key, existing.Version, changes), 2);
    }

    private async Task<(long? Bad, IReadOnlyList<RuleViolation> Violations)> FindBadAsync(List<(long Number, RowChange Change)> changes, IReadOnlyList<RuleViolation> batch, CancellationToken ct)
    {
        foreach (var (number, change) in changes)
        {
            try { await _writer.ValidateAsync([change], _actor, ct).ConfigureAwait(false); }
            catch (WriteRefusedException ex) { return (number, ex.Violations); }
        }

        return (null, batch);
    }

    private static bool Same(object? stored, object? given)
    {
        if (stored is null || given is null) return stored is null && given is null;
        if (stored.Equals(given)) return true;
        if (IsNumber(stored) && IsNumber(given)) return Convert.ToDecimal(stored, CultureInfo.InvariantCulture) == Convert.ToDecimal(given, CultureInfo.InvariantCulture);
        if (stored is DateTimeOffset s && given is DateTime g) return s.UtcDateTime == g.ToUniversalTime();
        if (stored is DateTime t && given is DateTimeOffset o) return t.ToUniversalTime() == o.UtcDateTime;
        return false;
    }

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double;
}
