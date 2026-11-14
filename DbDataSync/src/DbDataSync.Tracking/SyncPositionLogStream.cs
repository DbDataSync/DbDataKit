using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State.Tracking;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.Tracking;

/// <summary>
/// The one stream of Sync's tracking (plan 28c E17): the entries a replication's passes recorded in <c>dbdatasync.position_log</c> on its target. The ordering key is the table's identity column
/// (<c>entry_seq</c>, strictly growing, so the stream is append-only) and the row key is the entry's own id. A row travels as a JSON document of every column; the copy keeps it in the shared
/// <c>tracking</c> module (<see cref="TrackingModule"/>) of Sync's state, under the identity <see cref="Identity"/> makes.
/// </summary>
public static class SyncPositionLogStream
{
    public const string Name = SyncPositionLogCopy.Name;

    /// <summary>The stream's definition is shared with the runner (<see cref="SyncPositionLogCopy"/>), which writes the copy of a runner-owned replication.</summary>
    public static TrackingStream Stream => SyncPositionLogCopy.Stream;

    /// <summary>The progress store's key: where the follower's checkpoints are (the same store the runner's positions use, a key of its own).</summary>
    public static DbDataKit.State.ProgressKey ProgressKey { get; } = new(SyncState.ServiceId, SyncState.DefaultGroup, "tracking");

    /// <summary>The identity a replication's entries are copied under: which target (engine, server, database), which replication and which connection of Sync, so that a development copy of a target is never the same set of rows.</summary>
    public static string Identity(PositionLogEngine engine, string server, string database, string connection, string replication) =>
        $"syncposition:{TargetPositionLog.EngineWord(engine)}://{server}/{database}/{replication}@{connection}";

    /// <summary>The replication an identity of this stream is for (the part between the last <c>/</c> and the <c>@</c>).</summary>
    public static string ReplicationOf(string identity) => SyncPositionLogCopy.ReplicationOf(identity);

    public static string ConnectionOf(string identity) => SyncPositionLogCopy.ConnectionOf(identity);

    public static string Document(PositionLogEntry entry) => new JsonObject
    {
        ["entry_seq"] = entry.Sequence,
        ["entry_id"] = entry.EntryId.ToString("D"),
        ["replication"] = entry.Replication,
        ["mapping"] = entry.Mapping,
        ["watermark_key"] = entry.WatermarkKey,
        ["watermark_before"] = entry.WatermarkBefore,
        ["watermark_after"] = entry.WatermarkAfter,
        ["watermark_time"] = entry.WatermarkTime?.ToString("O", CultureInfo.InvariantCulture),
        ["rows_staged"] = entry.RowsStaged,
        ["run_id"] = entry.RunId?.ToString("D"),
        ["committed_at"] = entry.CommittedAt.ToString("O", CultureInfo.InvariantCulture),
    }.ToJsonString();

    public static TrackingRow ToRow(PositionLogEntry entry) => new(entry.EntryId.ToString("D"), entry.Sequence, Document(entry));

    /// <summary>When the entry was committed on the target, read back from a row's document (null when the document does not say).</summary>
    public static DateTimeOffset? CommittedAt(string document) => SyncPositionLogCopy.CommittedAt(document);
}
