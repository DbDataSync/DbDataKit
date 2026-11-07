using System.Collections.Concurrent;
using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.State;

/// <summary>Where one mapping's last completed pass got to, and when the source says that position
/// committed — see phase 87.</summary>
/// <param name="Watermark">
/// Null for a mapping that has an intent stored but has not yet completed a pass under it — phase 100's
/// <c>ChangesFromEarliest</c>, say, before its first read. Every existing caller already treats "no
/// applied position" as "nothing to measure from" (see <c>ReaderLagService</c>), which is exactly the
/// right reading here too.
/// </param>
/// <param name="WatermarkTimeUtc">
/// Null for three distinguishable-in-cause but identical-in-answer reasons: the row was written
/// before phase 87 added the column and the mapping has not run since, its reader has no
/// position-to-time mapping at all, or the engine declined to place the position on the pass that
/// stored it. All three mean the same thing to a report — no figure yet — which is why they are one
/// null rather than a state code.
/// </param>
public sealed record AppliedPosition(string? Watermark, DateTimeOffset? WatermarkTimeUtc);

/// <summary>
/// A mapping's full stored row — what it should do next, whether it is allowed to, and where it got to —
/// read together for the reason <see cref="AppliedPosition"/> already pairs a position with its time:
/// three separate reads of the same row could straddle a pass that ran between them. See phase 100.
/// </summary>
/// <param name="Intent">This mapping's own stored intent. There is no fallback here — a mapping with no
/// row at all (nothing in <see cref="ChangeWatermarkStore.GetReadState"/>) is the case
/// <c>ReadIntentResolution.Default</c> answers instead; once a row exists, its own value always wins.</param>
public sealed record MappingReadState(ReadIntent Intent, ReadHold Hold, string? Watermark, DateTimeOffset? WatermarkTimeUtc);

/// <summary>
/// Where each table mapping's incremental read got to — one current value per <c>(task, mapping, source table)</c>, kept as a declared checkpoint of the progress store (plan 28c, slices E14 and E15): the
/// checkpoint <see cref="WatermarkCheckpoint.Name"/> of the <c>runner_progress</c> module, kept per mapping and source table, with the seven parts of <see cref="WatermarkCheckpoint"/>. Every write is an
/// <c>Advance</c> that names the version it read, so a position that was moved by another writer in between is read again rather than overwritten blindly, and every change is a row of the checkpoint's history
/// (the console's "rewind a mapping to Monday").
/// <para>
/// **The mapping is part of the key, not the table alone.** A replication may have any number of table mappings pointing at the same physical source table, and nothing requires them to read it the same way.
/// Two that disagree — one on Change Tracking and one on the generic Watermark reader, or two generic ones with different <c>watermarkColumn</c> options — were previously one row, each pass overwriting a
/// position the other could not interpret (phase 74). The table stays in the key beside it: it is what makes a mapping repointed at a different source table start from the beginning rather than resume from a
/// position belonging to a table it no longer reads.
/// </para>
/// </summary>
public sealed class ChangeWatermarkStore(StateDatabase database)
{
    private const int MaxAttempts = 200;

    /// <summary>Who is recorded as having moved a position when the caller names nobody: the engine, a worker or the API.</summary>
    public const string DefaultActor = "dbdatasync";

    private readonly ConcurrentDictionary<string, ProgressStore> _stores = new(StringComparer.Ordinal);

    private ProgressStore For(string taskName) => _stores.GetOrAdd(taskName, task => new ProgressStore(database, WatermarkCheckpoint.KeyFor(task)));

