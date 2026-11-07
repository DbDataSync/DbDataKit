using DbDataKit.Cli;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>The shared <c>state show|test|grants</c> verbs on the server's command line (plan 29b, C01).</summary>
public sealed class StoreStateVerbsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-store-state-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await StoreCli.Router().RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task State_show_and_test_read_the_servers_state_and_create_nothing()
    {
        var before = await Run("state", "show", "--repo", directory);
        Assert.Equal(0, before.Exit);
        Assert.Contains("not created yet", before.Out);
        Assert.Contains("trust", before.Out);
        Assert.False(Directory.Exists(directory), "state show made the server's directory");

        StoreState.Open(directory).OpenConnection().Dispose();
        var shown = await Run("state", "show", "--repo", directory);
        Assert.Matches(@"trust\S*\s+.*registered v\d+\s+current", shown.Out);

        var tested = await Run("state", "test", "--repo", directory);
        Assert.Equal(0, tested.Exit);
        Assert.Contains("nothing is pending", tested.Out);

        Assert.Equal(CliExit.Usage, (await Run("state", "grants", "--login", "svc", "--repo", directory)).Exit);   // SQLite: nothing to grant
    }
}
