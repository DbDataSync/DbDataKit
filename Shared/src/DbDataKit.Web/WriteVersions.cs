using System.Collections.Concurrent;

namespace DbDataKit.Web.Trust;

/// <summary>
/// What a write was based on versus what is there now. A management write names the version it read (a content hash, or none for "this does
/// not exist yet"); if the thing has changed since, the write is refused with the version it has now, so the caller can read it again, merge,
/// and retry. Never a silent overwrite.
/// </summary>
public sealed record WriteResult<T>(bool Applied, T? Value, string? CurrentVersion)
{
    public static WriteResult<T> Done(T value) => new(true, value, null);
    public static WriteResult<T> Stale(string? currentVersion) => new(false, default, currentVersion);
}

/// <summary>The refusal as an exception, for a caller that prefers it; carries the version to read again from.</summary>
public sealed class StaleWriteException(string? currentVersion)
    : InvalidOperationException(currentVersion is null ? "The change was based on a version of something that is not there now." : $"The change was based on an older version; the current version is {currentVersion}.")
{
    public string? CurrentVersion { get; } = currentVersion;
}

/// <summary>
/// Serialises writes per key and checks the version under the lock, so two writers that both read version V cannot both apply: the first
/// writes, the second finds a different version and is refused. The check and the write are one step as far as other callers of the same
/// guard are concerned; a writer outside the guard (a person editing the file by hand) is caught by the same check on the next write, because
/// the version is read from the thing itself every time.
/// </summary>
public sealed class WriteGuard
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="write"/> when <paramref name="currentVersion"/> (read under the lock) equals <paramref name="expectedVersion"/>.
    /// A null expected version means "create": it holds only when the current version is null too. Versions are compared ignoring case (hex).
    /// </summary>
    public async Task<WriteResult<T>> RunAsync<T>(string key, string? expectedVersion, Func<Task<string?>> currentVersion, Func<Task<T>> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await currentVersion().ConfigureAwait(false);
            if (!string.Equals(current, expectedVersion, StringComparison.OrdinalIgnoreCase))
                return WriteResult<T>.Stale(current);
            return WriteResult<T>.Done(await write().ConfigureAwait(false));
        }
        finally { gate.Release(); }
    }

    /// <summary>As <see cref="RunAsync{T}"/>, throwing <see cref="StaleWriteException"/> instead of returning a refusal.</summary>
    public async Task<T> RunOrThrowAsync<T>(string key, string? expectedVersion, Func<Task<string?>> currentVersion, Func<Task<T>> write, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(key, expectedVersion, currentVersion, write, cancellationToken).ConfigureAwait(false);
        return result.Applied ? result.Value! : throw new StaleWriteException(result.CurrentVersion);
    }
}
