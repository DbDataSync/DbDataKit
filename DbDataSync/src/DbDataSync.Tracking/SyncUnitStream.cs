using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State.Tracking;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.Tracking;

/// <summary>
/// The one stream of Sync's tracking (plan 28c E17): the units a replication's passes recorded in <c>dbdatasync.unit</c> on its target. The ordering key is the table's identity column
/// (<c>unit_seq</c>, strictly growing, so the stream is append-only) and the row key is the unit's own id. A row travels as a JSON document of every column; the copy keeps it in the shared
/// <c>tracking</c> module (<see cref="TrackingModule"/>) of Sync's state, under the identity <see cref="Identity"/> makes.
/// </summary>
public static class SyncUnitStream
{
    public const string Name = SyncUnitCopy.Name;

    /// <summary>The stream's definition is shared with the runner (<see cref="SyncUnitCopy"/>), which writes the copy of a runner-owned replication.</summary>
    public static TrackingStream Stream => SyncUnitCopy.Stream;

    /// <summary>The progress store's key: where the follower's checkpoints are (the same store the runner's positions use, a key of its own).</summary>
    public static DbDataKit.State.ProgressKey ProgressKey { get; } = new(SyncState.ServiceId, SyncState.DefaultGroup, "tracking");

    /// <summary>The identity a replication's units are copied under: which target (engine, server, database), which replication and which connection of Sync, so that a development copy of a target is never the same set of rows.</summary>
    public static string Identity(UnitStateEngine engine, string server, string database, string connection, string replication) =>
        $"syncunit:{TargetUnitState.EngineWord(engine)}://{server}/{database}/{replication}@{connection}";

    /// <summary>The replication an identity of this stream is for (the part between the last <c>/</c> and the <c>@</c>).</summary>
    public static string ReplicationOf(string identity) => SyncUnitCopy.ReplicationOf(identity);

    public static string ConnectionOf(string identity) => SyncUnitCopy.ConnectionOf(identity);

    public static string Document(TargetUnitRecord unit) => new JsonObject
    {
        ["unit_seq"] = unit.Sequence,
        ["unit_id"] = unit.UnitId.ToString("D"),
        ["replication"] = unit.Replication,
        ["mapping"] = unit.Mapping,
        ["watermark_key"] = unit.WatermarkKey,
        ["watermark_before"] = unit.WatermarkBefore,
        ["watermark_after"] = unit.WatermarkAfter,
        ["watermark_time"] = unit.WatermarkTime?.ToString("O", CultureInfo.InvariantCulture),
        ["rows_staged"] = unit.RowsStaged,
        ["run_id"] = unit.RunId?.ToString("D"),
        ["committed_at"] = unit.CommittedAt.ToString("O", CultureInfo.InvariantCulture),
    }.ToJsonString();

    public static TrackingRow ToRow(TargetUnitRecord unit) => new(unit.UnitId.ToString("D"), unit.Sequence, Document(unit));

    /// <summary>When the unit was committed on the target, read back from a row's document (null when the document does not say).</summary>
    public static DateTimeOffset? CommittedAt(string document) => SyncUnitCopy.CommittedAt(document);
}
