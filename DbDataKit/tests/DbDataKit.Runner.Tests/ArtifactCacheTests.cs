using System.Security.Cryptography;
using DbDataKit.State;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 28a R05: the artifact cache and the read-only view of a version.</summary>
public sealed class ArtifactCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdatakit-r05-" + Guid.NewGuid().ToString("N"));

    public ArtifactCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose() => ArtifactTestSupport.DeleteTree(_root);

    private string CacheDirectory => Path.Combine(_root, "cache");

    [Fact]
    public async Task A_tampered_file_is_refused_naming_the_path_and_both_hashes_and_leaves_nothing()
    {
        var source = new MemorySource();
        var file = source.Add("bin/app.dll", "the published bytes");
        source.Tamper(file.Sha256, "other bytes, same name"u8.ToArray());
        var cache = new ArtifactCache(CacheDirectory, source);

        var thrown = await Assert.ThrowsAsync<ArtifactHashMismatchException>(() => cache.MaterializeAsync("v1", [file]));

        var actual = Convert.ToHexStringLower(SHA256.HashData("other bytes, same name"u8));
        Assert.Contains("bin/app.dll", thrown.Message);
        Assert.Contains(file.Sha256, thrown.Message);
        Assert.Contains(actual, thrown.Message);
        Assert.Equal(file.Sha256, thrown.ExpectedSha256);
        Assert.Equal(actual, thrown.ActualSha256);
        Assert.Empty(cache.CachedHashes());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(CacheDirectory, "tmp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(CacheDirectory, "views")));
    }

    [Fact]
    public async Task A_file_of_the_wrong_size_with_the_right_hash_is_refused()
    {
        var source = new MemorySource();
        var file = source.Add("a.txt", "abc") with { Size = 99 };
        var cache = new ArtifactCache(CacheDirectory, source);

        var thrown = await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("v1", [file]));

        Assert.Contains("a.txt", thrown.Message);
        Assert.Contains("99", thrown.Message);
        Assert.Empty(cache.CachedHashes());
    }

    [Fact]
    public async Task Two_versions_sharing_a_file_store_it_once()
    {
        var source = new MemorySource();
        var shared = source.Add("lib/shared.dll", "shared content");
        var one = source.Add("only1.txt", "one");
        var two = source.Add("only2.txt", "two");
        var cache = new ArtifactCache(CacheDirectory, source);

        using var v1 = await cache.MaterializeAsync("v1", [shared, one]);
        using var v2 = await cache.MaterializeAsync("v2", [shared with { Path = "renamed/shared.dll" }, two]);

        Assert.Equal(3, cache.CachedHashes().Count);
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(CacheDirectory, "objects"), "*", SearchOption.AllDirectories).Count());
        Assert.Equal(1, source.Opens(shared.Sha256));
        Assert.Equal("shared content", await File.ReadAllTextAsync(v1.PathOf("lib/shared.dll")));
        Assert.Equal("shared content", await File.ReadAllTextAsync(v2.PathOf("renamed/shared.dll")));
        Assert.StartsWith(Path.Combine(CacheDirectory, "objects", shared.Sha256[..2]), cache.ObjectPath(shared.Sha256));
    }

    [Theory]
    [InlineData(ArtifactLinkMode.Auto)]
    [InlineData(ArtifactLinkMode.Copy)]
    public async Task A_view_cannot_be_written_to_replaced_or_deleted_from(ArtifactLinkMode mode)
    {
        var source = new MemorySource();
        var top = source.Add("run.txt", "top");
        var deep = source.Add("a/b/deep.txt", "deep");
        var cache = new ArtifactCache(CacheDirectory, source, mode);

        using var view = await cache.MaterializeAsync("v1", [top, deep]);

        Assert.Equal("top", await File.ReadAllTextAsync(Path.Combine(view.Directory, "run.txt")));
        Assert.Equal("deep", await File.ReadAllTextAsync(view.PathOf("a/b/deep.txt")));
        var deepFile = view.PathOf("a/b/deep.txt");
        var deepDirectory = Path.GetDirectoryName(deepFile)!;

        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.GetAttributes(deepFile).HasFlag(FileAttributes.ReadOnly));
            Assert.True(File.GetAttributes(view.PathOf("run.txt")).HasFlag(FileAttributes.ReadOnly));
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(deepFile, "x"));
            return;
        }

        // the mode bits are what every user but a privileged one is held to
        const UnixFileMode noWrite = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(deepFile) & noWrite);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(view.PathOf("run.txt")) & noWrite);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(deepDirectory) & noWrite);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(Path.GetDirectoryName(deepDirectory)!) & noWrite);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(view.Directory) & noWrite);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(cache.ObjectPath(deep.Sha256)) & noWrite);

        // and, for a user the mode bits bind, the attempts themselves fail (root, as in some CI containers, may write anything)
        if (ArtifactTestSupport.ModeBitsDoNotBindThisUser(_root))
            return;
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(deepFile, "changed"));
        Assert.Throws<UnauthorizedAccessException>(() => File.Delete(deepFile));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(deepDirectory, "new.txt"), "x"));
        Assert.Throws<UnauthorizedAccessException>(() => File.Move(view.PathOf("run.txt"), view.PathOf("moved.txt")));
        Assert.Throws<UnauthorizedAccessException>(() => File.Replace(deepFile, view.PathOf("run.txt"), null));
        Assert.Equal("deep", await File.ReadAllTextAsync(deepFile));
    }

    [Fact]
    public async Task Disposing_a_view_removes_it_and_keeps_the_objects()
    {
        var source = new MemorySource();
        var file = source.Add("a/b/c.txt", "content");
        var cache = new ArtifactCache(CacheDirectory, source);

        var view = await cache.MaterializeAsync("v1", [file]);
        var directory = view.Directory;
        Assert.True(Directory.Exists(directory));
        view.Dispose();
        view.Dispose();

        Assert.False(Directory.Exists(directory));
        Assert.Single(cache.CachedHashes());
        using var again = await cache.MaterializeAsync("v1", [file]);
        Assert.Equal(1, source.Opens(file.Sha256));
        Assert.NotEqual(directory, again.Directory);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/x.dll")]
    [InlineData("c:x.dll")]
    [InlineData("a\\b.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/b/")]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("bin/nul.txt")]
    [InlineData("lpt1.log")]
    [InlineData("name.")]
    [InlineData("a:b.txt")]
    public async Task A_path_that_could_leave_the_view_is_refused_naming_the_path_before_anything_is_fetched(string path)
    {
        var source = new MemorySource();
        var good = source.Add("good.txt", "good");
        var bad = source.Add("bad-content", "bad") with { Path = path };
        var cache = new ArtifactCache(CacheDirectory, source);

        var thrown = await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("v1", [good, bad]));

        Assert.Contains($"'{path}'", thrown.Message);
        Assert.Equal(0, source.TotalOpens);
        Assert.Empty(cache.CachedHashes());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(CacheDirectory, "views")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Colliding_paths_and_bad_hashes_are_refused()
    {
        var source = new MemorySource();
        var a = source.Add("dir/a.txt", "a");
        var cache = new ArtifactCache(CacheDirectory, source);

        var twice = await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("v", [a, a]));
        Assert.Contains("dir/a.txt", twice.Message);
        var caseOnly = await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("v", [a, a with { Path = "DIR/A.txt" }]));
        Assert.Contains("differs only in case", caseOnly.Message);
        var fileAndDirectory = await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("v", [a with { Path = "dir" }, a]));
        Assert.Contains("'dir'", fileAndDirectory.Message);
        var badHash = await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("v", [a with { Sha256 = "xyz" }]));
        Assert.Contains("dir/a.txt", badHash.Message);
        Assert.Contains("xyz", badHash.Message);
        Assert.Equal(0, source.TotalOpens);
    }

    [Fact]
    public async Task An_upper_case_hash_in_the_descriptor_is_the_same_object()
    {
        var source = new MemorySource();
        var file = source.Add("a.txt", "a");
        var cache = new ArtifactCache(CacheDirectory, source);

        using var view = await cache.MaterializeAsync("v", [file with { Sha256 = file.Sha256.ToUpperInvariant() }]);

        Assert.Equal([file.Sha256], cache.CachedHashes());
        Assert.Equal(file.Sha256, Assert.Single(view.Files).Sha256);
    }

    [Fact]
    public async Task Eight_concurrent_materializations_of_one_hash_leave_one_object_and_eight_valid_views()
    {
        var source = new MemorySource { Delay = TimeSpan.FromMilliseconds(50) };
        var file = source.Add("lib/x.bin", new string('x', 300_000));
        var other = source.Add("lib/y.bin", "second");
        var cache = new ArtifactCache(CacheDirectory, source);

        var views = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => cache.MaterializeAsync($"run-{i}", [file, other]))));

        try
        {
            Assert.Equal(2, cache.CachedHashes().Count);
            Assert.Equal(8, views.Select(v => v.Directory).Distinct().Count());
            foreach (var view in views)
            {
                Assert.Equal(new string('x', 300_000), await File.ReadAllTextAsync(view.PathOf("lib/x.bin")));
                Assert.Equal("second", await File.ReadAllTextAsync(view.PathOf("lib/y.bin")));
            }

            Assert.Empty(Directory.EnumerateFiles(Path.Combine(CacheDirectory, "tmp")));
        }
        finally
        {
            foreach (var view in views) view.Dispose();
        }
    }

    [Fact]
    public async Task Two_caches_on_one_directory_materializing_the_same_hash_are_safe()
    {
        var source = new MemorySource { Delay = TimeSpan.FromMilliseconds(30) };
        var file = source.Add("x.bin", new string('q', 100_000));
        var one = new ArtifactCache(CacheDirectory, source);
        var two = new ArtifactCache(CacheDirectory, source);

        var views = await Task.WhenAll(Task.Run(() => one.MaterializeAsync("a", [file])), Task.Run(() => two.MaterializeAsync("b", [file])));

        Assert.Single(one.CachedHashes());
        foreach (var view in views)
        {
            Assert.Equal(100_000, (await File.ReadAllTextAsync(view.PathOf("x.bin"))).Length);
            view.Dispose();
        }

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(CacheDirectory, "tmp")));
    }

    [Fact]
    public async Task Eviction_keeps_what_is_needed_and_what_a_live_view_holds_and_removes_the_rest()
    {
        var source = new MemorySource();
        var shared = source.Add("shared.txt", "shared");
        var a = source.Add("a.txt", "only a");
        var b = source.Add("b.txt", "only b");
        var cache = new ArtifactCache(CacheDirectory, source);
        var viewA = await cache.MaterializeAsync("a", [shared, a]);
        var viewB = await cache.MaterializeAsync("b", [shared, b]);
        viewB.Dispose();

        // b is not served any more and no view holds its file; a is not "kept" but its view is live
        var first = await cache.EvictAsync(new HashSet<string> { shared.Sha256 });
        Assert.Equal(1, first.Removed);
        Assert.Equal(1, first.KeptInUse);
        Assert.Equal(b.Size, first.RemovedBytes);
        Assert.Equal(new[] { a.Sha256, shared.Sha256 }.Order(StringComparer.Ordinal), cache.CachedHashes());
        Assert.Equal("only a", await File.ReadAllTextAsync(viewA.PathOf("a.txt")));
        Assert.Equal("shared", await File.ReadAllTextAsync(viewA.PathOf("shared.txt")));

        viewA.Dispose();
        var second = await cache.EvictAsync(new HashSet<string> { shared.Sha256.ToUpperInvariant() });
        Assert.Equal(1, second.Removed);
        Assert.Equal([shared.Sha256], cache.CachedHashes());

        var third = await cache.EvictAsync(new HashSet<string>());
        Assert.Equal(1, third.Removed);
        Assert.Empty(cache.CachedHashes());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(CacheDirectory, "objects")));

        // an evicted file is fetched again when a version needs it
        using var again = await cache.MaterializeAsync("a", [a]);
        Assert.Equal("only a", await File.ReadAllTextAsync(again.PathOf("a.txt")));
    }

    [Fact]
    public async Task A_failed_materialization_releases_what_it_held()
    {
        var source = new MemorySource();
        var good = source.Add("good.txt", "good");
        var bad = source.Add("bad.txt", "bad");
        source.Tamper(bad.Sha256, "evil"u8.ToArray());
        var cache = new ArtifactCache(CacheDirectory, source);

        await Assert.ThrowsAsync<ArtifactHashMismatchException>(() => cache.MaterializeAsync("v", [good, bad]));

        var eviction = await cache.EvictAsync(new HashSet<string>());
        Assert.Equal(0, eviction.KeptInUse);
        Assert.Empty(cache.CachedHashes());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(CacheDirectory, "views")));
    }

    [Fact]
    public async Task Cancellation_stops_a_download_and_leaves_no_partial_file()
    {
        var source = new MemorySource { Delay = TimeSpan.FromSeconds(30) };
        var file = source.Add("slow.bin", "slow");
        var cache = new ArtifactCache(CacheDirectory, source);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.MaterializeAsync("v", [file], cts.Token));

        Assert.Empty(cache.CachedHashes());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(CacheDirectory, "tmp")));
        Assert.Equal(0, (await cache.EvictAsync(new HashSet<string>())).KeptInUse);
    }

    [Fact]
    public async Task A_version_is_materialized_from_the_payload_store()
    {
        var database = new StateDatabase(Path.Combine(_root, "state.db"), StateLayout.Create(StateEngineIds.Sqlite, [PayloadStore.Module]));
        var source = new PayloadArtifactSource(new PayloadStore(database, chunkSize: 4096));
        var bytes = new byte[10_000];
        new Random(7).NextBytes(bytes);
        var binary = await source.AddAsync("v1", "bin/tool.dll", new MemoryStream(bytes), "application/octet-stream");
        var text = await source.AddAsync("v1", "package.yaml", new MemoryStream("schema: package/1\n"u8.ToArray()), "text/yaml");
        var cache = new ArtifactCache(CacheDirectory, source);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), binary.Sha256);
        Assert.NotNull(binary.PayloadId);
        using var view = await cache.MaterializeAsync("v1");

        Assert.Equal(2, view.Files.Count);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(view.PathOf("bin/tool.dll")));
        Assert.Equal("schema: package/1\n", await File.ReadAllTextAsync(view.PathOf("package.yaml")));
        Assert.Equal(text.Size, ArtifactTestSupport.LengthOf(view.PathOf("package.yaml")));
        await Assert.ThrowsAsync<ArtifactException>(() => cache.MaterializeAsync("unknown"));
    }
}

