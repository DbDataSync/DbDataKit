using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace DbData.Update.Tests;

public class DownloaderTests
{
    private const string Prefix = "https://github.com/o/r/releases/download/";

    private static ReleaseArtifact Art(byte[] data, string url = Prefix + "v1/app.zip", long? size = null) => new()
    {
        Kind = "zip", Url = url, Size = size ?? data.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
    };

    private static Downloader D(Func<HttpRequestMessage, HttpResponseMessage> f, out StubHandler h)
    {
        h = new StubHandler(f);
        return new Downloader(new HttpContentFetcher(new HttpClient(h)), new TrustedUrlPolicy([Prefix]));
    }

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/app.zip", true)]
    [InlineData("https://github.com/o/r/releases/download/v1/../../evil.zip", false)]
    [InlineData("https://github.com/o/r/releases/download/%2e%2e/x.zip", false)]
    [InlineData("https://github.com/o/r/releases/downloadX/app.zip", false)]
    [InlineData("https://github.com.evil.com/o/r/releases/download/v1/app.zip", false)]
    [InlineData("https://user@github.com/o/r/releases/download/v1/app.zip", false)]
    [InlineData("http://github.com/o/r/releases/download/v1/app.zip", false)]
    [InlineData("https://github.com:444/o/r/releases/download/v1/app.zip", false)]
    [InlineData("https://github.com/o/r/releases/latest/app.zip", false)]
    public void TrustedPrefixOnly(string url, bool expected)
        => Assert.Equal(expected, new TrustedUrlPolicy([Prefix]).IsTrusted(new Uri(url)));

    [Fact]
    public async Task UntrustedUrlNeverFetched()
    {
        using var t = new TempDir();
        var data = new byte[] { 1, 2, 3 };
        var d = D(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }, out var h);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(Art(data, "https://evil.example/app.zip"), null, t.Path));
        Assert.Equal(VerificationFailure.UntrustedUrl, ex.Failure);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task DownloadsAndVerifies()
    {
        using var t = new TempDir();
        var data = RandomNumberGenerator.GetBytes(50_000);
        var d = D(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }, out _);
        var path = await d.DownloadAsync(Art(data), null, t.Path);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(t.Path, "*.partial"));
    }

    [Fact]
    public async Task ResumesFromPartial()
    {
        using var t = new TempDir();
        var data = RandomNumberGenerator.GetBytes(10_000);
        var art = Art(data);
        await File.WriteAllBytesAsync(Path.Combine(t.Path, art.Sha256 + ".partial"), data[..4000]);
        long? from = null;
        var d = D(req =>
        {
            from = req.Headers.Range?.Ranges.First().From;
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)from!.Value..]) };
        }, out _);
        var path = await d.DownloadAsync(art, null, t.Path);
        Assert.Equal(4000, from);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task TamperedBytesRejectedAndPartialDeleted()
    {
        using var t = new TempDir();
        var data = RandomNumberGenerator.GetBytes(1000);
        var evil = (byte[])data.Clone();
        evil[10] ^= 0xFF;
        var d = D(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(evil) }, out _);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(Art(data), null, t.Path));
        Assert.Equal(VerificationFailure.HashMismatch, ex.Failure);
        Assert.Empty(Directory.GetFiles(t.Path));
    }

    [Fact]
    public async Task OversizedResponseRejected()
    {
        using var t = new TempDir();
        var data = new byte[1000];
        var d = D(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) }, out _);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(Art(data), null, t.Path));
        Assert.Equal(VerificationFailure.SizeMismatch, ex.Failure);
        Assert.Empty(Directory.GetFiles(t.Path));
    }

    [Fact]
    public async Task TruncatedDownloadKeepsPartialForResume()
    {
        using var t = new TempDir();
        var data = new byte[1000];
        var d = D(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data[..400]) }, out _);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => d.DownloadAsync(Art(data), null, t.Path));
        Assert.Single(Directory.GetFiles(t.Path, "*.partial"));
    }

    [Fact]
    public async Task FolderSourceRelativeUrlResolvesUnderManifestFolder()
    {
        using var t = new TempDir();
        var data = TestKit.MakeZip(("a", "b"));
        var dir = Directory.CreateDirectory(Path.Combine(t.Path, "rel")).FullName;
        File.WriteAllBytes(Path.Combine(dir, "app.zip"), data);
        var manifestLoc = new Uri(Path.Combine(dir, "release.json"));
        var dl = new Downloader(new FileContentFetcher(), new TrustedUrlPolicy([new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri]));
        var p = await dl.DownloadAsync(Art(data, "app.zip"), manifestLoc, Path.Combine(t.Path, "out"));
        Assert.True(File.Exists(p));
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => dl.DownloadAsync(Art(data, "../app.zip"), manifestLoc, Path.Combine(t.Path, "out2")));
        Assert.Equal(VerificationFailure.UntrustedUrl, ex.Failure);
    }
}

