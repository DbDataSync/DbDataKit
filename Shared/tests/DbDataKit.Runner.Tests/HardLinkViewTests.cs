using System.Diagnostics;
using System.Security.Cryptography;

namespace DbDataKit.Runner.Tests;

/// <summary>
/// Plan 28a R-links: a view's files are hard links to the cache's objects, and a copy where a link is refused. Run on Linux and on Windows (the Windows job of dbdatarunner-ci.yml); the Windows
/// half is the one that was never run before: <c>CreateHardLinkW</c>, deleting a read-only link without making the shared object writable, and the .NET host started from a view.
/// </summary>
public sealed class HardLinkViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdatakit-links-" + Guid.NewGuid().ToString("N"));

    public HardLinkViewTests() => Directory.CreateDirectory(_root);

    public void Dispose() => ArtifactTestSupport.DeleteTree(_root);

    private string CacheDirectory => Path.Combine(_root, "cache");

    [Fact]
    public async Task A_view_file_is_the_object_under_another_name()
    {
        var source = new MemorySource();
        var file = source.Add("bin/Engine.dll", "engine");
        var cache = new ArtifactCache(CacheDirectory, source);

        using var view = await cache.MaterializeAsync("v1", [file]);

        var inView = view.PathOf("bin/Engine.dll");
        Assert.Null(new FileInfo(inView).LinkTarget);   // an ordinary file at its own path, which is what a host program that resolves links needs
        Assert.Equal("engine", await File.ReadAllTextAsync(inView));
        Assert.True(SameFile(inView, cache.ObjectPath(file.Sha256)), "a write to the object shows in the view, so they are one file");
        Assert.True(IsReadOnly(inView));
    }

    [Fact]
    public async Task A_refused_link_is_a_copy_with_the_same_content_that_is_not_the_object()
    {
        var source = new MemorySource();
        var file = source.Add("a/b.txt", "content");
        var asked = new List<(string Link, string Existing)>();
        var cache = new ArtifactCache(CacheDirectory, source, createLink: (link, existing) => { asked.Add((link, existing)); return false; });

        using var view = await cache.MaterializeAsync("v1", [file]);

        var inView = view.PathOf("a/b.txt");
        Assert.Single(asked);
        Assert.Equal(cache.ObjectPath(file.Sha256), asked[0].Existing);
        Assert.Equal("content", await File.ReadAllTextAsync(inView));
        Assert.False(SameFile(inView, cache.ObjectPath(file.Sha256)));
        Assert.True(IsReadOnly(inView));
    }

    [Fact]
    public async Task A_link_that_throws_is_a_copy_too()
    {
        var source = new MemorySource();
        var file = source.Add("a.txt", "content");
        var cache = new ArtifactCache(CacheDirectory, source, createLink: (_, _) => throw new IOException("Too many links"));

        using var view = await cache.MaterializeAsync("v1", [file]);

        Assert.Equal("content", await File.ReadAllTextAsync(view.PathOf("a.txt")));
        Assert.False(SameFile(view.PathOf("a.txt"), cache.ObjectPath(file.Sha256)));
    }

    [Fact]
    public async Task When_an_object_has_reached_its_link_limit_the_views_after_it_get_copies()
    {
        // the real limits are 1023 links to one file on NTFS and about 65000 on ext4; the limit is injected here (3 links) and counted per object as the file system does
        const int limit = 3;
        var source = new MemorySource();
        var file = source.Add("lib/shared.dll", "shared");
        var made = 0;
        var cache = new ArtifactCache(CacheDirectory, source, createLink: (link, existing) =>
        {
            if (made >= limit) return false;
            made++;
            return FileLinks.TryCreateHardLink(link, existing);
        });

        var views = new List<MaterializedVersion>();
        try
        {
            for (var i = 0; i < limit + 2; i++) views.Add(await cache.MaterializeAsync($"v{i}", [file]));

            var objectPath = cache.ObjectPath(file.Sha256);
            Assert.Equal(limit, views.Count(v => SameFile(v.PathOf("lib/shared.dll"), objectPath)));
            Assert.All(views, v => Assert.Equal("shared", File.ReadAllText(v.PathOf("lib/shared.dll"))));
            Assert.All(views, v => Assert.True(IsReadOnly(v.PathOf("lib/shared.dll"))));
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task Copy_mode_never_links()
    {
        var source = new MemorySource();
        var file = source.Add("a.txt", "content");
        var asked = 0;
        var cache = new ArtifactCache(CacheDirectory, source, ArtifactLinkMode.Copy, (_, _) => { asked++; return true; });

        using var view = await cache.MaterializeAsync("v1", [file]);

        Assert.Equal(0, asked);
        Assert.False(SameFile(view.PathOf("a.txt"), cache.ObjectPath(file.Sha256)));
    }

    [Fact]
    public async Task Removing_a_view_leaves_the_object_and_its_other_views_whole_and_read_only()
    {
        // on Windows the read-only attribute belongs to the file, not to a name: clearing it to delete a link must not make the cached object writable
        var source = new MemorySource();
        var file = source.Add("bin/a.dll", "bytes");
        var cache = new ArtifactCache(CacheDirectory, source);

        var first = await cache.MaterializeAsync("v1", [file]);
        using var second = await cache.MaterializeAsync("v2", [file]);
        var firstDirectory = first.Directory;
        first.Dispose();

        var objectPath = cache.ObjectPath(file.Sha256);
        Assert.False(Directory.Exists(firstDirectory));
        Assert.Equal("bytes", await File.ReadAllTextAsync(objectPath));
        Assert.Equal("bytes", await File.ReadAllTextAsync(second.PathOf("bin/a.dll")));
        Assert.True(IsReadOnly(objectPath), "the object is still read-only");
        Assert.True(IsReadOnly(second.PathOf("bin/a.dll")));
        Assert.True(SameFile(second.PathOf("bin/a.dll"), objectPath));
    }

    [Fact]
    public async Task An_object_a_live_view_holds_is_kept_by_eviction_and_removed_once_the_view_is_gone()
    {
        var source = new MemorySource();
        var file = source.Add("a.txt", "kept by the link");
        var cache = new ArtifactCache(CacheDirectory, source);
        var view = await cache.MaterializeAsync("v1", [file]);
        var path = view.PathOf("a.txt");

        var eviction = await cache.EvictAsync(new HashSet<string>());   // the view is live: the object stays
        Assert.Equal(1, eviction.KeptInUse);
        Assert.Equal("kept by the link", await File.ReadAllTextAsync(path));
        view.Dispose();
        eviction = await cache.EvictAsync(new HashSet<string>());
        Assert.Equal(1, eviction.Removed);
        Assert.Empty(cache.CachedHashes());
    }

    [Fact]
    public void A_link_onto_a_file_that_exists_or_from_one_that_does_not_is_refused_and_changes_nothing()
    {
        var one = Path.Combine(_root, "one.txt");
        var two = Path.Combine(_root, "two.txt");
        File.WriteAllText(one, "one");
        File.WriteAllText(two, "two");

        Assert.False(FileLinks.TryCreateHardLink(two, one));
        Assert.False(FileLinks.TryCreateHardLink(Path.Combine(_root, "three.txt"), Path.Combine(_root, "missing.txt")));
        Assert.False(FileLinks.TryCreateHardLink(Path.Combine(_root, "no-such-directory", "four.txt"), one));

        Assert.Equal("two", File.ReadAllText(two));
        Assert.False(File.Exists(Path.Combine(_root, "three.txt")));
        Assert.True(FileLinks.TryCreateHardLink(Path.Combine(_root, "five.txt"), one));
        Assert.Equal("one", File.ReadAllText(Path.Combine(_root, "five.txt")));
    }

    [Fact]
    public async Task A_view_deep_in_a_long_path_is_made_of_links_too()
    {
        // Windows' limit is 260 characters unless the call takes the \\?\ form; the cache directory here is deep enough that a view file is past it
        var deep = _root;
        while (deep.Length < 250) deep = Path.Combine(deep, "a-folder-with-a-long-name");
        var source = new MemorySource();
        var file = source.Add("lib/some/deeper/folder/Engine.Native.dll", "native");
        var cache = new ArtifactCache(Path.Combine(deep, "cache"), source);

        using var view = await cache.MaterializeAsync("v1", [file]);

        var inView = view.PathOf("lib/some/deeper/folder/Engine.Native.dll");
        Assert.True(inView.Length > 260, inView.Length.ToString());
        Assert.Equal("native", await File.ReadAllTextAsync(inView));
        Assert.True(SameFile(inView, cache.ObjectPath(file.Sha256)));
    }

    [Fact]
    public async Task A_program_the_dotnet_host_starts_from_a_view_finds_its_runtime_configuration_beside_itself()
    {
        // plan 28c E12e: objects are named by their hash, so a view made of symbolic links had `dotnet view/Engine.dll` look for Engine.runtimeconfig.json beside the object (exit code 131, "libhostpolicy not found");
        // with hard links the host sees ordinary files. The program is this test assembly (every file beside it that is not huge), started with --help.
        var assembly = Path.Combine(AppContext.BaseDirectory, "DbDataKit.Runner.Tests.dll");
        Assert.SkipUnless(File.Exists(assembly), "the test assembly's own files are not beside the test host");
        var source = new DiskSource(AppContext.BaseDirectory);
        var files = await source.ListAsync("self");
        var cache = new ArtifactCache(CacheDirectory, source);

        using var view = await cache.MaterializeAsync("hosted@1", files);

        var program = view.PathOf("DbDataKit.Runner.Tests.dll");
        Assert.Null(new FileInfo(program).LinkTarget);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        start.ArgumentList.Add(program);
        start.ArgumentList.Add("--help");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(limit.Token);

        Assert.True(process.ExitCode == 0, $"exit code {process.ExitCode}\n{await output}\n{await error}");
        Assert.Contains("Usage", await output);
    }

    /// <summary>True when the two paths are one file: a write to one is seen through the other (the object is read-only, so it is made writable for the moment).</summary>
    private static bool SameFile(string a, string b)
    {
        var before = File.GetAttributes(b);
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(b);
        try
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(b, FileAttributes.Normal);
            else File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var length = ArtifactTestSupport.LengthOf(a);
            using (var stream = new FileStream(b, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) stream.WriteByte((byte)'!');
            var same = ArtifactTestSupport.LengthOf(a) == length + 1;
            if (same)
            {
                // put the byte back, so the object is what it was
                using var stream = new FileStream(b, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(length);
            }
            else
            {
                using var stream = new FileStream(b, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(stream.Length - 1);
            }

            return same;
        }
        finally
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(b, before);
            else File.SetUnixFileMode(b, mode!.Value);
        }
    }

    private static bool IsReadOnly(string path)
    {
        const UnixFileMode noWrite = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        return OperatingSystem.IsWindows() ? File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly) : (File.GetUnixFileMode(path) & noWrite) == UnixFileMode.None;
    }

    private sealed class DiskSource(string directory) : IArtifactSource
    {
        public Task<IReadOnlyList<ArtifactFile>> ListAsync(string versionKey, CancellationToken cancellationToken = default)
        {
            var files = new List<ArtifactFile>();
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(path);
                if (info.Length > 8 * 1024 * 1024) continue;   // the DuckDB engine is a hundred megabytes and the program does not start it
                using var stream = File.OpenRead(path);
                files.Add(new ArtifactFile(info.Name, Convert.ToHexStringLower(SHA256.HashData(stream)), info.Length));
            }

            return Task.FromResult<IReadOnlyList<ArtifactFile>>(files);
        }

        public Task<Stream> OpenAsync(ArtifactFile file, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new FileStream(Path.Combine(directory, file.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }
}
