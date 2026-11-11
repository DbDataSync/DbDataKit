using DbDataKit.State.Tracking;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// The runner's copy of Build's tracking (plan 28c E16), bound to the run: the <c>build_tracking</c> module of the runner's state, written through the protocol because the task holds no connection to that state. Every
/// call is for an identity that ends with <c>@&lt;connection&gt;</c>, a connection the run holds a handle for (its slot's name, or the connection the channel binds it to), and for one of Build's tracking tables; the runner
/// refuses anything else. This is the copy side of a <see cref="TrackingFollower"/> in the task's process (<see cref="IRemoteTrackingCopy"/>): rows and checkpoint move in one transaction on the runner.
/// </summary>
public sealed class EngineTracking : IRemoteTrackingCopy
{
    private readonly RunContext _context;

    internal EngineTracking(RunContext context) => _context = context;

    public Task<TrackingCopyState> StateAsync(string identity, string stream, CancellationToken ct = default) => _context.Backend.TrackingStateAsync(identity, stream, ct);

    public Task<bool> WriteAdvanceAsync(string identity, TrackingStream stream, IReadOnlyList<TrackingRow> rows, long expectedVersion, CancellationToken ct = default) =>
        _context.Backend.TrackingWriteAsync(identity, stream.Name, rows, advance: true, expectedVersion, ct);

    public async Task RewriteAsync(string identity, string stream, IReadOnlyList<TrackingRow> rows, CancellationToken ct = default) =>
        await _context.Backend.TrackingWriteAsync(identity, stream, rows, advance: false, 0, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<TrackingPosition>> TailAsync(string identity, TrackingStream stream, int count, CancellationToken ct = default) => _context.Backend.TrackingTailAsync(identity, stream.Name, count, ct);

    public Task<TrackingDivergence> MarkDivergenceAsync(string identity, string stream, string reason, TrackingPosition? common, TrackingPosition? primaryHigh, TrackingPosition? copyHigh, CancellationToken ct = default) =>
        _context.Backend.TrackingDivergenceAsync(identity, stream, reason, common, primaryHigh, copyHigh, ct);

    public Task<IReadOnlyList<TrackingRow>> ReadAsync(string identity, string stream, TrackingPosition? after, int limit, CancellationToken ct = default) => _context.Backend.TrackingReadAsync(identity, stream, after, limit, ct);

    public Task<IReadOnlyList<(string Key, string Hash)>> UnsettledAsync(string identity, string stream, int limit, CancellationToken ct = default) => _context.Backend.TrackingUnsettledAsync(identity, stream, limit, ct);

    public Task<int> ReplaceSnapshotAsync(string identity, string table, IReadOnlyList<TrackingRow> rows, CancellationToken ct = default) => _context.Backend.TrackingSnapshotAsync(identity, table, rows, ct);
}
