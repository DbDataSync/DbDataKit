namespace DbDataKit.Runner;

/// <summary>
/// Read-only views shared by every run that needs them (plan 28c E04): a runtime package's files are the engine and its natives, often 100 MB and more, so a view per run would copy them per run wherever a link is refused. The first run of a
/// version builds the view, the runs that start while it is in use share it, and the last one to finish removes it; the objects stay in the artifact cache either way. A definition's own view stays per run.
/// </summary>
public sealed class SharedViewCache(ArtifactCache cache)
{
    private sealed class Entry(MaterializedVersion view) { public MaterializedVersion View { get; } = view; public int Users; }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The number of shared views that exist now (tests).</summary>
    public int Count { get { _gate.Wait(); try { return _entries.Count; } finally { _gate.Release(); } } }

    /// <summary>How many views were built since the start (tests: two runs of one version build one).</summary>
    public int Built { get; private set; }

    /// <summary>Takes a share of the view of <paramref name="key"/> (a version key such as <c>dbdatabuild-runtime@3</c>), building it from <paramref name="files"/> when no run holds it.</summary>
    public async Task<SharedView> AcquireAsync(string key, IReadOnlyCollection<ArtifactFile> files, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(await cache.MaterializeAsync(key, files, ct).ConfigureAwait(false));
                _entries[key] = entry;
                Built++;
            }

            entry.Users++;
            return new SharedView(this, key, entry.View.Directory);
        }
        finally { _gate.Release(); }
    }

    private void Release(string key)
    {
        _gate.Wait();
        try
        {
            if (_entries.TryGetValue(key, out var entry) && --entry.Users <= 0)
            {
                _entries.Remove(key);
                entry.View.Dispose();
            }
        }
        finally { _gate.Release(); }
    }

    internal void Give(string key) => Release(key);
}

/// <summary>A share of a view held in a <see cref="SharedViewCache"/>.</summary>
public sealed class SharedView : IDisposable
{
    private readonly SharedViewCache _owner;
    private readonly string _key;
    private int _disposed;

    internal SharedView(SharedViewCache owner, string key, string directory)
    {
        _owner = owner;
        _key = key;
        Directory = directory;
    }

    /// <summary>The root of the view: read-only.</summary>
    public string Directory { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Give(_key);
    }
}
