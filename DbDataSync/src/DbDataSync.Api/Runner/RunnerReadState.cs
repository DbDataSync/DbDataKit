using System.Globalization;
using DbDataKit.Runner.Client;
using DbDataSync.Api.Controllers;
using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>What a runner holds of one mapping's read state: the parts of its item of the checkpoint <c>watermark</c>.</summary>
/// <param name="Version">The item's version (0: the mapping has no item there yet, which reads as the defaults).</param>
/// <param name="HasPending">A position captured for an initial load waits to be promoted (<c>pending_position</c>).</param>
public sealed record RunnerMappingState(ReadIntent Intent, ReadHold Hold, string? Position, DateTimeOffset? PositionTime, long Version, bool HasPending)
{
    public MappingReadStateDto ToDto() => new(Intent, Hold, Position, PositionTime);
}

/// <summary>What <see cref="RunnerReadState.SetAsync"/> did.</summary>
public enum RunnerReadStateOutcome
{
    Done,

    /// <summary>"Continue from the captured position" was asked for a mapping that has none waiting.</summary>
    NoCapturedPosition,

    /// <summary>A drain kept moving the item while it was being changed; nothing was changed.</summary>
    Moved,
}

/// <summary>
/// The read intent, hold and captured position of a mapping of a replication the runner owns (plan 28c E12g, E22 precondition P3): the drain reads them from its checkpoint <c>watermark</c> on the runner, so the Sync API's
/// <c>read-state</c> calls read and change that item there (the runner's <c>checkpoints/{name}/set</c>, audited under the paired client) in place of its own state, which nothing reads for such a replication. Every change names the
/// version read and is made again from a fresh read when a drain moved the item meanwhile, as <see cref="ChangeWatermarkStore"/> does; the old path's refusal while a pass of the mapping runs is not made here, because a continuous
/// drain is always up and the version check is what keeps a pass from being overwritten (a pass acts on what it read when it began, as it always did).
/// </summary>
public sealed class RunnerReadState(RunnerAccess runner)
{
    private const int Attempts = 5;
    private static readonly string[] Pending = [WatermarkCheckpoint.PendingPosition, WatermarkCheckpoint.PendingPositionTime, WatermarkCheckpoint.PendingBatch];

    private IRunnerSync Require() => runner.Open() ?? throw new RunnerUnavailableException(runner.Link is null
        ? $"No runner is linked, so the read state of a replication it owns cannot be read or changed ({runner.File.Path})."
        : "The linked runner cannot be asked for the read state of its mappings.");

    /// <summary>The mapping's state on the runner; a mapping with no item reads as <paramref name="defaultIntent"/> and no hold. A runner that cannot be asked is thrown as <see cref="RunnerUnavailableException"/>.</summary>
    public async Task<RunnerMappingState> ReadAsync(string replication, string mapping, string sourceTable, ReadIntent defaultIntent, CancellationToken ct)
    {
        IReadOnlyList<ClientCheckpointItem> items;
        try { items = await Require().CheckpointAsync(RunnerAccess.TaskOf(replication), WatermarkCheckpoint.Name, ct).ConfigureAwait(false); }
        catch (RunnerClientException ex) { throw new RunnerUnavailableException(ex.Message, ex); }

        var key = WatermarkCheckpoint.ItemKey(mapping, sourceTable);
        return items.FirstOrDefault(i => i.Key == key) is { } item ? Shape(item, defaultIntent) : new RunnerMappingState(defaultIntent, ReadHold.None, null, null, 0, false);
    }

    private static RunnerMappingState Shape(ClientCheckpointItem item, ReadIntent defaultIntent)
    {
        var parts = item.Parts;
        var intent = parts.TryGetValue(WatermarkCheckpoint.Intent, out var i) && Enum.TryParse<ReadIntent>(i, out var parsedIntent) ? parsedIntent : defaultIntent;
        var hold = parts.TryGetValue(WatermarkCheckpoint.Hold, out var h) && Enum.TryParse<ReadHold>(h, out var parsedHold) ? parsedHold : ReadHold.None;
        DateTimeOffset? time = parts.TryGetValue(WatermarkCheckpoint.PositionTime, out var t) && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return new RunnerMappingState(intent, hold, parts.GetValueOrDefault(WatermarkCheckpoint.Position), time, item.Version, parts.ContainsKey(WatermarkCheckpoint.PendingPosition));
    }

    private static ClientPartValue Text(string value) => new("text", value);

    /// <summary>
    /// Sets the mapping's intent and hold, or (with <paramref name="useCapturedPosition"/>) promotes its captured position: the same changes the old path makes in its own state (<see cref="TableMappingsController"/>). Leaving
    /// <see cref="ReadHold.Loading"/> by hand abandons the load's pending position. Returns what happened with the hold the mapping had before and the state after.
    /// </summary>
    public async Task<(RunnerReadStateOutcome Outcome, ReadHold PreviousHold, RunnerMappingState After)> SetAsync(
        string replication, string mapping, string sourceTable, ReadIntent defaultIntent, ReadIntent intent, ReadHold hold, bool useCapturedPosition, CancellationToken ct)
    {
        var sync = Require();
        var task = RunnerAccess.TaskOf(replication);
        var key = WatermarkCheckpoint.ItemKey(mapping, sourceTable);
        RunnerMappingState before = null!;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            before = await ReadAsync(replication, mapping, sourceTable, defaultIntent, ct).ConfigureAwait(false);
            var set = new Dictionary<string, ClientPartValue>(StringComparer.Ordinal);
            var remove = new List<string>();
            if (useCapturedPosition)
            {
                if (before.Hold != ReadHold.Loading || !before.HasPending) return (RunnerReadStateOutcome.NoCapturedPosition, before.Hold, before);
                // the pending parts are read back as text; the position time travels as a time
                var items = await sync.CheckpointAsync(task, WatermarkCheckpoint.Name, ct).ConfigureAwait(false);
                var parts = items.First(i => i.Key == key).Parts;
                set[WatermarkCheckpoint.Position] = Text(parts[WatermarkCheckpoint.PendingPosition]);
                if (parts.TryGetValue(WatermarkCheckpoint.PendingPositionTime, out var pendingTime)) set[WatermarkCheckpoint.PositionTime] = new ClientPartValue("time", pendingTime);
                else remove.Add(WatermarkCheckpoint.PositionTime);
                set[WatermarkCheckpoint.Intent] = Text(nameof(ReadIntent.Changes));
                set[WatermarkCheckpoint.Hold] = Text(nameof(ReadHold.None));
                remove.AddRange(Pending);
            }
            else
            {
                set[WatermarkCheckpoint.Intent] = Text(intent.ToString());
                set[WatermarkCheckpoint.Hold] = Text(hold.ToString());
                // leaving Loading by hand: the batch it waited on is abandoned and must not promote its old position over this choice
                if (before.Hold == ReadHold.Loading && hold != ReadHold.Loading) remove.AddRange(Pending);
            }

            long? version;
            try { version = await sync.SetCheckpointAsync(task, WatermarkCheckpoint.Name, key, before.Version, set, remove, ct).ConfigureAwait(false); }
            catch (RunnerClientException ex) { throw new RunnerUnavailableException(ex.Message, ex); }
            if (version is not null)
                return (RunnerReadStateOutcome.Done, before.Hold, await ReadAsync(replication, mapping, sourceTable, defaultIntent, ct).ConfigureAwait(false));
        }

        return (RunnerReadStateOutcome.Moved, before.Hold, before);
    }
}
