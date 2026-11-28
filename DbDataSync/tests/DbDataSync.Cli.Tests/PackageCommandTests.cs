using System.Text.Json.Nodes;

namespace DbDataSync.Cli.Tests;

/// <summary>Plan 28c E08: `dbdatasync package publish --to runner --check --json`, the form the Sync API's publish action and its check run: what the package would hold, or the verb's own refusal, and nothing contacted or restored.</summary>
public sealed class PackageCommandTests : IDisposable
{
    private readonly ReplicationSetRepo _repo = new("dbdatasync-pkgcmd-");

    public void Dispose() => _repo.Dispose();

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var (originalOut, originalErr) = (Console.Out, Console.Error);
        var (o, e) = (new StringWriter(), new StringWriter());
        Console.SetOut(o);
        Console.SetError(e);
        try { return (await PackageCommand.RunAsync(["publish", .. args]), o.ToString(), e.ToString()); }
        finally { Console.SetOut(originalOut); Console.SetError(originalErr); }
    }

    [Fact]
    public async Task CheckSaysWhatThePackageWouldHold_AsOneJsonLine_AndSendsNothing()
    {
        var (exit, output, error) = await RunAsync("--to", "runner", "--check", "--json", "--package", "sales", "--repo", _repo.Root);

        Assert.True(exit == 0, error);
        var doc = JsonNode.Parse(output)!;
        Assert.Equal("check", doc["mode"]!.GetValue<string>());
        Assert.Equal("sales", doc["package"]!.GetValue<string>());
        Assert.Null(doc["version"]);
        Assert.Equal(["customers", "events", "orders"], doc["replications"]!.AsArray().Select(r => r!["task"]!.GetValue<string>()).Order());
        var orders = doc["replications"]!.AsArray().Single(r => r!["task"]!.GetValue<string>() == "orders")!;
        Assert.Equal(["crm_src", "dwh"], orders["slots"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal("crm-src", orders["slotConnections"]!["crm_src"]!.GetValue<string>());     // the connection each slot is for: the runner's connection bound to it carries this name
        Assert.Equal("*/15 * * * *", orders["cron"]!.GetValue<string>());
        Assert.Equal(2, orders["mappings"]!.GetValue<int>());
        Assert.DoesNotContain("secret-pw", output);
    }

    [Fact]
    public async Task AMappingWithNoColumnCacheIsRefusedInTheWordsOfTheMetadataException_AndNothingElseIsPrinted()
    {
        _repo.Replication("returns", DbDataKit.Sync.Core.Config.ScheduleMode.Periodic, cron: "0 3 * * *", cached: false);

        var (exit, output, error) = await RunAsync("--to", "runner", "--check", "--json", "--repo", _repo.Root);

        Assert.Equal(1, exit);
        Assert.Equal("", output);                     // a result is a line of JSON; a refusal is the error text alone, which the API relays unchanged
        Assert.Contains("Table mapping 't1' has no cached source column metadata", error);
        Assert.Contains("returns", error);
        Assert.Contains("t1.yaml", error);
    }

    [Fact]
    public async Task TwoReplicationsThatMakeOneTaskNameAreRefusedNamingBoth()
    {
        // names that differ by punctuation, not case: a case-insensitive file system (Windows) makes two folders that differ only by case one folder
        _repo.Replication("sales.orders", DbDataKit.Sync.Core.Config.ScheduleMode.Periodic, cron: "0 3 * * *");
        _repo.Replication("sales-orders", DbDataKit.Sync.Core.Config.ScheduleMode.Periodic, cron: "0 3 * * *");

        var (exit, _, error) = await RunAsync("--to", "runner", "--check", "--json", "--repo", _repo.Root);

        Assert.Equal(1, exit);
        Assert.Contains("both make the task `sales-orders`", error);
    }

    [Fact]
    public async Task CheckTakesNoOutDirectory()
    {
        var (exit, _, error) = await RunAsync("--to", "runner", "--check", "--out", Path.Combine(_repo.Root, "out"), "--repo", _repo.Root);

        Assert.Equal(2, exit);
        Assert.Contains("--check builds the package and writes nothing", error);
    }

    [Theory]
    [InlineData("--server", "runner-1", "--channel", "prod")]
    [InlineData("--server", "https://runner.example:7840", "--code", "AAAAA-AAAAA", "--channel", "prod", "--json")]
    [InlineData("--check", "--server", "runner-1")]
    public async Task SendingThePackageToARunnerIsGoneAndNamesTheConsolesDeploy(params string[] extra)
    {
        var client = Path.Combine(_repo.Root, "client");
        var (exit, output, error) = await RunAsync(["--to", "runner", .. extra, "--client-dir", client, "--repo", _repo.Root]);

        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Equal(DbDataKit.Cli.RemovedCommands.PublishingGone("dbdatasync package publish --to runner --server"), error.Trim());
        Assert.Contains("the console deploys", error);
        Assert.False(Directory.Exists(client));       // nothing was paired
    }

    [Fact]
    public async Task WithNeitherOutNorCheckItSaysWhatToGive()
    {
        var (exit, _, error) = await RunAsync("--to", "runner", "--repo", _repo.Root);

        Assert.Equal(2, exit);
        Assert.Contains("--out <directory>", error);
        Assert.Contains("console's deploy", error);
    }
}
