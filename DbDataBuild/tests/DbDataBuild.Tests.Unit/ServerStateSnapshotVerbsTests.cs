using DbDataBuild.Cli.Server;
using DbDataKit.Scheduling;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DbDataBuild.Tests.Unit;

/// <summary>The shared snapshot verbs on the server's command line (plan 29b X04b): the scheduler's state (what it has handled, per project and plan) survives an export and an import into a fresh directory.</summary>
public sealed class ServerStateSnapshotVerbsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-snap-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Dir(string name) => Path.Combine(root, name);

    private static async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync(args, o, e, TextReader.Null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Export_then_import_into_a_fresh_directory_keeps_the_schedulers_state_and_verify_finds_a_change()
    {
        var handled = new DateTimeOffset(2026, 11, 7, 6, 30, 0, TimeSpan.Zero);
        new ModuleJobStateStore(BuildSchedulerSchema.Open(Dir("server")), BuildSchedulerSchema.Service).SaveHandled("project-1", "refresh:daily", handled);
        SqliteConnection.ClearAllPools();

        var export = await Run("state", "export", "--to", Dir("snapshot"), "--repo", Dir("server"), "--include-secrets-check");
        Assert.True(export.Exit == 0, export.Out + export.Err);
        Assert.Contains("scheduling.", export.Out);

        var import = await Run("state", "import", "--from", Dir("snapshot"), "--repo", Dir("fresh"));
        Assert.True(import.Exit == 0, import.Out + import.Err);
        Assert.Contains("Start the server as usual", import.Out);

        var database = BuildSchedulerSchema.Open(Dir("fresh"));
        Assert.Equal(handled, new ModuleJobStateStore(database, BuildSchedulerSchema.Service).Get("project-1", "refresh:daily")!.HandledUpTo);
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, (await Run("state", "verify", "--snapshot", Dir("snapshot"))).Exit);
        Assert.Equal(0, (await Run("state", "verify", "--snapshot", Dir("snapshot"), "--db", "--repo", Dir("fresh"))).Exit);

        // a changed row is found and its table named
        new ModuleJobStateStore(BuildSchedulerSchema.Open(Dir("fresh")), BuildSchedulerSchema.Service).SaveHandled("project-1", "refresh:daily", handled.AddHours(1));
        SqliteConnection.ClearAllPools();
        var changed = await Run("state", "verify", "--snapshot", Dir("snapshot"), "--db", "--repo", Dir("fresh"));
        Assert.Equal(DbDataKit.State.Cli.StateExit.VerifyDiffers, changed.Exit);
        Assert.Contains("scheduling", changed.Out);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task State_move_to_a_server_engine_writes_the_state_section_of_server_yaml_and_the_server_then_reads_it(string engine)
    {
        using var db = ScratchStateDatabase.For(engine);
        if (db is null) return;
        BuildSchedulerSchema.Open(Dir("server")).OpenConnection().Dispose();
        SqliteConnection.ClearAllPools();

        var moved = await Run("state", "move", "--to-engine", engine, "--to-connection", db.Connection, "--yes", "--repo", Dir("server"));

        Assert.True(moved.Exit == 0, moved.Out + moved.Err);
        var text = File.ReadAllText(Path.Combine(Dir("server"), "server.yaml"));
        Assert.Contains($"engine: {engine}", text);
        Assert.DoesNotContain("DbKit_Test_Pw1", text);
        Assert.Equal(DbDataKit.State.StateSourceKind.File, BuildState.Resolve(Dir("server"), db.Environment).Sources.Engine.Kind);
    }
}
