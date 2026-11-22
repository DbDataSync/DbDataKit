using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Plan 13, E04 (clean break, D10): the settings files of earlier releases are refused with a message that names the file, and
/// the identity and paired clients that <c>server.yaml</c> shares with the settings are not read as settings.
/// </summary>
public sealed class LegacyHostSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbdatasync-legacy-settings-").FullName;
    private string Root => Path.Combine(_dir, "root");
    private string Exe => Path.Combine(_dir, "exe");

    public LegacyHostSettingsTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Exe);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NoOldFile_IsNotRefused() => Assert.Null(LegacyHostSettings.Refusal(Root, Exe));

    [Fact]
    public void AServerYamlAlone_IsNotRefused()
    {
        File.WriteAllText(Path.Combine(Root, "server.yaml"), "DbDataSync:\n  App:\n    Url: http://localhost:5080\n");

        Assert.Null(LegacyHostSettings.Refusal(Root, Exe));
    }

    [Fact]
    public void AnOldConfigFile_IsRefusedByName_AndSaysToSetTheServerUpAgain()
    {
        var old = Path.Combine(Root, "dbdatasync.config.yaml");
        File.WriteAllText(old, "DbDataSync:\n  Url: http://localhost:5080\n");

        var message = LegacyHostSettings.Refusal(Root, Exe);

        Assert.NotNull(message);
        Assert.Contains(old, message);
        Assert.Contains("no longer read", message);
        Assert.Contains("server setup", message);
        Assert.Contains(Path.Combine(Root, "server.yaml"), message);
    }

    [Theory]
    [InlineData("root", "appsettings.json")]
    [InlineData("root", "appsettings.Development.json")]
    [InlineData("config", "appsettings.json")]
    [InlineData("exe", "appsettings.json")]
    public void AnAppSettingsFile_IsRefusedByName_WhereverItIs(string where, string name)
    {
        var folder = where switch { "root" => Root, "exe" => Exe, _ => Path.Combine(Root, "config") };
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "{ \"Logging\": {} }");

        var message = LegacyHostSettings.Refusal(Root, Exe);

        Assert.NotNull(message);
        Assert.Contains(path, message);
        Assert.Contains("no longer read", message);
        Assert.Contains("server.yaml", message);
    }

    [Fact]
    public void TheOldPerUserFolder_IsRefusedWhileTheRootHasNoServerYaml_AndNotAfter()
    {
        var legacy = Path.Combine(_dir, "per-user");
        Directory.CreateDirectory(legacy);
        var old = Path.Combine(legacy, "dbdatasync.config.yaml");
        File.WriteAllText(old, "DbDataSync:\n  Url: http://localhost:5080\n");

        var message = LegacyHostSettings.Refusal(Root, Exe, legacy);
        Assert.NotNull(message);
        Assert.Contains(old, message);

        File.WriteAllText(Path.Combine(Root, "server.yaml"), "DbDataSync:\n  App:\n    Url: http://localhost:5080\n");
        Assert.Null(LegacyHostSettings.Refusal(Root, Exe, legacy));
    }

    [Fact]
    public void TheOldPerUserFolder_IsLookedAtOnlyWhenOneIsGiven()
    {
        var legacy = Path.Combine(_dir, "per-user");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "dbdatasync.config.yaml"), "DbDataSync:\n  Url: x\n");

        Assert.Null(LegacyHostSettings.Refusal(Root, Exe));
    }

    [Fact]
    public void TheIdentityAndPairedClients_AreNotSettings()
    {
        File.WriteAllText(Path.Combine(Root, "server.yaml"),
            "instanceId: sync-0123\npairedClients:\n  - {id: console-1, name: console, kind: console, publicKey: abc, ceiling: [admin], paired: \"2026-10-30T00:00:00Z\"}\nDbDataSync:\n  App:\n    Url: http://localhost:5080\n");

        var settings = DbDataSyncConfigFile.Read(Root);

        Assert.Equal(["DbDataSync:App:Url"], settings.Keys);
    }
}
