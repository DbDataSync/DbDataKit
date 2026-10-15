using DbData.Update.Tools;

namespace DbData.Update.Tools.Tests;

public sealed class Cli : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "dbdut-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string?> Env = new();
    public Cli() => Directory.CreateDirectory(Dir);
    public string P(string n) => Path.Combine(Dir, n);

    public (int Code, string Out, string Err) Run(params string[] a)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var c = UpdateToolCli.Run(a, o, e, k => Env.GetValueOrDefault(k));
        return (c, o.ToString(), e.ToString());
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class CliTests
{
    private static string PublicKeyOf(string output) => output.Split('\n').First(l => l.StartsWith("public-key: ")).Substring(12).Trim();

    private static (Cli cli, string pub, string art) Setup()
    {
        var c = new Cli();
        var r = c.Run("keys", "generate", "--key-id", "k1", "--out-private", c.P("k1.key"));
        Assert.Equal(0, r.Code);
        var art = Path.Combine(c.Dir, "art");
        Directory.CreateDirectory(art);
        File.WriteAllText(Path.Combine(art, "dbdataproxy-1.4.0-win-x64.zip"), "zip-bytes");
        File.WriteAllText(Path.Combine(art, "dbdataproxy-1.4.0-linux-x64.tar.gz"), "tar-bytes");
        File.WriteAllText(Path.Combine(art, "SHA256SUMS"), "ignored");
        return (c, PublicKeyOf(r.Out), art);
    }

    [Fact]
    public void CreateSignVerify_RoundTrip()
    {
        var (c, pub, art) = Setup();
        using var _ = c;
        var cr = c.Run("manifest", "create", "--product", "dbdataproxy", "--version", "1.4.0", "--artifacts", art,
            "--base-url", "https://github.com/o/r/releases/download/v1.4.0", "--out", c.P("release.json"),
            "--min-updater", "1.0", "--requires-config-migration", "--schema-version", "config=3", "--supersedes", "1.3.0", "--notes", "hello");
        Assert.Equal(0, cr.Code);
        var m = ReleaseManifest.Parse(File.ReadAllBytes(c.P("release.json")));
        Assert.Equal(2, m.Artifacts.Count);
        Assert.Contains(m.Artifacts, a => a.Rid == "win-x64" && a.Kind == "zip" && a.Url.EndsWith("/dbdataproxy-1.4.0-win-x64.zip"));
        Assert.Contains(m.Artifacts, a => a.Rid == "linux-x64" && a.Kind == "tarball");
        Assert.True(m.RequiresConfigMigration);
        Assert.Equal(3, m.SchemaVersions["config"]);
        Assert.Equal(["1.3.0"], m.Supersedes);

        var sg = c.Run("manifest", "sign", "--manifest", c.P("release.json"), "--key-file", c.P("k1.key"), "--key-id", "k1");
        Assert.Equal(0, sg.Code);
        var vr = c.Run("manifest", "verify", "--manifest", c.P("release.json"), "--trusted-key", $"k1={pub}", "--product", "dbdataproxy", "--artifacts", art);
        Assert.Equal(0, vr.Code);
        Assert.Contains("OK: dbdataproxy 1.4.0", vr.Out);
    }

    [Fact]
    public void Sign_FromEnvVariable_ForCi()
    {
        var (c, pub, art) = Setup();
        using var _ = c;
        c.Run("manifest", "create", "--product", "dbdataproxy", "--version", "1.4.0", "--artifacts", art, "--base-url", "https://x/y", "--out", c.P("release.json"));
        c.Env[UpdateToolCli.SigningKeyEnv] = File.ReadAllText(c.P("k1.key"));
        Assert.Equal(0, c.Run("manifest", "sign", "--manifest", c.P("release.json"), "--key-id", "k1").Code);
        Assert.Equal(0, c.Run("manifest", "verify", "--manifest", c.P("release.json"), "--trusted-key", $"k1={pub}").Code);
    }

    [Fact]
    public void Verify_FailsOnTamperedManifest_WrongKey_AndTamperedArtifact()
    {
        var (c, pub, art) = Setup();
        using var _ = c;
        c.Run("manifest", "create", "--product", "dbdataproxy", "--version", "1.4.0", "--artifacts", art, "--base-url", "https://x/y", "--out", c.P("release.json"));
        c.Run("manifest", "sign", "--manifest", c.P("release.json"), "--key-file", c.P("k1.key"), "--key-id", "k1");

        // artifact tampered after signing
        File.WriteAllText(Path.Combine(art, "dbdataproxy-1.4.0-win-x64.zip"), "zip-bytez");
        var r1 = c.Run("manifest", "verify", "--manifest", c.P("release.json"), "--trusted-key", $"k1={pub}", "--artifacts", art);
        Assert.Equal(1, r1.Code);
        Assert.Contains("HashMismatch", r1.Err);
        File.WriteAllText(Path.Combine(art, "dbdataproxy-1.4.0-win-x64.zip"), "zip-bytes");

        // wrong key
        var other = c.Run("keys", "generate", "--key-id", "k1", "--out-private", c.P("other.key"));
        var r2 = c.Run("manifest", "verify", "--manifest", c.P("release.json"), "--trusted-key", $"k1={PublicKeyOf(other.Out)}");
        Assert.Equal(1, r2.Code);
        Assert.Contains("BadSignature", r2.Err);

        // manifest tampered
        File.WriteAllText(c.P("release.json"), File.ReadAllText(c.P("release.json")).Replace("x/y", "evil/y"));
        var r3 = c.Run("manifest", "verify", "--manifest", c.P("release.json"), "--trusted-key", $"k1={pub}");
        Assert.Equal(1, r3.Code);
        Assert.Contains("BadSignature", r3.Err);
    }

    [Fact]
    public void KeyRotation_ViaCli_ProducesAcceptedKeyManifest()
    {
        var (c, pub, _) = Setup();
        using var _ = c;
        var next = c.Run("keys", "generate", "--key-id", "k2", "--out-private", c.P("k2.key"));
        var r = c.Run("keys", "rotate", "--sequence", "1", "--signer-id", "k1", "--key-file", c.P("k1.key"),
            "--add", $"k2={PublicKeyOf(next.Out)}", "--retire", "k1", "--out", c.P("keys.json"));
        Assert.Equal(0, r.Code);
        var store = TrustStore.FromBase64("k1", pub).Rotate(File.ReadAllBytes(c.P("keys.json")));
        Assert.True(store.Contains("k2"));
        Assert.False(store.Contains("k1"));
    }

    [Fact]
    public void KeyGenerate_RefusesToOverwrite()
    {
        var (c, _, _) = Setup();
        using var _ = c;
        var r = c.Run("keys", "generate", "--key-id", "k1", "--out-private", c.P("k1.key"));
        Assert.Equal(1, r.Code);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("manifest", "create")]
    [InlineData("manifest", "bogus")]
    public void UsageErrors_ReturnNonZero(params string[] args)
    {
        using var c = new Cli();
        Assert.Equal(1, c.Run(args).Code);
    }

    [Fact]
    public void Create_RejectsChannelVersionMismatch()
    {
        var (c, _, art) = Setup();
        using var _ = c;
        var r = c.Run("manifest", "create", "--product", "p", "--version", "1.4.0", "--channel", "beta", "--artifacts", art, "--base-url", "https://x");
        Assert.Equal(1, r.Code);
    }
}
