using System.IO.Compression;
using System.Text;

namespace DbData.Update.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbdu-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Combine(params string[] p) => System.IO.Path.Combine([Path, .. p]);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
}

public sealed class KeyPair
{
    public byte[] Seed { get; }
    public byte[] Public { get; }
    public string Id { get; }
    public KeyPair(string id) { Id = id; (Seed, Public) = Ed25519.GenerateKeyPair(); }
    public TrustStore Trust() => new([new TrustedKey(Id, Public)]);
}

public static class TestKit
{
    public static byte[] MakeZip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, c) in files)
            {
                var e = z.CreateEntry(n);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(c);
            }
        return ms.ToArray();
    }

    /// <summary>Writes a signed release into <c>root/&lt;version&gt;/</c> (folder-source layout). Returns manifest and artifact file path.</summary>
    public static (ReleaseManifest Manifest, string Dir) WriteRelease(string root, KeyPair key, string version, byte[] artifact, Action<ManifestBuildOptions>? _ = null,
        Func<ReleaseManifest, ReleaseManifest>? mutate = null, string product = "dbdataproxy", string? channel = null)
    {
        var dir = System.IO.Path.Combine(root, version);
        Directory.CreateDirectory(dir);
        var file = $"{product}-{version}-win-x64.zip";
        File.WriteAllBytes(System.IO.Path.Combine(dir, file), artifact);
        var m = ManifestBuilder.Build(new ManifestBuildOptions
        {
            Product = product, Version = version, Channel = channel ?? "",
            BaseUrl = new Uri(dir + System.IO.Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/'),
        }, dir);
        File.Delete(System.IO.Path.Combine(dir, "release.json"));
        if (mutate is not null) m = mutate(m);
        var bytes = ManifestBuilder.ToBytes(m);
        File.WriteAllBytes(System.IO.Path.Combine(dir, "release.json"), bytes);
        File.WriteAllText(System.IO.Path.Combine(dir, "release.json.sig"), ManifestSigner.Sign(bytes, key.Seed, key.Id));
        return (m, dir);
    }
}

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}
