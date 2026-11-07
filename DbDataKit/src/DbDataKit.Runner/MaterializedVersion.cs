namespace DbDataKit.Runner;

/// <summary>
/// One run's read-only view of a version: <see cref="Directory"/> holds the files at their artifact paths. Disposing it removes the view and lets the cache evict objects only it
/// needed; the cached objects themselves stay.
/// </summary>
public sealed class MaterializedVersion : IDisposable, IAsyncDisposable
{
    private readonly ArtifactCache _cache;
    private readonly IReadOnlyList<string> _hashes;
    private int _disposed;

    internal MaterializedVersion(ArtifactCache cache, string versionKey, string directory, IReadOnlyList<ArtifactFile> files, IReadOnlyList<string> hashes)
    {
        _cache = cache;
        VersionKey = versionKey;
        Directory = directory;
        Files = files;
        _hashes = hashes;
    }

    public string VersionKey { get; }

    /// <summary>The view's root.</summary>
    public string Directory { get; }

    /// <summary>The files of the view, with normalized (lower-case) hashes.</summary>
    public IReadOnlyList<ArtifactFile> Files { get; }

    /// <summary>The full path of an artifact in this view.</summary>
    public string PathOf(string artifactPath) => System.IO.Path.Combine([Directory, .. ArtifactPaths.Segments(artifactPath)]);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ArtifactCache.DeleteView(Directory);
        _cache.Release(_hashes);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
