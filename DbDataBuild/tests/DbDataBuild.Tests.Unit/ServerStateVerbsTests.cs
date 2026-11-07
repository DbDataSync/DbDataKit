using DbDataBuild.Cli.Server;
using Xunit;

namespace DbDataBuild.Tests.Unit;

/// <summary>The shared <c>state show|test|grants</c> verbs on the server's command line (plan 29b, C01).</summary>
public sealed class ServerStateVerbsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-state-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync([.. args, "--repo", dir], o, e, TextReader.Null);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task State_show_and_test_read_the_servers_state_and_create_nothing()
    {
        var before = await Run("state", "show");
        Assert.Equal(0, before.Exit);
        Assert.Contains("not created yet", before.Out);
        Assert.Contains("scheduling", before.Out);
        Assert.False(Directory.Exists(dir));

        BuildSchedulerSchema.Open(dir).OpenConnection().Dispose();
        Assert.Matches(@"scheduling\S*\s+.*registered v\d+\s+current", (await Run("state", "show")).Out);
        var tested = await Run("state", "test");
        Assert.Equal(0, tested.Exit);
        Assert.Contains("nothing is pending", tested.Out);
        Assert.Equal(2, (await Run("state", "grants", "--login", "svc")).Exit);
    }
}