    private static IReadOnlyDictionary<string, ProgressValue> Without(IReadOnlyDictionary<string, ProgressValue> parts, params string[] names) =>
        parts.Where(p => !names.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>
    /// Reads the mapping's checkpoint, lets <paramref name="change"/> say its new parts, and advances it from the version read; when another writer moved it first, reads again and applies the change to
    /// what is there now (a position is last writer wins, as it always was: the writers are made exclusive by the run lock, and this is the belt for the case where they were not).
    /// <paramref name="change"/> returns null for "nothing to do".
    /// </summary>
    private bool Move(string taskName, string mappingName, string sourceTable, string actor, Func<Dictionary<string, ProgressValue>, bool, Dictionary<string, ProgressValue>?> change)
    {
        var store = For(taskName);
        var item = WatermarkCheckpoint.ItemKey(mappingName, sourceTable);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = database.Retry(() => store.GetCheckpoint(WatermarkCheckpoint.Name, item));
            var parts = current is null ? new Dictionary<string, ProgressValue>(StringComparer.Ordinal) : new Dictionary<string, ProgressValue>(current.Parts, StringComparer.Ordinal);
            if (current is null)
            {
                // a new mapping is read as the defaults say until something else is written
                parts[WatermarkCheckpoint.Intent] = ProgressValue.OfText(nameof(ReadIntent.Changes));
                parts[WatermarkCheckpoint.Hold] = ProgressValue.OfText(nameof(ReadHold.None));
            }

            if (change(parts, current is not null) is not { } next)
                return false;
            try
            {
                if (database.Retry(() => store.Advance(WatermarkCheckpoint.Name, next, current?.Version ?? 0, itemKey: item, actor: actor)))
                    return true;
            }
            catch (DbException) when (current is null)
            {
                // two writers made the same new mapping at once and the other one won the insert: read it again and apply the change to what is there now
            }

            // refused: another writer moved it between the read and the advance. A short, uneven pause keeps writers that keep colliding from colliding in step.
            Thread.Sleep(Random.Shared.Next(0, 4 * Math.Min(attempt + 1, 8)));
        }

        throw new InvalidOperationException($"The position of '{mappingName}' ({sourceTable}) of '{taskName}' kept moving while it was being written; it was not changed.");
    }

