using System.Runtime.CompilerServices;

namespace DbDataKit.Runner.Triggers;

/// <summary>A file a source lists: where (relative to the source's root, with <c>/</c>), how big, when it was last modified.</summary>
public sealed record FileEntry(string Path, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// The folder of a connection, as a file trigger sees it (plan 28a R13). No file or SFTP connection kind exists in <c>DbDataKit.Connections</c> yet, so this is the seam: <see cref="LocalDirectoryFileSource"/> is the only
/// implementation today, and a kind that arrives implements the same two calls. A source lists and reads; it never writes, moves or deletes (what happens to a file after the run is the task's).
/// </summary>
public interface IFileSource
{
    /// <summary>The files of <paramref name="directory"/> (relative to the root, empty for the root itself) that match <paramref name="pattern"/> (<c>*</c> and <c>?</c>), not descending into folders.</summary>
    IAsyncEnumerable<FileEntry> ListAsync(string directory, string pattern, CancellationToken ct);

    /// <summary>Opens a listed file for reading, sequentially.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
}

/// <summary>Finds the source for a connection slot of a task. Plan 30 binds a slot to a connection per channel; until then the runner is told which folder a slot means.</summary>
public interface IFileSourceProvider
{
    /// <summary>The source behind <paramref name="connectionSlot"/> of <paramref name="task"/>, or null when this runner has none for it.</summary>
    IFileSource? Open(TaskRef task, string connectionSlot);
}

/// <summary>A folder on this machine as a source. A path that leaves the folder (<c>..</c>, a rooted path) is refused.</summary>
public sealed class LocalDirectoryFileSource : IFileSource
{
    private readonly string _root;

    public LocalDirectoryFileSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = System.IO.Path.GetFullPath(root);
    }

    public string Root => _root;

    private string Resolve(string relative)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? _root : _root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(full, _root, StringComparison.Ordinal))
            throw new ArgumentException($"The path '{relative}' leaves the folder of the connection.");
        return full;
    }

    public async IAsyncEnumerable<FileEntry> ListAsync(string directory, string pattern, [EnumeratorCancellation] CancellationToken ct)
    {
        var folder = Resolve(directory);
        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
        if (!Directory.Exists(folder)) yield break;
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = directory.Length == 0 ? info.Name : directory.TrimEnd('/') + "/" + info.Name;
            yield return new FileEntry(relative, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
        System.Threading.Tasks.Task.FromResult<Stream>(new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan | FileOptions.Asynchronous));
}

/// <summary>
/// Tells the runner which local folder a connection slot means: by <c>package/channel/slot</c> or by the slot alone (the first is looked for first). The stand-in for a channel's binding of a slot to a connection (plan 30) for the
/// folders of this machine.
/// </summary>
public sealed class LocalFileSources(IReadOnlyDictionary<string, string> folders) : IFileSourceProvider
{
    public IFileSource? Open(TaskRef task, string connectionSlot)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (folders.TryGetValue($"{task.Package}/{task.Channel}/{connectionSlot}", out var specific) || folders.TryGetValue(connectionSlot, out specific))
            return new LocalDirectoryFileSource(specific);
        return null;
    }
}
