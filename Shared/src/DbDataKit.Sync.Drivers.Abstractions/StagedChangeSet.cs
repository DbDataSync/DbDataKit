namespace DbDataSync.Drivers.Abstractions;

/// <summary>Handle to a change set persisted by an <see cref="IStagingProvider"/>, opaque to the
/// caller and passed straight through to an <see cref="IChangeWriter"/>.</summary>
/// <param name="HasChangeOrdering">Whether the staging provider found and persisted both
/// <see cref="ChangeOrdering"/> columns — phase 132. Defaulted so every existing construction site
/// stays valid; a provider that staged them sets it, and a writer that finds it true can process a key
/// with more than one staged row in true source order instead of colliding.</param>
/// <param name="Stream">Set only by a pass-through cache, which stages nothing: the rows are still the reader's
/// stream, to be consumed once by the writer. See <see cref="PassThroughRows"/>.</param>
public sealed record StagedChangeSet(
    string StagingLocation, long StagedRowCount, bool HasChangeOrdering = false, PassThroughRows? Stream = null)
{
    /// <summary>Rows staged, or — for a pass-through — rows the writer has streamed so far, which is
    /// why it is only final once the write has finished.</summary>
    public long RowCount => Stream?.RowsStreamed ?? StagedRowCount;
}

/// <summary>
/// The rows of a pass that was not staged. A pass-through cache wraps the reader's stream in one of these
/// instead of loading it into a staging table, and the writer paired with it (<see cref="IPassThroughWriter"/>)
/// consumes it exactly once, straight into the target.
/// </summary>
public sealed class PassThroughRows(IAsyncEnumerable<ChangeRow> source)
{
    private bool _taken;

    public long RowsStreamed { get; private set; }

    /// <summary>The stream, counted as it is read. Single use: a reader's stream cannot be replayed, and
    /// a second consumer would silently see nothing.</summary>
    public IAsyncEnumerable<ChangeRow> Take()
    {
        if (_taken)
            throw new InvalidOperationException("A pass-through change set can only be read once.");
        _taken = true;
        return Count();
    }

    private async IAsyncEnumerable<ChangeRow> Count(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in source.WithCancellation(cancellationToken))
        {
            RowsStreamed++;
            yield return row;
        }
    }
}

/// <summary>
/// A writer that loads the reader's rows straight into the target and so needs the pass-through cache
/// rather than a staging table — and the reverse: no other writer can read a pass-through change set.
/// </summary>
public interface IPassThroughWriter : IChangeWriter;

public sealed record WriteResult(long RowsWritten);
