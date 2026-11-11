using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.State.Tracking;

/// <summary>How a follower works. The defaults suit a deploy that calls it after every apply.</summary>
public sealed record TrackingFollowerOptions
{
    /// <summary>Rows read and written at once (bounded: a long outage is caught up in many small steps, each its own transaction and checkpoint move).</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Batches one run copies before it stops and reports not caught up, so a run is bounded even when the copy is far behind.</summary>
    public int MaxBatchesPerRun { get; init; } = 20;

    /// <summary>The longest one run waits for the two sides together. A side that blocks past it is abandoned (its call is cancelled and, when it ignores that, left behind) and the run reports an error.</summary>
    public TimeSpan RunTimeLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many of the newest keys of each side are compared to find a copy that holds rows the primary does not.</summary>
    public int TailSize { get; init; } = 50;
}

/// <summary>
/// Copies the rows a primary side added to a stream onto the other side (plan 29 section 4a.3), for one connection <b>identity</b>. The follower's own position is a checkpoint in the progress store
/// (<see cref="ProgressStore.Advance"/>, guarded by its <c>row_version</c>): where the copy is a state sink in the progress store's database, the checkpoint moves in the <b>same transaction</b> as the rows are
/// written; otherwise the rows are written first, idempotently, and the checkpoint moves after, so a crash only repeats work.
/// <para>
/// It never throws into the code that asked for a copy: a side that is down, slow or refusing is a <see cref="TrackingRunResult.Error"/>, and a copy that parts from its primary is
/// marked diverged (<see cref="ITrackingDivergenceStore"/>) and left alone until <see cref="ResolveAsync"/>. Everything it reads and writes is for the one identity it was made for: it
/// never reads, copies or marks another connection's rows.
/// </para>
/// </summary>
public sealed class TrackingFollower
{
    private readonly ITrackingSource _primary;
    private readonly ITrackingSink _copy;
    private readonly ProgressStore _progress;
    private readonly ITrackingDivergenceStore _divergences;
    private readonly TrackingFollowerOptions _options;
    private readonly AuditLog? _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrackingFollower(string identity, TrackingStream stream, ITrackingSource primary, ITrackingSink copy, ProgressStore progress, ITrackingDivergenceStore divergences,
        TrackingFollowerOptions? options = null, AuditLog? audit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(divergences);
        Identity = identity;
        Stream = stream;
        _primary = primary;
        _copy = copy;
        _progress = progress;
        _divergences = divergences;
        _options = options ?? new TrackingFollowerOptions();
        _audit = audit;
        if (_options.BatchSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "A batch has at least one row.");
        if (_options.MaxBatchesPerRun < 1) throw new ArgumentOutOfRangeException(nameof(options), "A run copies at least one batch.");
        if (_options.TailSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "The comparison looks at least one key.");
    }

    public string Identity { get; }

    public TrackingStream Stream { get; }

    private string CheckpointName => $"tracking.{Stream.Name}";

    private const string OrderPart = "order";
    private const string KeyPart = "key";

    /// <summary>The follower's checkpoint: the last position copied, or null before the first row.</summary>
    public TrackingPosition? Position => ToPosition(_progress.GetCheckpoint(CheckpointName, Identity));

    private static TrackingPosition? ToPosition(Checkpoint? checkpoint) =>
        checkpoint is not null && checkpoint.Parts.TryGetValue(OrderPart, out var order) && checkpoint.Parts.TryGetValue(KeyPart, out var key) ? new TrackingPosition(order.AsInt, key.AsText) : null;

    /// <summary>The open divergence of this connection and stream, or null.</summary>
    public TrackingDivergence? Divergence => _divergences.Open(Identity, Stream.Name);

    // ---------- running ----------

    /// <summary>
    /// Copies what the primary added since the checkpoint, up to <see cref="TrackingFollowerOptions.MaxBatchesPerRun"/> batches and within <see cref="TrackingFollowerOptions.RunTimeLimit"/>. Always returns a result,
    /// never throws: this is the call a deploy makes.
    /// </summary>
    public Task<TrackingRunResult> RunOnceAsync(CancellationToken cancellationToken = default) => RunBoundedAsync(_options.RunTimeLimit, cancellationToken);

