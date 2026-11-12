using System.Data.Common;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// What a writer that carries the replication's state calls just before it commits the unit's transaction (plan 28c E17): the hook writes the unit's state row
/// (<see cref="TargetUnitState"/>) on the same connection and transaction, so the unit's data and the record of it commit together or not at all.
/// </summary>
public interface IUnitStateCommit
{
    /// <summary>Writes the unit's state row in <paramref name="transaction"/>. Called once per commit.</summary>
    Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// The ambient hook of the unit being written (an <see cref="AsyncLocal{T}"/>, so the writer's own signature does not change and a writer that does not know of it is unaffected).
/// <see cref="Begin"/> is what the run does around <c>IChangeWriter.ApplyAsync</c> when the replication keeps its state on the target; a writer with
/// <see cref="IChangeWriter.SupportsUnitState"/> calls <see cref="BeforeCommitAsync"/> before its commit, and calls nothing when no scope is active.
/// </summary>
public static class UnitStateScope
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Whether a unit is being written with its state on the target.</summary>
    public static bool Active => Current.Value is not null;

    /// <summary>Starts a scope; dispose it when the writer has returned. Scopes do not nest.</summary>
    public static Scope Begin(IUnitStateCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (Current.Value is not null) throw new InvalidOperationException("A unit is already being written with its state on the target.");
        var scope = new Scope(commit);
        Current.Value = scope;
        return scope;
    }

    /// <summary>The writer's call before its commit: writes the unit's state row in the open transaction (nothing when no scope is active).</summary>
    public static async Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        if (Current.Value is not { } scope) return;
        await scope.Commit.BeforeCommitAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        scope.Fired++;
    }

    public sealed class Scope : IDisposable
    {
        internal Scope(IUnitStateCommit commit) => Commit = commit;

        internal IUnitStateCommit Commit { get; }

        /// <summary>How many times a writer wrote the state row. Zero after a write that had rows means the writer did not carry the state (the run refuses that).</summary>
        public int Fired { get; internal set; }

        public void Dispose() => Current.Value = null;
    }
}
