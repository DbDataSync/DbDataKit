using DbDataKit.Cli;
using DbDataKit.State.Cli;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.Data.Sqlite;

namespace DbDataStore.Tests;

/// <summary>The snapshot verbs on the server's command line (plan 29b X04b): its state (the pairing codes and clients of the trust module) survives an export and an import into a fresh directory.</summary>
public sealed class StoreStateSnapshotVerbsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ddk-store-snap").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(root, name);

    private static async Task<(int Exit, string Out, string Error, IDictionary<string, object?> Data)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CliContext(output, error, TextReader.Null);
        var exit = await StoreCli.Router().RunAsync(args, context);
        return (exit, output.ToString(), error.ToString(), context.Data);
    }

    [Fact]
    public async Task Export_then_import_into_a_fresh_directory_keeps_the_pairing_codes_and_verifies_equal()
    {
        var codes = StoreState.Pairing(Dir("server")).Codes();
        codes.Create(ServerRoles.Close(ServerRole.Admin), "first");
        codes.Create(ServerRoles.Close(ServerRole.Author), "second");
        SqliteConnection.ClearAllPools();

        var export = await Run("state", "export", "--to", Dir("snapshot"), "--repo", Dir("server"), "--include-secrets-check");
        Assert.True(export.Exit == 0, export.Out + export.Error);
        Assert.Contains("trust.", export.Out);

        var import = await Run("state", "import", "--from", Dir("snapshot"), "--repo", Dir("fresh"));
        Assert.True(import.Exit == 0, import.Out + import.Error);
        Assert.Contains("Start the server as usual", import.Out);

        Assert.Equal(2, StoreState.Pairing(Dir("fresh")).Codes().Pending());
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, (await Run("state", "verify", "--snapshot", Dir("snapshot"))).Exit);
        var against = await Run("state", "verify", "--snapshot", Dir("snapshot"), "--db", "--repo", Dir("fresh"));
        Assert.True(against.Exit == 0, against.Out + against.Error);
        var other = await Run("state", "verify", "--other-engine", "sqlite", "--other-sqlite", Path.Combine(Dir("server"), "state.db"), "--repo", Dir("fresh"));
        Assert.True(other.Exit == 0, other.Out + other.Error);

        // a second import into the same directory is refused: it is no longer empty
        Assert.Equal(StateExit.TargetNotEmpty, (await Run("state", "import", "--from", Dir("snapshot"), "--repo", Dir("fresh"))).Exit);
    }

    [Fact]
    public async Task State_move_says_the_server_has_no_settings_file_for_it_and_changes_nothing()
    {
        StoreState.Pairing(Dir("server")).Codes().Create(ServerRoles.Close(ServerRole.Admin));
        SqliteConnection.ClearAllPools();

        var moved = await Run("state", "move", "--to-engine", "postgres", "--to-connection", "Host=h;Database=d;Username=u;Password=env:X", "--yes", "--repo", Dir("server"));

        Assert.Equal(StateExit.Settings, moved.Exit);
        Assert.Contains("state export", moved.Error);
        Assert.False(File.Exists(Path.Combine(Dir("server"), "server.yaml")));
    }
}
