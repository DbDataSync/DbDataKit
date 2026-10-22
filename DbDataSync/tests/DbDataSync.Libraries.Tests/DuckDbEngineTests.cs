using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DbDataSync.Libraries;

namespace DbDataSync.Libraries.Tests;

/// <summary>
/// The DuckDB engine is added to the `duckdb` library from the repository's pin (plan 13, K05). These use a small fake archive and a pin
/// that describes it, so nothing is downloaded.
/// </summary>
public sealed class DuckDbEngineTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dbdatasync-duckdbengine-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _libDir;
    private readonly byte[] _library = Encoding.ASCII.GetBytes(new string('E', 5000));
    private string _pin = "";

    public DuckDbEngineTests()
    {
        _source = Path.Combine(_work, "source");
        _libDir = Path.Combine(_work, "lib");
        Directory.CreateDirectory(_source);
        var archive = Path.Combine(_source, "engine-linux.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "libduckdb.so") { DataStream = new MemoryStream(_library) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "duckdb.h") { DataStream = new MemoryStream("header"u8.ToArray()) });
        }
        _pin = $"""
            # a test pin
            VERSION=9.9.9
            BASE=https://example.invalid/releases/v9.9.9
            ARCHIVE_linux_x64=engine-linux.tar.gz
            ARCHIVE_SHA256_linux_x64={Hash(File.ReadAllBytes(archive))}
            LIB_linux_x64=libduckdb.so
            LIB_SHA256_linux_x64={Hash(_library)}
            """;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task TheEngineIsExtractedFromTheArchiveAndVerified()
    {
        await DuckDbEngine.EnsureAsync(_libDir, _pin, "linux-x64", _source);
        Assert.Equal(_library, await File.ReadAllBytesAsync(Path.Combine(_libDir, "libduckdb.so")));
    }

    [Fact]
    public async Task AnEngineThatIsAlreadyThereIsLeftAlone()
    {
        await DuckDbEngine.EnsureAsync(_libDir, _pin, "linux-x64", _source);
        var path = Path.Combine(_libDir, "libduckdb.so");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        // The source is gone: nothing needs to be fetched.
        await DuckDbEngine.EnsureAsync(_libDir, _pin, "linux-x64", Path.Combine(_work, "nowhere"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task AnInstalledEngineThatDoesNotMatchThePinIsReplaced()
    {
        Directory.CreateDirectory(_libDir);
        await File.WriteAllBytesAsync(Path.Combine(_libDir, "libduckdb.so"), "changed"u8.ToArray());
        await DuckDbEngine.EnsureAsync(_libDir, _pin, "linux-x64", _source);
        Assert.Equal(_library, await File.ReadAllBytesAsync(Path.Combine(_libDir, "libduckdb.so")));
    }

    [Fact]
    public async Task AChangedByteInTheArchiveFailsAndInstallsNothing()
    {
        var archive = Path.Combine(_source, "engine-linux.tar.gz");
        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DuckDbEngine.EnsureAsync(_libDir, _pin, "linux-x64", _source));
        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(Path.Combine(_libDir, "libduckdb.so")));
    }

    [Fact]
    public async Task ALibraryThatIsNotThePinnedOneFailsEvenInAnArchiveThatMatches()
    {
        var wrong = _pin.Replace($"LIB_SHA256_linux_x64={Hash(_library)}", "LIB_SHA256_linux_x64=" + new string('0', 64));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DuckDbEngine.EnsureAsync(_libDir, wrong, "linux-x64", _source));
        Assert.Contains("inside", ex.Message);
        Assert.False(File.Exists(Path.Combine(_libDir, "libduckdb.so")));
    }

    [Fact]
    public async Task ASourceDirectoryWithoutTheArchiveSaysSo()
    {
        var empty = Path.Combine(_work, "empty");
        Directory.CreateDirectory(empty);
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => DuckDbEngine.EnsureAsync(_libDir, _pin, "linux-x64", empty));
        Assert.Contains(DuckDbEngine.SourceVariable, ex.Message);
        Assert.Contains("engine-linux.tar.gz", ex.Message);
    }

    [Fact]
    public async Task APlatformWithoutAnEngineIsRefusedWithAMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DuckDbEngine.EnsureAsync(_libDir, _pin, rid: null, _source));
        Assert.Contains("No DuckDB engine is pinned for this platform", ex.Message);
    }

    [Fact]
    public async Task APinWithoutThePlatformNamesTheMissingKey()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DuckDbEngine.EnsureAsync(_libDir, _pin, "win-x64", _source));
        Assert.Contains("ARCHIVE_win_x64", ex.Message);
    }

    [Fact]
    public void OnlyTheBareBindingsPackageNeedsTheEngine()
    {
        Assert.True(DuckDbEngine.Needs([new PackageRef("DuckDB.NET.Data", "1.5.6")]));
        Assert.True(DuckDbEngine.Needs([new PackageRef("duckdb.net.data", "1.5.6")]));
        Assert.False(DuckDbEngine.Needs([new PackageRef("DuckDB.NET.Data.Full", "1.5.6")]));
        Assert.False(DuckDbEngine.Needs([new PackageRef("Npgsql", "10.0.3")]));
    }

    [Fact]
    public void TheEmbeddedPinIsTheRepositorysAndNamesEveryPinnedPlatform()
    {
        var pin = DuckDbEngine.ParsePin(DuckDbEngine.ReadEmbeddedPin());
        Assert.Equal("2.0.0", DuckDbEngine.PinnedVersion);
        foreach (var rid in new[] { "linux_x64", "linux_arm64", "win_x64", "osx_arm64", "osx_x64" })
            Assert.True(pin.ContainsKey("LIB_SHA256_" + rid), rid);
    }
}
