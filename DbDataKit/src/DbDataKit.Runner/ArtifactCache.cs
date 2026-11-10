using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DbDataKit.Runner;

/// <summary>How a run's view refers to the cached objects.</summary>
public enum ArtifactLinkMode
{
    /// <summary>Hard links to the cached objects (<see cref="FileLinks"/>: no privilege on any system, the file is an ordinary file at its path in the view), a copy of the file where a link is refused (another volume, a file system without links, the limit on the links to one file).</summary>
    Auto,

    /// <summary>Always copies.</summary>
    Copy,
}

/// <summary>What <see cref="ArtifactCache.EvictAsync"/> did.</summary>
/// <param name="Removed">Objects deleted.</param>
/// <param name="RemovedBytes">Their total size.</param>
/// <param name="KeptInUse">Objects that were not in the keep set but are held by a live view in this process, and so were left.</param>
public sealed record ArtifactEviction(int Removed, long RemovedBytes, int KeptInUse);

/// <summary>
/// The runner's local store of published files (plan 28 DR13, plan 30 sections 3 and 6): content-addressed objects under <c>objects/&lt;first two hex&gt;/&lt;sha256&gt;</c>, and a read-only
/// view per run laid out by the files' paths. A file is streamed from the <see cref="IArtifactSource"/> to a temporary name while it is hashed, refused if the hash is not the
/// descriptor's, made read-only, and renamed into place, so a partial or wrong file is never visible under an object name and two materializations of one hash (in this process or
/// another) leave one object. Memory use is one copy buffer, whatever the size of the files.
/// </summary>
/// <remarks>
/// A view's files are hard links to objects (or copies, see <see cref="ArtifactLinkMode"/>); the objects are mode 0444 and the view's directories 0555 on Linux, and carry the
/// read-only attribute on Windows, where directories stay writable in practice and the file attribute is what stops a plain write. A link and its object are one file, so the mode or attribute of one is
/// the other's: a write through a view is refused for the same reason a write to the object is. Which hashes a live view needs is tracked in
/// this process only: a second process evicting from the same directory is not protected against.
/// </remarks>
public sealed class ArtifactCache
{
    private const int BufferSize = 128 * 1024;

    private const UnixFileMode ReadOnlyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ReadOnlyDirectory = ReadOnlyFile | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const UnixFileMode WritableDirectory = ReadOnlyDirectory | UnixFileMode.UserWrite;

    private readonly IArtifactSource _source;
    private readonly ArtifactLinkMode _linkMode;
    private readonly Func<string, string, bool> _createLink;
    private readonly string _objects;
    private readonly string _temp;
    private readonly string _views;
    private readonly object _live = new();
    private readonly Dictionary<string, int> _liveHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <param name="directory">The cache's directory; objects, partial files and views live under it, so all of them are on one volume.</param>
    /// <param name="source">Where the files come from.</param>
    /// <param name="linkMode">Hard links or copies.</param>
    /// <param name="createLink">How a hard link is made: <c>(link, existing)</c>, true when it was made. Defaults to <see cref="FileLinks.TryCreateHardLink"/>; tests pass one that refuses, to see the copy that follows.</param>
    public ArtifactCache(string directory, IArtifactSource source, ArtifactLinkMode linkMode = ArtifactLinkMode.Auto, Func<string, string, bool>? createLink = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(source);
        Directory = Path.GetFullPath(directory);
        _source = source;
        _linkMode = linkMode;
        _createLink = createLink ?? FileLinks.TryCreateHardLink;
        _objects = Path.Combine(Directory, "objects");
        _temp = Path.Combine(Directory, "tmp");
        _views = Path.Combine(Directory, "views");
        System.IO.Directory.CreateDirectory(_objects);
        System.IO.Directory.CreateDirectory(_temp);
        System.IO.Directory.CreateDirectory(_views);
    }

    public string Directory { get; }

    /// <summary>Where the object with this hash is (or would be).</summary>
    public string ObjectPath(string sha256)
    {
        var hash = ArtifactPaths.NormalizeHash(sha256, sha256);
        return Path.Combine(_objects, hash[..2], hash);
    }