    /// <summary>
    /// Runs until the copy is caught up, the stream is diverged, a side fails, or <paramref name="timeBudget"/> is spent, whichever comes first, and returns at the budget even when a side is blocked. A budget of zero
    /// or less does no work and returns a not-caught-up result at once: the answer for a caller that will not wait at all.
    /// </summary>
    public async Task<TrackingRunResult> CatchUpAsync(TimeSpan timeBudget, CancellationToken cancellationToken = default)
    {
        if (timeBudget <= TimeSpan.Zero) return new TrackingRunResult(0, false, null);
        var clock = Stopwatch.StartNew();
        var copied = 0;
        while (true)
        {
            var remaining = timeBudget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) return new TrackingRunResult(copied, false, null);
            var result = await RunBoundedAsync(remaining < _options.RunTimeLimit ? remaining : _options.RunTimeLimit, cancellationToken).ConfigureAwait(false);
            copied += result.Copied;
            if (result.CaughtUp || result.Error is not null || result.Diverged) return result with { Copied = copied };
        }
    }

    private async Task<TrackingRunResult> RunBoundedAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        if (!_gate.Wait(0))
            return new TrackingRunResult(0, false, "An earlier run of this stream is still going (a side did not answer in time); it will finish or be cancelled before another starts.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);
        var progress = new RunProgress();
        Task<TrackingRunResult> work;
        try
        {
            work = Task.Run(() => CopyAsync(progress, cts.Token), CancellationToken.None);
        }
        catch (Exception)
        {
            _gate.Release();
            throw;
        }

        // the gate is held until the work itself ends, even when the caller stopped waiting for it: two runs of one follower never overlap
        _ = work.ContinueWith(_ => _gate.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            return await work.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            return new TrackingRunResult(progress.Copied, false, $"A side of the copy of '{Stream.Name}' did not answer within {limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds.");
        }
        catch (OperationCanceledException)
        {
            cts.Cancel();
            return new TrackingRunResult(progress.Copied, false, "The copy was cancelled.");
        }
        catch (Exception exception)
        {
            return new TrackingRunResult(progress.Copied, false, Describe(exception));
        }
    }

    private sealed class RunProgress
    {
        private int _copied;

        public int Copied => Volatile.Read(ref _copied);

        public void Add(int count) => Interlocked.Add(ref _copied, count);
    }

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

    private async Task<TrackingRunResult> CopyAsync(RunProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            if (_divergences.Open(Identity, Stream.Name) is { } open)
                return new TrackingRunResult(0, false, null, Diverged: true, open);

            var checkpoint = _progress.GetCheckpoint(CheckpointName, Identity);
            var version = checkpoint?.Version ?? 0;
            var position = ToPosition(checkpoint);

            if (await FindDivergenceAsync(position, cancellationToken).ConfigureAwait(false) is { } found)
                return new TrackingRunResult(0, false, null, Diverged: true, found);

            for (var batch = 0; batch < _options.MaxBatchesPerRun; batch++)
            {
                var rows = await _primary.ReadAfterAsync(Identity, Stream, position, _options.BatchSize, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0) return new TrackingRunResult(progress.Copied, true, null);

                var last = rows[^1];
                var parts = new Dictionary<string, ProgressValue> { [OrderPart] = ProgressValue.OfInt(last.Order), [KeyPart] = ProgressValue.OfText(last.Key) };
                bool moved;
                if (_copy is IStateTrackingSink state && ReferenceEquals(state.Database, _progress.Database))
                {
                    // the rows and the checkpoint are one transaction: both land or neither does
                    cancellationToken.ThrowIfCancellationRequested();
                    moved = _progress.Advance(CheckpointName, parts, version, itemKey: Identity, actor: "tracking-follower", alsoWrite: (connection, transaction) => state.Write(connection, transaction, Identity, Stream, rows));
                }
                else
                {
                    // the copy is somewhere else: its write is idempotent and durable before the checkpoint moves, so a crash between the two only repeats the write
                    await _copy.WriteAsync(Identity, Stream, rows, cancellationToken).ConfigureAwait(false);
                    moved = _progress.Advance(CheckpointName, parts, version, itemKey: Identity, actor: "tracking-follower");
                }

                if (!moved)
                    return new TrackingRunResult(progress.Copied, false, "Another follower moved this stream's checkpoint first; the next run reads it again.");

                progress.Add(rows.Count);
                version++;
                position = new TrackingPosition(last.Order, last.Key);
                if (rows.Count < _options.BatchSize) return new TrackingRunResult(progress.Copied, true, null);
            }

            return new TrackingRunResult(progress.Copied, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TrackingRunResult(progress.Copied, false, "The copy was cancelled or ran out of time.");
        }
        catch (Exception exception)
        {
            return new TrackingRunResult(progress.Copied, false, Describe(exception));
        }
    }

    // ---------- divergence ----------

    /// <summary>
    /// Looks for the two ways a copy parts from its primary: the primary's newest row is <i>behind</i> the checkpoint (a restored backup), or the copy holds rows the primary does not (the newest
    /// <see cref="TrackingFollowerOptions.TailSize"/> keys of each are compared as a digest). Marks the stream and returns the divergence when it finds one.
    /// </summary>
    private async Task<TrackingDivergence?> FindDivergenceAsync(TrackingPosition? checkpoint, CancellationToken cancellationToken)
    {
        var primaryHigh = await _primary.HighWaterMarkAsync(Identity, Stream, cancellationToken).ConfigureAwait(false);
        var copyTail = await _copy.TailAsync(Identity, Stream, _options.TailSize, cancellationToken).ConfigureAwait(false);
        var copyHigh = copyTail.Count > 0 ? copyTail[^1] : null;

        var primaryTail = copyTail.Count > 0 ? await _primary.TailAsync(Identity, Stream, _options.TailSize, cancellationToken).ConfigureAwait(false) : [];
        var differ = copyTail.Count > 0 ? Compare(copyTail, primaryTail) : null;

        if (checkpoint is not null && (primaryHigh is null || primaryHigh.Order < checkpoint.Order))
        {
            var reason = primaryHigh is null
                ? $"The primary has no rows of '{Stream.Name}' for this connection, but the copy was copied up to order {checkpoint.Order}: the primary was restored or replaced."
                : $"The primary's newest row of '{Stream.Name}' (order {primaryHigh.Order}) is behind the follower's checkpoint (order {checkpoint.Order}): the primary was restored from a backup.";
            // the common point is only ever one both sides were seen to hold: what the tails agree on, or the primary's newest row when the copy has it too; otherwise none (conservative)
            var common = differ?.Common ?? (primaryHigh is not null && copyTail.Contains(primaryHigh) ? primaryHigh : null);
            return _divergences.Mark(Identity, Stream.Name, reason, common, primaryHigh, copyHigh ?? checkpoint);
        }

        if (differ is null) return null;
        return _divergences.Mark(Identity, Stream.Name, $"The copy holds rows of '{Stream.Name}' that the primary does not (digest of the copy's newest keys {Digest(differ.CopyWindow)}, the primary's {Digest(differ.PrimaryWindow)}).", differ.Common, primaryHigh, copyHigh);
    }

    private sealed record Difference(TrackingPosition? Common, IReadOnlyList<TrackingPosition> CopyWindow, IReadOnlyList<TrackingPosition> PrimaryWindow);

    /// <summary>
    /// The copy's newest keys must be a prefix of what the primary holds: a copy that is merely behind is. Both lists are the newest N, oldest first, so they are compared from the first ordering value
    /// of the primary's list that follows its start (the start itself may cut a run of equal ordering values in the middle).
    /// </summary>
    private static Difference? Compare(IReadOnlyList<TrackingPosition> copy, IReadOnlyList<TrackingPosition> primary)
    {
        if (primary.Count == 0) return new Difference(null, copy, primary);

        var boundary = primary[0].Order;
        var primarySet = primary.ToHashSet();
        TrackingPosition? CommonBy(IEnumerable<TrackingPosition> candidates)
        {
            TrackingPosition? common = null;
            foreach (var candidate in candidates)
            {
                if (!primarySet.Contains(candidate)) break;
                common = candidate;
            }

            return common;
        }

        // the copy's window starts after the primary's: it holds more than the primary can account for
        if (copy[0].Order > boundary) return new Difference(CommonBy(copy), copy, primary);

        var copyAfter = copy.Where(p => p.Order > boundary).ToList();
        var primaryAfter = primary.Where(p => p.Order > boundary).ToList();
        var matched = 0;
        while (matched < copyAfter.Count && matched < primaryAfter.Count && copyAfter[matched] == primaryAfter[matched]) matched++;
        if (matched == copyAfter.Count) return null;

        var common = matched > 0 ? copyAfter[matched - 1] : copy.LastOrDefault(p => p.Order <= boundary && primarySet.Contains(p));
        return new Difference(common, copyAfter, primaryAfter);
    }

    private static string Digest(IReadOnlyList<TrackingPosition> window)
    {
        var text = string.Join('\n', window.Select(p => $"{p.Order}\t{p.Key}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    // ---------- resolving ----------

    /// <summary>
    /// Resolves the open divergence as a recorded action. <see cref="TrackingResolution.Archive"/> keeps the copy's later rows under an archive marker (out of the live stream);
    /// <see cref="TrackingResolution.ResetCopy"/> deletes them. Either way the checkpoint goes back to the common point and the next run copies the primary's rows from there. Returns false when nothing
    /// was diverged. The steps are each safe to repeat, and the divergence is closed last, so a failure part way is resolved again by calling this again. Written to the audit when one was given.
    /// </summary>
    public async Task<bool> ResolveAsync(TrackingResolution resolution, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (_divergences.Open(Identity, Stream.Name) is not { } divergence) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var marker = $"divergence-{divergence.Id.ToString(CultureInfo.InvariantCulture)}";
            var moved = resolution == TrackingResolution.Archive
                ? await _copy.ArchiveAfterAsync(Identity, Stream, divergence.Common, marker, cancellationToken).ConfigureAwait(false)
                : await _copy.DeleteAfterAsync(Identity, Stream, divergence.Common, cancellationToken).ConfigureAwait(false);

            MoveCheckpointTo(divergence.Common, actor);
            if (!_divergences.Resolve(divergence.Id, resolution, actor)) return false;

            _audit?.Record(new AuditEvent("person", actor, "tracking.resolve", TargetKind: "tracking_stream", TargetId: $"{Identity}/{Stream.Name}",
                Detail: $"{{\"stream\":\"{Escape(Stream.Name)}\",\"resolution\":\"{resolution}\",\"rows\":{moved.ToString(CultureInfo.InvariantCulture)},\"divergence\":{divergence.Id.ToString(CultureInfo.InvariantCulture)}}}"));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MoveCheckpointTo(TrackingPosition? common, string actor)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = _progress.GetCheckpoint(CheckpointName, Identity);
            if (common is null)
            {
                if (current is null || _progress.DeleteCheckpoint(CheckpointName, actor, Identity)) return;
            }
            else if (_progress.Advance(CheckpointName, new Dictionary<string, ProgressValue> { [OrderPart] = ProgressValue.OfInt(common.Order), [KeyPart] = ProgressValue.OfText(common.Key) }, current?.Version ?? 0, itemKey: Identity, actor: actor))
            {
                return;
            }
        }

        throw new InvalidOperationException($"The checkpoint of '{Stream.Name}' for this connection kept moving while the divergence was being resolved.");
    }

    internal static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
