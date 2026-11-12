using System.Globalization;
using DbDataKit.Runner.Client;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>
/// The bulk loads (batch reloads and initial loads in segments) of a replication the runner owns, for the Monitoring card and the history (plan 28c E22 precondition, E13 item 5). A drain keeps each batch as an item of its
/// checkpoint <c>batch</c> (key: the batch id; parts: <c>mapping</c>, <c>segments</c>, <c>succeeded</c>, <c>failed</c>, <c>rows_read</c>, <c>rows_written</c>, <c>estimated_rows</c>, <c>estimate_caveat</c>, <c>created_at</c>),
/// which is read here and shaped as <see cref="BulkLoadBatchProgress"/>. What the runner does not keep is how many segments are running and when the first began or the last ended: those are 0 and null here, and the card shows
/// the batch's creation time and its counts.
/// </summary>
public sealed class RunnerBulkLoads(RunnerAccess runner)
{
    public const string CheckpointName = "batch";

    private IRunnerSync Require() => runner.Open() ?? throw new RunnerUnavailableException(runner.Link is null
        ? $"No runner is linked, so the bulk loads of a replication it owns cannot be read ({runner.File.Path})."
        : "The linked runner cannot be asked for its bulk loads.");

    /// <summary>The batches the runner holds for the replication, newest first, those of one mapping when <paramref name="mappingName"/> is given; a runner that cannot be asked is thrown as <see cref="RunnerUnavailableException"/>, never answered as none.</summary>
    public async Task<IReadOnlyList<BulkLoadBatchProgress>> ListAsync(string replication, string? mappingName, CancellationToken ct)
    {
        IReadOnlyList<ClientCheckpointItem> items;
        try { items = await Require().CheckpointAsync(RunnerAccess.TaskOf(replication), CheckpointName, ct).ConfigureAwait(false); }
        catch (RunnerClientException ex) { throw new RunnerUnavailableException(ex.Message, ex); }

        return [.. items.Select(Shape).OfType<BulkLoadBatchProgress>()
            .Where(b => mappingName is null || b.MappingName == mappingName)
            .OrderByDescending(b => b.CreatedAtUtc).ThenByDescending(b => b.BatchId, StringComparer.Ordinal)];
    }

    /// <summary>One item of the checkpoint as a batch; null when it has no segment count (not a batch of this shape).</summary>
    public static BulkLoadBatchProgress? Shape(ClientCheckpointItem item)
    {
        var parts = item.Parts;
        if (!TryLong(parts, "segments", out var segments)) return null;
        _ = TryLong(parts, "succeeded", out var succeeded);
        _ = TryLong(parts, "failed", out var failed);
        _ = TryLong(parts, "rows_read", out var read);
        _ = TryLong(parts, "rows_written", out var written);
        long? estimate = TryLong(parts, "estimated_rows", out var estimated) ? estimated : null;
        var created = parts.TryGetValue("created_at", out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
        return new BulkLoadBatchProgress(
            item.Key, parts.GetValueOrDefault("mapping") ?? "", created, (int)segments, (int)succeeded, (int)failed, 0, read, written, estimate,
            parts.TryGetValue("estimate_caveat", out var caveat) && caveat.Length > 0 ? caveat : null, created == DateTimeOffset.MinValue ? null : created, null);
    }

    private static bool TryLong(IReadOnlyDictionary<string, string> parts, string name, out long value)
    {
        value = 0;
        return parts.TryGetValue(name, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