public class ExtractorTests
{
    private static void AssertUnsafe(Action a) => Assert.Equal(VerificationFailure.UnsafeArchive, Assert.Throws<UpdateVerificationException>(a).Failure);

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("C:/evil.txt")]
    public void ZipSlipRejectedBeforeAnythingIsWritten(string name)
    {
        using var t = new TempDir();
        var zip = Path.Combine(t.Path, "z.zip");
        File.WriteAllBytes(zip, TestKit.MakeZip(("ok.txt", "fine"), (name, "bad")));
        var dest = Path.Combine(t.Path, "out");
        AssertUnsafe(() => ArchiveExtractor.Extract(zip, dest));
        Assert.False(File.Exists(Path.Combine(dest, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(t.Path, "evil.txt")));
    }

    [Fact]
    public void ExtractsNormalZipWithNesting()
    {
        using var t = new TempDir();
        var zip = Path.Combine(t.Path, "z.bin");
        File.WriteAllBytes(zip, TestKit.MakeZip(("app.exe", "x"), ("lib/a.dll", "y")));
        ArchiveExtractor.Extract(zip, Path.Combine(t.Path, "out"));
        Assert.Equal("y", File.ReadAllText(Path.Combine(t.Path, "out", "lib", "a.dll")));
    }

    [Fact]
    public void ZipBombLimitEnforced()
    {
        using var t = new TempDir();
        var zip = Path.Combine(t.Path, "z.zip");
        File.WriteAllBytes(zip, TestKit.MakeZip(("big.txt", new string('a', 100_000))));
        AssertUnsafe(() => ArchiveExtractor.ExtractZip(zip, Path.Combine(t.Path, "out"), new ExtractionLimits(MaxTotalBytes: 1000)));
        AssertUnsafe(() => ArchiveExtractor.ExtractZip(zip, Path.Combine(t.Path, "out"), new ExtractionLimits(MaxEntries: 0)));
    }

    private static string MakeTarGz(string dir, Action<TarWriter> fill)
    {
        var path = Path.Combine(dir, "a.tar.gz");
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tw = new TarWriter(gz, TarEntryFormat.Pax);
        fill(tw);
        return path;
    }

    [Fact]
    public void ExtractsTarGz()
    {
        using var t = new TempDir();
        var p = MakeTarGz(t.Path, tw =>
        {
            var e = new PaxTarEntry(TarEntryType.RegularFile, "bin/app") { DataStream = new MemoryStream("hi"u8.ToArray()) };
            tw.WriteEntry(e);
        });
        ArchiveExtractor.Extract(p, Path.Combine(t.Path, "out"));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(t.Path, "out", "bin", "app")));
    }

    [Fact]
    public void TarTraversalRejected()
    {
        using var t = new TempDir();
        var p = MakeTarGz(t.Path, tw => tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../evil") { DataStream = new MemoryStream([1]) }));
        AssertUnsafe(() => ArchiveExtractor.Extract(p, Path.Combine(t.Path, "out")));
    }

    [Fact]
    public void TarSymlinkRejected()
    {
        using var t = new TempDir();
        var p = MakeTarGz(t.Path, tw => tw.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc" }));
        AssertUnsafe(() => ArchiveExtractor.Extract(p, Path.Combine(t.Path, "out")));
    }
}