/// <summary>A source of files held in memory, keyed by hash, that can hand out other bytes than the descriptor promises and count its opens.</summary>
internal sealed class MemorySource : IArtifactSource
{
    private readonly Dictionary<string, byte[]> _content = [];
    private readonly Dictionary<string, int> _opens = [];
    private int _total;

    public TimeSpan Delay { get; init; }

    public int TotalOpens => Volatile.Read(ref _total);

    public int Opens(string sha256) { lock (_opens) return _opens.GetValueOrDefault(sha256); }

    public ArtifactFile Add(string path, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _content[hash] = bytes;
        return new ArtifactFile(path, hash, bytes.Length);
    }

    public void Tamper(string sha256, byte[] other) => _content[sha256] = other;

    public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _total);
        lock (_opens) _opens[file.Sha256.ToLowerInvariant()] = _opens.GetValueOrDefault(file.Sha256.ToLowerInvariant()) + 1;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        return new MemoryStream(_content[file.Sha256.ToLowerInvariant()], writable: false);
    }
}

internal static class ArtifactTestSupport
{
    /// <summary>The size of the file a path leads to, through a link if it is one.</summary>
    public static long LengthOf(string path)
    {
        using var handle = File.OpenHandle(path);
        return RandomAccess.GetLength(handle);
    }

    /// <summary>True for a user who can write a read-only file (root, or one with CAP_DAC_OVERRIDE): the mode bits are then asserted, but not the attempt to write.</summary>
    public static bool ModeBitsDoNotBindThisUser(string scratchDirectory)
    {
        if (OperatingSystem.IsWindows()) return false;
        var probe = Path.Combine(scratchDirectory, "probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "x");
        File.SetUnixFileMode(probe, UnixFileMode.UserRead);
        try
        {
            File.WriteAllText(probe, "y");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(probe);
        }
    }

    /// <summary>Removes a tree that may hold read-only directories.</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Append(path))
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(directory, FileAttributes.Normal);
            else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try { Directory.Delete(path, recursive: true); } catch (IOException) { }
    }
}
