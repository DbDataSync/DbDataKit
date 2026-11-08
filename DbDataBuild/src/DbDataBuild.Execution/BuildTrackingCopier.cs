using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.State.Tracking;

namespace DbDataBuild.Execution;

/// <summary>What one run of the copier did, stream by stream. A value, never an exception: the code that asked for a copy goes on whatever it says.</summary>
public sealed record BuildTrackingResult(IReadOnlyList<(string Stream, TrackingRunResult Result)> Streams, int Reconciled, bool SnapshotsRebuilt, string? Error)
{
    public int Copied => Streams.Sum(s => s.Result.Copied);

    public bool CaughtUp => Error is null && Streams.Count == BuildStreams.All.Count && Streams.All(s => s.Result.CaughtUp);

    public IReadOnlyList<string> Problems => [.. Streams.Where(s => s.Result.Error is not null).Select(s => $"{s.Stream}: {s.Result.Error}"), .. Streams.Where(s => s.Result.Diverged).Select(s => $"{s.Stream}: diverged ({s.Result.Divergence?.Reason})"), .. Error is null ? [] : new[] { Error }];

    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Keeps the copy of one identity's tracking tables up to date (plan 28c E16): one <see cref="TrackingFollower"/> for each stream of <see cref="BuildStreams.All"/>, the rows of <c>ddl_log</c> and <c>run_log</c> that were copied before their step ended
/// compared with the target again until they settle, and the two snapshot tables (<c>object_info</c>, <c>metadata_dependency</c>) rebuilt from the copy's own metadata documents when new documents came. It writes only to the state database, reads the target
/// only through the read session, and never fails the caller: a down side is an error text in the result, and the run is bounded by a time budget (the follower's own: a side that blocks is abandoned at it).
/// </summary>
/// <param name="infoRows">The rows of <c>object_info</c> a document says (the same reading the publish uses); null for a kind of subject that has none. Given by the host, which owns that reading.</param>
public sealed class BuildTrackingCopier(string identity, BuildTrackingSource source, BuildTrackingStore store, Func<string, string, IReadOnlyList<InfoRow>?> infoRows, TrackingFollowerOptions? options = null, DbDataKit.State.AuditLog? audit = null, int reconcileLimit = 200)
{
    private readonly ProgressStore _progress = new(store.Database, BuildTrackingState.ProgressKey);
    private readonly StateTrackingDivergences _divergences = new(store.Database);

    public string Identity => identity;

    /// <summary>The follower of one stream (for resolving a divergence, switching the primary, or looking at a checkpoint).</summary>
    public TrackingFollower Follower(string stream) => new(identity, BuildStreams.Get(stream).Stream, source, store, _progress, _divergences, options, audit);

    public IReadOnlyList<TrackingFollower> Followers() => [.. BuildStreams.All.Select(s => Follower(s.Name))];

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
            if (results.Any(r => r.Item1 == "metadata_document" && r.Item2.Error is null && !r.Item2.Diverged) && (metadataCopied > 0 || store.Count(identity, "object_info") == 0 && store.Count(identity, "metadata_document") > 0))
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

    /// <summary>Compares the rows the copy holds as unsettled with the target's and writes the ones that changed (the newest <c>reconcileLimit</c> only, so a run stays bounded).</summary>
    private async Task<int> ReconcileAsync(BuildStream stream, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var (key, hash) in store.Unsettled(identity, stream.Name, reconcileLimit))
        {
            var document = await source.ReadDocumentAsync(stream.Name, key, cancellationToken).ConfigureAwait(false);
            if (document is null || BuildStreams.RowHash(document) == hash) continue;
            await store.WriteAsync(identity, stream.Stream, [new TrackingRow(key, BuildStreams.OrderOf(stream, document), document)], cancellationToken).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Makes <c>object_info</c> and <c>metadata_dependency</c> in the copy what the copy's newest documents say (rows of a subject that has a newer document are gone with the old document), the way a publish makes them on the target.
    /// </summary>
    public async Task RebuildSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var latest = new Dictionary<(string Kind, string Subject), (long Order, string Json)>();
        var metadata = new TrackingStream("metadata_document", ["k", "o"], "o", "k");
        TrackingPosition? after = null;
        while (true)
        {
            var batch = await store.ReadAfterAsync(identity, metadata, after, 500, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                using var parsed = JsonDocument.Parse(row.Document);
                var kind = parsed.RootElement.GetProperty("subject_kind").GetString()!;
                var subject = parsed.RootElement.GetProperty("subject").GetString()!;
                latest[(kind, subject)] = (row.Order, parsed.RootElement.GetProperty("document").GetString()!);
            }

            after = new TrackingPosition(batch[^1].Order, batch[^1].Key);
            if (batch.Count < 500) break;
        }

        var info = new List<(string, long, string)>();
        foreach (var ((kind, subject), (order, json)) in latest.OrderBy(l => l.Key.Kind, StringComparer.Ordinal).ThenBy(l => l.Key.Subject, StringComparer.Ordinal))
        {
            if (infoRows(kind, json) is not { } rows) continue;
            foreach (var r in rows)
            {
                var values = new Dictionary<string, object?>
                {
                    ["subject_kind"] = kind, ["subject"] = subject, ["element"] = r.Element, ["item"] = r.Item, ["item_key"] = r.Key,
                    ["value_type"] = r.ValueType, ["value"] = r.Value, ["recorded_utc"] = BuildStreams.TimeOf(order),
                };
                info.Add(($"{kind}|{subject}|{r.Element}|{r.Item}|{r.Key}", order, BuildStreams.SnapshotDocument("object_info", values)));
            }
        }

        var newest = latest.Count == 0 ? 0 : latest.Values.Max(v => v.Order);
        var dependencies = MetadataDependencies.FromDocuments(latest.Select(l => (l.Key.Kind, l.Key.Subject, l.Value.Json)))
            .Select(d => ($"{d.Subject}|{d.DependsOn}", newest, BuildStreams.SnapshotDocument("metadata_dependency", new Dictionary<string, object?>
            {
                ["subject"] = d.Subject, ["depends_on"] = d.DependsOn, ["depends_on_category"] = d.DependsOnCategory, ["depends_on_kind"] = d.DependsOnKind, ["depth"] = d.Depth,
            }))).ToList();

        store.ReplaceSnapshot(identity, "object_info", [.. info.OrderBy(r => r.Item1, StringComparer.Ordinal)]);
        store.ReplaceSnapshot(identity, "metadata_dependency", [.. dependencies.OrderBy(r => r.Item1, StringComparer.Ordinal)]);
    }
}