    /// <summary>The hashes of the objects in the cache.</summary>
    public IReadOnlyCollection<string> CachedHashes() =>
        [.. System.IO.Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>Materializes the files the source lists for the version.</summary>
    public async Task<MaterializedVersion> MaterializeAsync(string versionKey, CancellationToken cancellationToken = default) =>
        await MaterializeAsync(versionKey, await _source.ListAsync(versionKey, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Makes sure every file's object is cached (downloading and verifying those that are not), then builds a new read-only view of them. Refuses, before any download, a path that
    /// could leave the view, a malformed hash, and paths that collide (also by case, or one a file and the other a directory).
    /// </summary>
    public async Task<MaterializedVersion> MaterializeAsync(string versionKey, IReadOnlyCollection<ArtifactFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionKey);
        ArgumentNullException.ThrowIfNull(files);
        var checkedFiles = Validate(files);
        var hashes = checkedFiles.Select(f => f.Sha256).Distinct(StringComparer.Ordinal).ToList();

        // the hashes are marked in use before they are downloaded, so an eviction running meanwhile cannot take them
        Acquire(hashes);
        string? view = null;
        try
        {
            foreach (var file in checkedFiles.DistinctBy(f => f.Sha256, StringComparer.Ordinal))
                await EnsureObjectAsync(file, cancellationToken).ConfigureAwait(false);

            var name = Sanitize(versionKey);
            view = Path.Combine(_views, $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)]);
            BuildView(view, checkedFiles, cancellationToken);
            return new MaterializedVersion(this, versionKey, view, checkedFiles, hashes);
        }
        catch
        {
            if (view is not null) DeleteView(view);
            Release(hashes);
            throw;
        }
    }

    /// <summary>
    /// Removes the cached objects whose hash is not in <paramref name="keep"/> (the hashes of every version still served) and that no live view of this process holds, and
    /// partial downloads left by a crashed process.
    /// </summary>
    public Task<ArtifactEviction> EvictAsync(IReadOnlySet<string> keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var wanted = new HashSet<string>(keep.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        int removed = 0, inUse = 0;
        long bytes = 0;
        lock (_live)
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_objects, "*", SearchOption.AllDirectories).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Path.GetFileName(path);
                if (wanted.Contains(hash)) continue;
                if (_liveHashes.ContainsKey(hash)) { inUse++; continue; }
                var length = new FileInfo(path).Length;
                DeleteFile(path);
                removed++;
                bytes += length;
            }

