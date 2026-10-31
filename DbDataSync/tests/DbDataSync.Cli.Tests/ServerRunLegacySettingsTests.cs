using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Plan 13, E04 and D10 (clean break): <c>server run</c> reads only <c>server.yaml</c>. An old install (a <c>dbdatasync.config.yaml</c>, an <c>appsettings.json</c>)
/// stops it with a message that names the file, and nothing is created beside it; the migrations that used to rewrite the old files are gone.
/// </summary>
public sealed class ServerRunLegacySettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-server-run-legacy-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Theory]
    [InlineData("dbdatasync.config.yaml", "DbDataSync:\n  Url: http://localhost:5080\n")]
    [InlineData("appsettings.json", "{ \"DbDataSync\": { \"Url\": \"http://localhost:5080\" } }")]
    public async Task AnOldSettingsFile_StopsServerRun_NamingTheFile_AndCreatesNothing(string name, string content)
    {
        var old = Path.Combine(_root, name);
        File.WriteAllText(old, content);

        var (exitCode, error) = await RunServer();

        Assert.Equal(1, exitCode);
        Assert.Contains(old, error);
        Assert.Contains("no longer read", error);
        Assert.False(File.Exists(DbDataSyncConfigFile.PathIn(_root)), "nothing is written beside an old install");
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public async Task ConfigCheck_NamesTheOldFile()
    {
        ServeCommand.Prepare(_root);
        var old = Path.Combine(_root, "dbdatasync.config.yaml");
        File.WriteAllText(old, "DbDataSync:\n  Url: http://localhost:5080\n");

        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = await ConfigCommand.RunAsync(["check", "--repo", _root]);

            Assert.Equal(1, exitCode);
            Assert.Contains(old, output.ToString());
            Assert.Contains("no longer read", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void TheOldMigrations_AreGone()
    {
        var assembly = typeof(ServeCommand).Assembly;

        Assert.Null(assembly.GetType("DbDataSync.Cli.LegacyConfigMigration"));
        Assert.Null(assembly.GetType("DbDataSync.Cli.LegacyRootMigration"));
    }

    [Fact]
    public void AnIdentityOnlyServerYaml_IsNotAnExistingSetup()
    {
        File.WriteAllText(DbDataSyncConfigFile.PathIn(_root), "instanceId: sync-0123\npairedClients:\n  - {id: c, name: c, kind: console, publicKey: k, ceiling: [admin]}\n");

        Assert.False(ExistingSetup.DetectedAt(_root));
    }

    private async Task<(int ExitCode, string Error)> RunServer()
    {
        var originalErr = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            return (await ServeCommand.RunAsync(["--repo", _root]), error.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
