using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;

namespace DbDataSync.Planning;

/// <summary>One item of the drain's checkpoint as the runner holds it: its key (the engine's watermark key of a source table) and its parts as text (times as ISO 8601 in UTC).</summary>
public sealed record CheckpointItem(string Key, IReadOnlyDictionary<string, string> Parts);

/// <summary>A hand-over of positions that was refused; the message says what was found.</summary>
public sealed class PositionHandOverException(string message) : InvalidOperationException(message);

/// <summary>
/// The positions a replication moves between Sync's own state and a runner's drain (plan 28c E12, E12c): <see cref="SeedsFor"/> reads them from Sync's state as the requests that seed the drain's first run (cutover),
/// and <see cref="CopyBack"/> writes what the drain left (the checkpoint <c>watermark</c>: position, time, intent and hold) into Sync's state (rollback), so the one that takes over reads on from where the other stopped and no row is read twice.
/// </summary>
public sealed class RunnerPositions(ConfigRepository config, ChangeWatermarkStore watermarks, DriverRegistry drivers)
{
    /// <summary>The checkpoint of the drain's positions: one item per source table, keyed by the engine's watermark key.</summary>
    public const string WatermarkCheckpoint = "watermark";

    public static void RefuseLoading(IReadOnlyList<CheckpointItem> items)
    {
        foreach (var item in items)
        {
            if (item.Parts.TryGetValue("pending_position", out var pending) && pending.Length > 0 || item.Parts.TryGetValue("hold", out var hold) && hold == nameof(ReadHold.Loading))
                throw new PositionHandOverException($"The source table '{item.Key}' is loading on the runner (its initial load is a batch of the runner's queue); let it finish before the rollback. The owner is as it was.");
        }
    }

    /// <summary>Writes the positions, intents and holds the drain left on the runner as the mappings' state here; the number of mappings written. A key no mapping has is not this replication's and is left.</summary>
    public int CopyBack(ReplicationTaskConfig task, IReadOnlyList<CheckpointItem> items)
    {
        var byKey = items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var copied = 0;
        foreach (var name in config.ListTableMappings(task.Name))
        {
            var mapping = config.LoadTableMapping(task.Name, name);
            if (mapping.Sources.Count != 1) continue;
            var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            var dialect = (drivers.Get(config.LoadConnection(source.ConnectionName).DriverType) as IDialectProvider)?.Dialect
                ?? throw new PositionHandOverException($"The driver of the connection '{source.ConnectionName}' names no SQL dialect.");
            var key = WatermarkKey.Build(source, dialect);
            if (!byKey.TryGetValue(key, out var item)) continue;
            if (item.Parts.TryGetValue("position", out var position) && position.Length > 0)
            {
                DateTimeOffset? time = item.Parts.TryGetValue("position_time", out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                watermarks.SetWatermark(task.Name, name, key, position, time);
            }

            var intent = item.Parts.TryGetValue("intent", out var i) && Enum.TryParse<ReadIntent>(i, out var parsedIntent) ? parsedIntent : ReadIntent.Changes;
            var holdNow = item.Parts.TryGetValue("hold", out var h) && Enum.TryParse<ReadHold>(h, out var parsedHold) ? parsedHold : ReadHold.None;
            watermarks.SetReadIntentAndHold(task.Name, name, key, intent, holdNow);
            copied++;
        }

        return copied;
    }


    /// <summary>The positions this API holds for the mappings, as the requests that seed a drain that has none. A mapping that is held for a load (no position yet, or one in progress) is refused: its batch lives in this API's queue.</summary>
    public List<DrainRequest> SeedsFor(ReplicationTaskConfig task, IReadOnlyList<string> mappings)
    {
        var seeds = new List<DrainRequest>();
        foreach (var name in mappings)
        {
            var mapping = config.LoadTableMapping(task.Name, name);
            if (mapping.Sources.Count != 1) continue;
            var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
            var dialect = (drivers.Get(config.LoadConnection(source.ConnectionName).DriverType) as IDialectProvider)?.Dialect
                ?? throw new PositionHandOverException($"The driver of the connection '{source.ConnectionName}' names no SQL dialect.");
            var key = WatermarkKey.Build(source, dialect);
            var state = watermarks.GetReadState(task.Name, name, key);
            if (state?.Hold == ReadHold.Loading)
                throw new PositionHandOverException($"The mapping '{name}' is loading (its initial load is a batch of this API's queue); let it finish before the move. The owner is as it was.");
            if (state?.Watermark is { Length: > 0 } position)
                seeds.Add(new DrainRequest("Position", name, SourceKey: key, Position: position, PositionTime: state.WatermarkTimeUtc, Intent: state.Intent.ToString(), Hold: state.Hold.ToString()));
        }

        return seeds;
    }
}
