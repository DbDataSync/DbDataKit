using DbDataKit.State.Tracking;

namespace DbDataKit.Tracking.Build;

/// <summary>
/// Where a record of Build's goes <b>first</b> when the project's tracking has the state as its primary (plan 28c E16, <c>tracking: { primary: state }</c>): the writer of a tracking row calls the journal before it writes the target, so the state
/// holds every row the target holds (the target stays the second copy). A journal is for one tracking store, named by its identity. The rows are the same documents the follower copies (<see cref="BuildStreams"/>).
/// </summary>
public interface ITrackingJournal
{
    /// <summary>The identity of the tracking store the rows are for (<c>buildtracking:...@connection</c>).</summary>
    string Identity { get; }

    /// <summary>A row of <paramref name="table"/> as the writer is about to insert it: the columns by name (a column left out is null; <c>connection</c> is never needed).</summary>
    Task InsertAsync(string table, IReadOnlyDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary>An update of the row of <paramref name="table"/> whose key columns are <paramref name="key"/>: the columns that change, by name.</summary>
    Task UpdateAsync(string table, IReadOnlyDictionary<string, object?> key, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken);
}

/// <summary>The state side of a journal: rows are upserted without moving any follower checkpoint, and read back where an update needs the row it changes. The runner's copy (through the protocol) and a state database in this process both are one.</summary>
public interface IBuildTrackingSink
{
    Task WriteAsync(string identity, string stream, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrackingRow>> ReadAsync(string identity, string stream, TrackingPosition? after, int limit, CancellationToken cancellationToken);

    Task ReplaceSnapshotAsync(string identity, string table, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken);
}

/// <summary>A <see cref="BuildTrackingStore"/> in this process as a journal's sink (the Build server, a command given a state database).</summary>
public sealed class StateBuildTrackingSink(BuildTrackingStore store) : IBuildTrackingSink
{
    public Task WriteAsync(string identity, string stream, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken) =>
        store.WriteAsync(identity, BuildStreams.Get(stream).Stream, rows, cancellationToken);

    public Task<IReadOnlyList<TrackingRow>> ReadAsync(string identity, string stream, TrackingPosition? after, int limit, CancellationToken cancellationToken) =>
        store.ReadAfterAsync(identity, BuildStreams.Get(stream).Stream, after, limit, cancellationToken);

    public Task ReplaceSnapshotAsync(string identity, string table, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken)
    {
        store.ReplaceSnapshot(identity, table, [.. rows.Select(r => (r.Key, r.Order, r.Document))]);
        return Task.CompletedTask;
    }
}

/// <summary>The runner's copy as a journal's sink: a task has no connection to the runner's state, so rows go through the protocol (<see cref="IRemoteTrackingCopy"/>), bound to the run's connections.</summary>
public sealed class RemoteBuildTrackingSink(IRemoteTrackingCopy copy) : IBuildTrackingSink
{
    public Task WriteAsync(string identity, string stream, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken) => copy.RewriteAsync(identity, stream, rows, cancellationToken);

    public Task<IReadOnlyList<TrackingRow>> ReadAsync(string identity, string stream, TrackingPosition? after, int limit, CancellationToken cancellationToken) => copy.ReadAsync(identity, stream, after, limit, cancellationToken);

    public async Task ReplaceSnapshotAsync(string identity, string table, IReadOnlyList<TrackingRow> rows, CancellationToken cancellationToken) => await copy.ReplaceSnapshotAsync(identity, table, rows, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Where the journals of a process get their state (plan 28c E16): the state a run may write its records to, set by the host that has one (the Build server, a runner task through the protocol, a command given a state database by its
/// environment), and read by the code that opens a project's tracking. <see cref="Sink"/> is per async flow, so two projects in one process do not meet.
/// </summary>
public static class TrackingJournalSource
{
    private static readonly AsyncLocal<IBuildTrackingSink?> Current = new();

    /// <summary>The sink of the current flow; null where the host has no state to give.</summary>
    public static IBuildTrackingSink? Sink => Current.Value;

    /// <summary>Makes <paramref name="sink"/> the state of the current flow until the returned scope is disposed.</summary>
    public static IDisposable Use(IBuildTrackingSink? sink)
    {
        var previous = Current.Value;
        Current.Value = sink;
        return new Restore(previous);
    }

    private sealed class Restore(IBuildTrackingSink? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
