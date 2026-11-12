using System.Text.Json;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;

namespace DbDataBuild.Execution;

/// <summary>
/// The journal of one tracking store (plan 28c E16, <c>tracking: { primary: state }</c>): what the writers of tracking rows hand it first becomes the same documents the follower would copy (<see cref="BuildStreams"/>) and goes to the state
/// through its sink, before the target statement runs. An update needs the row it changes: the journal keeps the documents it wrote in this process, and reads the row back from the state when it did not write it (a step <c>started</c> by an earlier
/// process, as <c>apply --resume</c> finishes it), by paging through the stream. A new <c>metadata_document</c> rebuilds the two snapshots in the state from the state's documents, as a publish does on the target.
/// </summary>
public sealed class BuildTrackingJournal(string identity, IBuildTrackingSink sink, Func<string, string, IReadOnlyList<InfoRow>?>? infoRows = null) : ITrackingJournal
{
    private readonly Dictionary<(string Stream, string Key), (long Order, string Document)> _written = [];
    private readonly Dictionary<string, long> _planOrder = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string Identity => identity;

    public async Task InsertAsync(string table, IReadOnlyDictionary<string, object?> columns, CancellationToken cancellationToken)
    {
        if (!Streams.TryGet(table, out var stream)) return;
        var document = BuildStreams.Document(stream, columns);
        var key = BuildStreams.KeyOf(stream, columns);
        var order = await OrderOfInsertAsync(stream, columns, cancellationToken).ConfigureAwait(false);
        lock (_lock) _written[(stream.Name, key)] = (order, document);
        await sink.WriteAsync(identity, stream.Name, [new TrackingRow(key, order, document)], cancellationToken).ConfigureAwait(false);
        if (stream.Name == "migration_log" && columns.GetValueOrDefault("plan_id") is { } plan) lock (_lock) _planOrder.TryAdd(plan.ToString()!.Trim(), order);
        if (stream.Name == "metadata_document" && infoRows is not null) await RebuildSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(string table, IReadOnlyDictionary<string, object?> keyColumns, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken)
    {
        if (!Streams.TryGet(table, out var stream)) return;
        var key = BuildStreams.KeyOf(stream, keyColumns);
        (long Order, string Document) current;
        bool known;
        lock (_lock) known = _written.TryGetValue((stream.Name, key), out current);
        if (!known)
        {
            var found = await FindAsync(stream, key, cancellationToken).ConfigureAwait(false);
            if (found is null) throw new InvalidOperationException($"The state holds no row '{key}' of `{stream.Name}` for this tracking store, so the update cannot be recorded there first. The row was not written by the state-first journal (a plan started before the project took the state as its primary?).");
            current = found.Value;
        }

        var document = BuildStreams.Merge(stream, current.Document, changes);
        lock (_lock) _written[(stream.Name, key)] = (current.Order, document);
        await sink.WriteAsync(identity, stream.Name, [new TrackingRow(key, current.Order, document)], cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> OrderOfInsertAsync(BuildStream stream, IReadOnlyDictionary<string, object?> columns, CancellationToken cancellationToken)
    {
        if (stream.Name != "refresh_plan_member") return BuildStreams.Order(columns.GetValueOrDefault(stream.OrderColumn) ?? throw new InvalidOperationException($"A row of `{stream.Name}` has no `{stream.OrderColumn}`."));
        // a plan's members are ordered by the time of the plan's first migration_log row, as the target's stream reads them
        var plan = columns["plan_id"]!.ToString()!.Trim();
        lock (_lock) if (_planOrder.TryGetValue(plan, out var known)) return known;
        TrackingPosition? after = null;
        long? first = null;
        while (true)
        {
            var batch = await sink.ReadAsync(identity, "migration_log", after, 500, cancellationToken).ConfigureAwait(false);
            foreach (var row in batch)
            {
                using var parsed = JsonDocument.Parse(row.Document);
                if (parsed.RootElement.GetProperty("plan_id").GetString() == plan) first = first is null ? row.Order : Math.Min(first.Value, row.Order);
            }

            if (batch.Count < 500) break;
            after = new TrackingPosition(batch[^1].Order, batch[^1].Key);
        }

        var order = first ?? BuildStreams.Order(DateTime.UtcNow);
        lock (_lock) _planOrder[plan] = order;
        return order;
    }

    private async Task<(long Order, string Document)?> FindAsync(BuildStream stream, string key, CancellationToken cancellationToken)
    {
        TrackingPosition? after = null;
        while (true)
        {
            var batch = await sink.ReadAsync(identity, stream.Name, after, 500, cancellationToken).ConfigureAwait(false);
            foreach (var row in batch) if (row.Key == key) return (row.Order, row.Document);
            if (batch.Count < 500) return null;
            after = new TrackingPosition(batch[^1].Order, batch[^1].Key);
        }
    }

    private async Task RebuildSnapshotsAsync(CancellationToken cancellationToken)
    {
        var latest = new Dictionary<(string Kind, string Subject), (long Order, string Json)>();
        TrackingPosition? after = null;
        while (true)
        {
            var batch = await sink.ReadAsync(identity, "metadata_document", after, 500, cancellationToken).ConfigureAwait(false);
            foreach (var row in batch)
            {
                using var parsed = JsonDocument.Parse(row.Document);
                latest[(parsed.RootElement.GetProperty("subject_kind").GetString()!, parsed.RootElement.GetProperty("subject").GetString()!)] = (row.Order, parsed.RootElement.GetProperty("document").GetString()!);
            }

            if (batch.Count < 500) break;
            after = new TrackingPosition(batch[^1].Order, batch[^1].Key);
        }

        var (info, dependencies) = BuildTrackingSnapshots.Compute(latest, infoRows!);
        await sink.ReplaceSnapshotAsync(identity, "object_info", [.. info.Select(r => new TrackingRow(r.Key, r.Order, r.Document))], cancellationToken).ConfigureAwait(false);
        await sink.ReplaceSnapshotAsync(identity, "metadata_dependency", [.. dependencies.Select(r => new TrackingRow(r.Key, r.Order, r.Document))], cancellationToken).ConfigureAwait(false);
    }

    private static class Streams
    {
        public static bool TryGet(string table, out BuildStream stream)
        {
            stream = BuildStreams.All.FirstOrDefault(s => s.Name == table)!;
            // tracking_version is written by the init script, and the two snapshots are rebuilt from the documents: neither is a row the writers hand over
            return stream is not null && table != "tracking_version";
        }
    }
}