    /// <param name="watermarkTimeUtc">
    /// When the source says <paramref name="watermark"/> committed, where the reader that produced it could say — see phase 87. **In this call rather than a call of its own**, because a position and its
    /// commit time are one fact: written separately they could end up describing two different passes, and a lag figure computed from a mismatched pair is confidently wrong rather than merely absent.
    /// Overwritten on every pass including a null one, so a reader that stops being able to state a time clears the stale one instead of leaving it to be read as current.
    /// </param>
    public void SetWatermark(
        string taskName,
        string mappingName,
        string sourceTable,
        string watermark,
        DateTimeOffset? watermarkTimeUtc = null) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, _) =>
        {
            parts[WatermarkCheckpoint.Position] = ProgressValue.OfText(watermark);
            if (watermarkTimeUtc is { } time) parts[WatermarkCheckpoint.PositionTime] = ProgressValue.OfTime(time);
            else parts.Remove(WatermarkCheckpoint.PositionTime);
            return parts;
        });

    private Checkpoint? Read(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() => For(taskName).GetCheckpoint(WatermarkCheckpoint.Name, WatermarkCheckpoint.ItemKey(mappingName, sourceTable)));

    private static string? Text(Checkpoint checkpoint, string part) => checkpoint.Parts.TryGetValue(part, out var v) ? v.AsText : null;

    private static DateTimeOffset? Time(Checkpoint checkpoint, string part) => checkpoint.Parts.TryGetValue(part, out var v) ? v.AsTime : null;

    /// <summary>The position and its commit time, or null for a mapping that has no row at all (a row that has only an intent or a hold has no position: <see cref="AppliedPosition.Watermark"/> is null).</summary>
    public AppliedPosition? GetAppliedPosition(string taskName, string mappingName, string sourceTable) =>
        Read(taskName, mappingName, sourceTable) is { } checkpoint
            ? new AppliedPosition(Text(checkpoint, WatermarkCheckpoint.Position), Time(checkpoint, WatermarkCheckpoint.PositionTime))
            : null;

    public string? GetWatermark(string taskName, string mappingName, string sourceTable) =>
        Read(taskName, mappingName, sourceTable) is { } checkpoint ? Text(checkpoint, WatermarkCheckpoint.Position) : null;

    /// <summary>A mapping's whole stored state, read together so that three reads cannot straddle a pass that ran between them (phase 100); null for a mapping with no row.</summary>
    public MappingReadState? GetReadState(string taskName, string mappingName, string sourceTable) =>
        Read(taskName, mappingName, sourceTable) is { } checkpoint
            ? new MappingReadState(
                Enum.Parse<ReadIntent>(Text(checkpoint, WatermarkCheckpoint.Intent) ?? nameof(ReadIntent.Changes)),
                Enum.Parse<ReadHold>(Text(checkpoint, WatermarkCheckpoint.Hold) ?? nameof(ReadHold.None)),
                Text(checkpoint, WatermarkCheckpoint.Position),
                Time(checkpoint, WatermarkCheckpoint.PositionTime))
            : null;

    public void SetReadIntent(string taskName, string mappingName, string sourceTable, ReadIntent intent) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, _) =>
        {
            parts[WatermarkCheckpoint.Intent] = ProgressValue.OfText(intent.ToString());
            return parts;
        });

    public void SetReadHold(string taskName, string mappingName, string sourceTable, ReadHold hold) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, _) =>
        {
            parts[WatermarkCheckpoint.Hold] = ProgressValue.OfText(hold.ToString());
            return parts;
        });

    public void SetReadIntentAndHold(string taskName, string mappingName, string sourceTable, ReadIntent intent, ReadHold hold) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, _) =>
        {
            parts[WatermarkCheckpoint.Intent] = ProgressValue.OfText(intent.ToString());
            parts[WatermarkCheckpoint.Hold] = ProgressValue.OfText(hold.ToString());
            return parts;
        });

    /// <summary>Records a position captured for an initial load that is not yet promoted, and holds the mapping while it loads (<see cref="ReadHold.Loading"/>). The position, intent and hold the mapping had stay as they were.</summary>
    public void SetPendingLoad(
        string taskName, string mappingName, string sourceTable,
        string pendingWatermark, DateTimeOffset? pendingWatermarkTimeUtc, string bulkLoadBatchId) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, _) =>
        {
            parts[WatermarkCheckpoint.Hold] = ProgressValue.OfText(nameof(ReadHold.Loading));
            parts[WatermarkCheckpoint.PendingPosition] = ProgressValue.OfText(pendingWatermark);
            if (pendingWatermarkTimeUtc is { } time) parts[WatermarkCheckpoint.PendingPositionTime] = ProgressValue.OfTime(time);
            else parts.Remove(WatermarkCheckpoint.PendingPositionTime);
            parts[WatermarkCheckpoint.PendingBatch] = ProgressValue.OfText(bulkLoadBatchId);
            return parts;
        });

    /// <summary>Moves the pending parts to the position and clears them, in one advance: the intent is <see cref="ReadIntent.Changes"/> and the hold <see cref="ReadHold.None"/> from then on.</summary>
    private static Dictionary<string, ProgressValue> Promoted(Dictionary<string, ProgressValue> parts)
    {
        parts[WatermarkCheckpoint.Position] = parts[WatermarkCheckpoint.PendingPosition];
        if (parts.TryGetValue(WatermarkCheckpoint.PendingPositionTime, out var time)) parts[WatermarkCheckpoint.PositionTime] = time;
        else parts.Remove(WatermarkCheckpoint.PositionTime);
        parts[WatermarkCheckpoint.Intent] = ProgressValue.OfText(nameof(ReadIntent.Changes));
        parts[WatermarkCheckpoint.Hold] = ProgressValue.OfText(nameof(ReadHold.None));
        parts.Remove(WatermarkCheckpoint.PendingPosition);
        parts.Remove(WatermarkCheckpoint.PendingPositionTime);
        parts.Remove(WatermarkCheckpoint.PendingBatch);
        return parts;
    }

    /// <summary>Promotes a mapping's pending initial load when it is still held for loading and has a pending position (one moved the hold elsewhere in the meantime: nothing happens). True when it was promoted.</summary>
    public bool PromotePendingLoadFor(string taskName, string mappingName, string sourceTable) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, exists) =>
            exists && parts.TryGetValue(WatermarkCheckpoint.Hold, out var hold) && hold.AsText == nameof(ReadHold.Loading) && parts.ContainsKey(WatermarkCheckpoint.PendingPosition)
                ? Promoted(parts)
                : null);

    public void ClearPendingLoad(string taskName, string mappingName, string sourceTable) =>
        Move(taskName, mappingName, sourceTable, DefaultActor, (parts, exists) =>
            exists ? new Dictionary<string, ProgressValue>(Without(parts, WatermarkCheckpoint.PendingPosition, WatermarkCheckpoint.PendingPositionTime, WatermarkCheckpoint.PendingBatch), StringComparer.Ordinal) : null);

    /// <summary>
    /// Promotes the mapping whose pending load is the bulk-load batch <paramref name="bulkLoadBatchId"/>: its pending position becomes its position, in one advance. True when exactly one mapping was waiting on
    /// the batch (a batch belongs to one mapping).
    /// </summary>
    public bool PromotePendingLoad(string bulkLoadBatchId)
    {
        var waiting = database.Retry(() => ProgressStore.FindTextPart(database, WatermarkCheckpoint.Package, WatermarkCheckpoint.Channel, WatermarkCheckpoint.Name, WatermarkCheckpoint.PendingBatch, bulkLoadBatchId));
        var promoted = 0;
        foreach (var (task, item) in waiting)
        {
            var (mapping, table) = WatermarkCheckpoint.Split(item);
            if (Move(task, mapping, table, DefaultActor, (parts, exists) => exists && parts.ContainsKey(WatermarkCheckpoint.PendingPosition) ? Promoted(parts) : null))
                promoted++;
        }

        return promoted == 1;
    }

    /// <summary>Removes a mapping's position altogether, so its next pass starts as a new mapping would; the history of the checkpoint stays, naming <paramref name="actor"/>. False when it had none.</summary>
    public bool ClearWatermark(string taskName, string mappingName, string sourceTable, string? actor = null) =>
        database.Retry(() => For(taskName).DeleteCheckpoint(WatermarkCheckpoint.Name, actor ?? DefaultActor, WatermarkCheckpoint.ItemKey(mappingName, sourceTable)));

    // ---------- E15: the version, the strict advance, the history and the rewind ----------

    /// <summary>The version of a mapping's checkpoint (how many times it has been advanced), 0 when it has none: what <see cref="TryAdvancePosition"/> asks for.</summary>
    public long Version(string taskName, string mappingName, string sourceTable) => Read(taskName, mappingName, sourceTable)?.Version ?? 0;

    /// <summary>
    /// Moves a mapping's position to <paramref name="position"/> only if the checkpoint is still at <paramref name="expectedVersion"/> (0: it does not exist yet), in the same transaction as
    /// <paramref name="alsoWrite"/> (which is where a run's outcome is written, so the position and the outcome stand or fall together: a callback that fails leaves the position where it was). False when
    /// another writer moved it first and nothing was written: the caller reads it again. This is the advance an engine makes after the target write committed (<see cref="SetWatermark"/> is the same move
    /// without the check, for the callers that were made exclusive by the run lock).
    /// </summary>
    public bool TryAdvancePosition(
        string taskName, string mappingName, string sourceTable, long expectedVersion, string position, DateTimeOffset? positionTime,
        string actor, Guid? runId = null, Action<DbConnection, DbTransaction>? alsoWrite = null)
    {
        var current = Read(taskName, mappingName, sourceTable);
        if ((current?.Version ?? 0) != expectedVersion)
            return false;

        var parts = current is null
            ? new Dictionary<string, ProgressValue>(StringComparer.Ordinal)
            {
                [WatermarkCheckpoint.Intent] = ProgressValue.OfText(nameof(ReadIntent.Changes)),
                [WatermarkCheckpoint.Hold] = ProgressValue.OfText(nameof(ReadHold.None)),
            }
            : new Dictionary<string, ProgressValue>(current.Parts, StringComparer.Ordinal);
        parts[WatermarkCheckpoint.Position] = ProgressValue.OfText(position);
        if (positionTime is { } time) parts[WatermarkCheckpoint.PositionTime] = ProgressValue.OfTime(time);
        else parts.Remove(WatermarkCheckpoint.PositionTime);

        return database.Retry(() => For(taskName).Advance(WatermarkCheckpoint.Name, parts, expectedVersion, runId, WatermarkCheckpoint.ItemKey(mappingName, sourceTable), actor, alsoWrite));
    }

    /// <summary>The changes to a mapping's position, intent, hold and pending load, newest first, each naming who made it (a run, or a person's rewind).</summary>
    public IReadOnlyList<CheckpointChange> History(string taskName, string mappingName, string sourceTable, int limit = 100) =>
        database.Retry(() => For(taskName).History(WatermarkCheckpoint.Name, WatermarkCheckpoint.ItemKey(mappingName, sourceTable), limit));

    /// <summary>
    /// Rewinds a mapping to what it was before the change <paramref name="historyId"/> of its <see cref="History"/> (and so before every later one): "reload everything since Monday". A rewind is a new
    /// version with <paramref name="actor"/> in its history, so it can itself be undone, and the next pass reads from the earlier position again. False when another writer moved the mapping meanwhile,
    /// or the change is not this mapping's.
    /// </summary>
    public bool Rewind(string taskName, string mappingName, string sourceTable, long historyId, string actor) =>
        database.Retry(() => For(taskName).Rewind(WatermarkCheckpoint.Name, historyId, actor, WatermarkCheckpoint.ItemKey(mappingName, sourceTable)));
}