            foreach (var prefix in System.IO.Directory.EnumerateDirectories(_objects))
                if (!System.IO.Directory.EnumerateFileSystemEntries(prefix).Any())
                    try { System.IO.Directory.Delete(prefix); } catch (IOException) { }
        }

        foreach (var part in System.IO.Directory.EnumerateFiles(_temp))
            if (File.GetLastWriteTimeUtc(part) < DateTime.UtcNow.AddHours(-1))
                try { DeleteFile(part); } catch (IOException) { }

        return Task.FromResult(new ArtifactEviction(removed, bytes, inUse));
    }

    internal void Release(IEnumerable<string> hashes)
    {
        lock (_live)
            foreach (var hash in hashes)
                if (_liveHashes.TryGetValue(hash, out var n))
                {
                    if (n <= 1) _liveHashes.Remove(hash);
                    else _liveHashes[hash] = n - 1;
                }
    }

    internal static void DeleteView(string view)
    {
        try
        {
            if (!System.IO.Directory.Exists(view)) return;
            foreach (var directory in System.IO.Directory.EnumerateDirectories(view, "*", SearchOption.AllDirectories).Append(view))
                MakeDirectoryWritable(directory);
            if (OperatingSystem.IsWindows())
                foreach (var file in System.IO.Directory.EnumerateFiles(view, "*", SearchOption.AllDirectories))
                {
                    // a file of a view may be a hard link of a cached object, and the read-only attribute is the file's, not the name's: the delete ignores it where Windows can, and only otherwise clears it
                    if (!FileLinks.TryDeleteIgnoringReadOnly(file)) File.SetAttributes(file, FileAttributes.Normal);
                }
            System.IO.Directory.Delete(view, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a view that cannot be removed now is only disk space; the cache directory is the runner's own
        }
    }

    private void Acquire(IEnumerable<string> hashes)
    {
        lock (_live)
            foreach (var hash in hashes)
                _liveHashes[hash] = _liveHashes.GetValueOrDefault(hash) + 1;
    }

    private static List<ArtifactFile> Validate(IReadOnlyCollection<ArtifactFile> files)
    {
        var result = new List<ArtifactFile>(files.Count);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            var segments = ArtifactPaths.Segments(file.Path);
            var hash = ArtifactPaths.NormalizeHash(file.Path, file.Sha256);
            if (file.Size < 0) throw new ArtifactException($"The artifact '{file.Path}' has a negative size ({file.Size}).");
            if (seen.TryGetValue(file.Path, out var other))
                throw new ArtifactException($"The artifact path '{file.Path}' is listed twice" + (other == file.Path ? "." : $" (once as '{other}', which differs only in case)."));
            seen[file.Path] = file.Path;
            for (var i = 1; i < segments.Length; i++) directories.Add(string.Join('/', segments.Take(i)));
            result.Add(file with { Sha256 = hash });
        }

        foreach (var file in result)
            if (directories.Contains(file.Path))
                throw new ArtifactException($"The artifact path '{file.Path}' is a file, but other artifacts are placed inside it as if it were a directory.");
        return result;
    }

    private async Task EnsureObjectAsync(ArtifactFile file, CancellationToken cancellationToken)
    {
        var final = ObjectPath(file.Sha256);
        if (IsComplete(final, file.Size)) return;

        var gate = _gates.GetOrAdd(file.Sha256, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsComplete(final, file.Size)) return;
            if (File.Exists(final)) DeleteFile(final); // present with the wrong size: damaged outside the cache's own writes
            await DownloadAsync(file, final, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsComplete(string path, long size) => File.Exists(path) && new FileInfo(path).Length == size;

    private async Task DownloadAsync(ArtifactFile file, string final, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(_temp, $"{Guid.NewGuid():N}.part");
        try
        {
            string actual;
            long total = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var content = await _source.OpenAsync(file, cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0, Options = FileOptions.Asynchronous,
                });
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (actual != file.Sha256) throw new ArtifactHashMismatchException(file.Path, file.Sha256, actual);
            if (total != file.Size)
                throw new ArtifactException($"The artifact '{file.Path}' is {total} bytes, but the version says {file.Size} (the hash {actual} matches). Nothing of it was kept.");

            MakeFileReadOnly(temp);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                File.Move(temp, final, overwrite: false);
            }
            catch (IOException) when (File.Exists(final))
            {
                // another materialization (maybe another process) renamed the same content in first; it is the same bytes by hash
            }
        }
        finally
        {
            if (File.Exists(temp)) DeleteFile(temp);
        }
    }

    private void BuildView(string view, List<ArtifactFile> files, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(view);
        var directories = new HashSet<string>(StringComparer.Ordinal) { view };
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = ArtifactPaths.Segments(file.Path);
            var target = Path.Combine([view, .. segments]);
            var parent = Path.GetDirectoryName(target)!;
            if (!Path.GetFullPath(target).StartsWith(view + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArtifactException($"The artifact path '{file.Path}' is refused: it resolves outside the run's directory.");
            for (var d = parent; d.Length > view.Length; d = Path.GetDirectoryName(d)!) directories.Add(d);
            System.IO.Directory.CreateDirectory(parent);

            var objectPath = ObjectPath(file.Sha256);
            var linked = false;
            if (_linkMode == ArtifactLinkMode.Auto)
            {
                // the link is the object under another name (same mode, same attributes); a refusal of any kind (volume, file system, link count) means a copy
                try { linked = _createLink(target, objectPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }

            if (!linked)
            {
                File.Copy(objectPath, target, overwrite: false);
                MakeFileReadOnly(target);
            }
        }

        // directories last, deepest first: a read-only directory cannot take the files above
        foreach (var directory in directories.OrderByDescending(d => d.Length))
            MakeDirectoryReadOnly(directory);
    }

    private static string Sanitize(string key)
    {
        var chars = key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').Take(48).ToArray();
        var name = new string(chars).TrimEnd('.');
        return name.Length == 0 ? "v" : name;
    }

    private static void MakeFileReadOnly(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        else File.SetUnixFileMode(path, ReadOnlyFile);
    }

    private static void MakeDirectoryReadOnly(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        else File.SetUnixFileMode(path, ReadOnlyDirectory);
    }

    private static void MakeDirectoryWritable(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        else File.SetUnixFileMode(path, WritableDirectory);
    }

    private static void DeleteFile(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}
