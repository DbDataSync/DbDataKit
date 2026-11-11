using System.Diagnostics;
using System.Text.Json;
using DbDataBuild.State;
using DbDataKit.State.Tracking;

namespace DbDataBuild.Execution;

/// <summary>
/// <see cref="BuildTrackingCopier"/> for a process that is not where the copy is (plan 28c E16, the runner's task): the target is read through <see cref="BuildTrackingSource"/> with the login the run holds, and the copy,
/// its checkpoints and its divergences are the runner's, reached through <see cref="IRemoteTrackingCopy"/> (the runner protocol). Rows and checkpoint move in one transaction on the runner. The same <see cref="TrackingFollower"/>
/// does the copying, the rows of <c>ddl_log</c> and <c>run_log</c> copied before their step ended are compared with the target again until they settle, and the two snapshots are rebuilt from the copy's own metadata documents
/// (<see cref="BuildTrackingSnapshots"/>, the function the local copier uses). It never throws: a down side is an error text in the result.
/// </summary>
/// <param name="infoRows">The rows of <c>object_info</c> a document says; null for a kind of subject that has none. Given by the host, which owns that reading.</param>
public sealed class RemoteBuildTrackingCopier(string identity, BuildTrackingSource source, IRemoteTrackingCopy copy, Func<string, string, IReadOnlyList<InfoRow>?> infoRows, TrackingFollowerOptions? options = null, int reconcileLimit = 200)
{
    public string Identity => identity;

    public TrackingFollower Follower(string stream) => new(identity, BuildStreams.Get(stream).Stream, source, copy, options);

    public async Task<BuildTrackingResult> RunAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<(string, TrackingRunResult)>();
        var reconciled = 0;
        var metadataCopied = 0;
        try
        {
            foreach (var stream in BuildStreams.All)
            {
                var remaining = budget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                var result = await Follower(stream.Name).CatchUpAsync(remaining, cancellationToken).ConfigureAwait(false);
                results.Add((stream.Name, result));
                if (stream.Name == "metadata_document") metadataCopied = result.Copied;
                if (stream.Kind == BuildStreamKind.Updated && !result.Diverged) reconciled += await ReconcileAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            var rebuilt = false;
            if (results.Any(r => r.Item1 == "metadata_document" && r.Item2.Error is null && !r.Item2.Diverged)
                && (metadataCopied > 0 || await IsEmptyAsync("object_info", cancellationToken).ConfigureAwait(false) && !await IsEmptyAsync("metadata_document", cancellationToken).ConfigureAwait(false)))
            {
                await RebuildSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                rebuilt = true;
            }

            return new BuildTrackingResult(results, reconciled, rebuilt, null);
        }
        catch (OperationCanceledException)
        {
            return new BuildTrackingResult(results, reconciled, false, "The copy was cancelled.");
        }
        catch (Exception exception)
        {
            return new BuildTrackingResult(results, reconciled, false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task<bool> IsEmptyAsync(string stream, CancellationToken cancellationToken) =>
        (await copy.TailAsync(identity, new TrackingStream(stream, ["k", "o"], "o", "k"), 1, cancellationToken).ConfigureAwait(false)).Count == 0;

    private async Task<int> ReconcileAsync(BuildStream stream, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var (key, hash) in await copy.UnsettledAsync(identity, stream.Name, reconcileLimit, cancellationToken).ConfigureAwait(false))
        {
            var document = await source.ReadDocumentAsync(stream.Name, key, cancellationToken).ConfigureAwait(false);
            if (document is null || BuildStreams.RowHash(document) == hash) continue;
            await copy.RewriteAsync(identity, stream.Name, [new TrackingRow(key, BuildStreams.OrderOf(stream, document), document)], cancellationToken).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }

    /// <summary>Makes <c>object_info</c> and <c>metadata_dependency</c> in the runner's copy what the copy's newest documents say.</summary>
    public async Task RebuildSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var latest = new Dictionary<(string Kind, string Subject), (long Order, string Json)>();
        TrackingPosition? after = null;
        while (true)
        {
            var batch = await copy.ReadAsync(identity, "metadata_document", after, 500, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                using var parsed = JsonDocument.Parse(row.Document);
                latest[(parsed.RootElement.GetProperty("subject_kind").GetString()!, parsed.RootElement.GetProperty("subject").GetString()!)] = (row.Order, parsed.RootElement.GetProperty("document").GetString()!);
            }

            after = new TrackingPosition(batch[^1].Order, batch[^1].Key);
            if (batch.Count < 500) break;
        }

        var (info, dependencies) = BuildTrackingSnapshots.Compute(latest, infoRows);
        await copy.ReplaceSnapshotAsync(identity, "object_info", [.. info.Select(r => new TrackingRow(r.Key, r.Order, r.Document))], cancellationToken).ConfigureAwait(false);
        await copy.ReplaceSnapshotAsync(identity, "metadata_dependency", [.. dependencies.Select(r => new TrackingRow(r.Key, r.Order, r.Document))], cancellationToken).ConfigureAwait(false);
    }
}
