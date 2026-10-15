using System.IO.Compression;
using System.Text;

namespace DbDataKit.Update.Service.Tests;

public sealed class KeyPair
{
    public byte[] Seed { get; }
    public byte[] Public { get; }
    public string Id { get; }
    public KeyPair(string id) { Id = id; (Seed, Public) = Ed25519.GenerateKeyPair(); }
    public TrustStore Trust() => new([new TrustedKey(Id, Public)]);
}

public sealed class FakeService : IServiceControl
{
    public bool Running { get; set; } = true;
    public List<string> Calls { get; } = new();
    public Func<Task>? OnStart { get; set; }
    public Task<bool> IsRunningAsync(CancellationToken ct) => Task.FromResult(Running);
    public Task StopAsync(TimeSpan timeout, CancellationToken ct) { Calls.Add("stop"); Running = false; return Task.CompletedTask; }
    public async Task StartAsync(CancellationToken ct) { Calls.Add("start"); Running = true; if (OnStart is not null) await OnStart(); }
}

public sealed class FakeHealth : IHealthProbe
{
    /// <summary>Versions that come up healthy.</summary>
    public HashSet<string> Healthy { get; } = new();
    public bool ThrowCancel { get; set; }
    public Task<HealthResult> WaitHealthyAsync(string expectedVersion, string? healthUrl, TimeSpan timeout, CancellationToken ct)
    {
        if (ThrowCancel) throw new OperationCanceledException();
        return Task.FromResult(new HealthResult(Healthy.Contains(expectedVersion), "fake"));
    }
}

public sealed class FakeMigration : IMigrationRunner
{
    public bool Irreversible { get; set; }
    public bool ThrowCancel { get; set; }
    public int Runs { get; private set; }
    public bool HasIrreversibleSteps(UpdatePlan plan) => Irreversible;
    public Task MigrateAsync(MigrationContext c, CancellationToken ct)
    {
        Runs++;
        if (ThrowCancel) throw new OperationCanceledException();
        File.WriteAllText(Path.Combine(c.ConfigDir, "app.json"), "migrated");
        return Task.CompletedTask;
    }
}

public sealed class FakeBackup(bool ok) : IPreUpdateBackup
{
    public Task<bool> BackupAsync(MigrationContext c, CancellationToken ct) => Task.FromResult(ok);
}

/// <summary>A machine at version 1.0.0 with a staged release ready to apply.</summary>
public sealed class Env : IDisposable
{
    public string Root = Path.Combine(Path.GetTempPath(), "dbdus-" + Guid.NewGuid().ToString("N"));
    public InstallLayout Layout;
    public MarkerFileCurrentPointer Pointer;
    public KeyPair Key = new("k1");
    public FakeService Service = new();
    public FakeHealth Health = new();
    public FakeMigration Migration = new();
    public const string Product = "dbdataproxy";

    public Env(string installed = "1.0.0")
    {
        Layout = new InstallLayout(Root);
        Pointer = new MarkerFileCurrentPointer(Layout);
        Directory.CreateDirectory(Path.Combine(Layout.VersionsDir, installed));
        File.WriteAllText(Path.Combine(Layout.VersionsDir, installed, "app.exe"), "old " + installed);
        Directory.CreateDirectory(Layout.ConfigDir);
        File.WriteAllText(Path.Combine(Layout.ConfigDir, "app.json"), "v1");
        Pointer.Set(installed);
        Installed = installed;
    }

    public string Installed { get; }

    public static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, c) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false));
                w.Write(c);
            }
        return ms.ToArray();
    }

    /// <summary>Build, sign and stage a release; returns the plan (already saved). <paramref name="tweak"/> mutates the manifest before signing.</summary>
    public UpdatePlan Stage(string version, byte[]? artifact = null, Func<ReleaseManifest, ReleaseManifest>? tweak = null, KeyPair? signer = null, string? signerId = null)
    {
        var work = Path.Combine(Root, "_work", version);
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, $"{Product}-{version}-win-x64.zip"), artifact ?? Zip(("app.exe", "new " + version)));
        var m = ManifestBuilder.Build(new ManifestBuildOptions { Product = Product, Version = version, BaseUrl = "https://example.invalid/dl" }, work);
        if (tweak is not null) m = tweak(m);
        var bytes = ManifestBuilder.ToBytes(m);
        var s = signer ?? Key;
        var sig = ManifestSigner.Sign(bytes, s.Seed, signerId ?? s.Id);
        return UpdateStager.Stage(Layout, Product, Installed, bytes, sig, Path.Combine(work, $"{Product}-{version}-win-x64.zip"), "win-x64", migrationSteps: ["config:3"]);
    }

    public UpdateHelper Helper(UpdateHelperOptions? o = null, IPreUpdateBackup? backup = null, TrustStore? trust = null)
        => new(Layout, Pointer, Service, Health, Migration, trust ?? Key.Trust(),
            (o ?? new()) with { Product = Product, Channel = Channel.Snapshot }, backup);

    public string ConfigText => File.ReadAllText(Path.Combine(Layout.ConfigDir, "app.json"));

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
