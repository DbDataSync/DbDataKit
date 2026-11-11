using System.Text.Json;
using DbDataKit.Cli;
using Json.Schema;

namespace DbDataKit.Lineage.Cli.Tests;

/// <summary>Plan 28c E24: the deployment verbs of the <c>lineage</c> noun, over a graph with deployment nodes; they exist only where the host asks for them.</summary>
public sealed class DeploymentVerbsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static LineageNode Node(string id, string type, string name, params (string, string)[] properties) =>
        new(id, NodeKind.Deployment, name, false, new Dictionary<string, string>(properties.ToDictionary(p => p.Item1, p => p.Item2)) { [DeploymentProperties.Type] = type });

    // orders on prod: active 2, previous 1; host-a is active on 2, host-b is ready on 1
    private static LineageGraph Graph()
    {
        var channel = DeploymentIds.Channel("main", "integration", "orders", "prod");
        var a = DeploymentIds.Instance("main", "aaa");
        var b = DeploymentIds.Instance("main", "bbb");
        var v2 = DeploymentIds.Version("main", "integration", "orders", 2);
        var run = DeploymentIds.Run("main", "r1");
        return new LineageGraph(
        [
            Node(channel, DeploymentTypes.Channel, "orders on prod", ("kind", "integration"), ("name", "orders"), ("channel", "prod"), ("group", "main"), ("active", "2"), ("previous", "1"), ("last_event", "activate"), ("last_by", "grace"), ("last_at", At.ToString("O"))),
            Node(a, DeploymentTypes.Instance, "host-a", ("instance_id", "aaa"), ("host", "host-a"), ("group", "main"), ("runtime", "1.0")),
            Node(b, DeploymentTypes.Instance, "host-b", ("instance_id", "bbb"), ("host", "host-b"), ("group", "main"), ("runtime", "1.0")),
            Node(v2, DeploymentTypes.Version, "orders v2", ("kind", "integration"), ("name", "orders"), ("version", "2")),
            new LineageNode(run, NodeKind.Job, "orders/prod/load", false, new Dictionary<string, string> { [DeploymentProperties.Type] = DeploymentTypes.Run, ["outcome"] = "Succeeded", ["started_at"] = At.ToString("O") }),
        ],
        [
            new LineageEdge(channel, a, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, new DeploymentDetail(2, "active", At).Format()),
            new LineageEdge(channel, b, EdgeKind.Deploys, Granularity.Table, Confidence.Observed, new DeploymentDetail(1, "ready", At).Format()),
            new LineageEdge(v2, channel, EdgeKind.Deploys, Granularity.Table, Confidence.Declared, "active"),
            new LineageEdge(v2, run, EdgeKind.Runs, Granularity.Table, Confidence.Observed),
        ]);
    }

    private static NounRouter Router(bool deployment = true) => new("demo", [LineageNoun.Create(_ => Task.FromResult(Graph()), deployment)]);

    private static async Task<(int Exit, JsonElement Root, string Out, string Err)> Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await Router().RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        var root = output.ToString().Length == 0 ? default : JsonDocument.Parse(output.ToString()).RootElement;
        if (exit == 0) Assert.True(JsonSchema.FromText(Router().OutputSchema()).Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, root.ToString());
        return (exit, root, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Where_says_the_versions_of_a_channel_and_which_runner_lags()
    {
        var (exit, root, _, _) = await Run("lineage", "where", "orders");

        Assert.Equal(0, exit);
        var channel = root.GetProperty("data").GetProperty("channels")[0];
        Assert.Equal(2, channel.GetProperty("active").GetInt32());
        Assert.Equal(1, channel.GetProperty("previous").GetInt32());
        Assert.Equal("grace", channel.GetProperty("last_by").GetString());
        var instances = channel.GetProperty("instances").EnumerateArray().ToDictionary(i => i.GetProperty("host").GetString()!);
        Assert.True(instances["host-a"].GetProperty("on_active").GetBoolean());
        Assert.False(instances["host-b"].GetProperty("on_active").GetBoolean());
        Assert.Equal("ready", instances["host-b"].GetProperty("state").GetString());
    }

    [Fact]
    public async Task On_says_what_a_runner_holds_by_host_or_id_and_runs_lists_the_runs_of_a_version()
    {
        var (exit, root, _, _) = await Run("lineage", "on", "host-b");
        Assert.Equal(0, exit);
        var holding = root.GetProperty("data").GetProperty("instances")[0].GetProperty("holdings")[0];
        Assert.Equal((1, "ready", 2, true), (holding.GetProperty("version").GetInt32(), holding.GetProperty("state").GetString(), holding.GetProperty("channel_active").GetInt32(), holding.GetProperty("behind").GetBoolean()));
        Assert.Equal(0, (await Run("lineage", "on", "aaa")).Exit);

        var (runsExit, runs, _, _) = await Run("lineage", "runs", "orders", "2");
        Assert.Equal(0, runsExit);
        Assert.Equal("Succeeded", runs.GetProperty("data").GetProperty("runs")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Unknown_things_are_exit_1_bad_use_is_exit_2_and_the_verbs_are_absent_unless_asked_for()
    {
        Assert.Equal(1, (await Run("lineage", "where", "nothing")).Exit);
        Assert.Equal(1, (await Run("lineage", "on", "nowhere")).Exit);
        Assert.Equal(2, (await Run("lineage", "runs", "orders", "x")).Exit);
        var (output, error) = (new StringWriter(), new StringWriter());
        Assert.NotEqual(0, await Router(deployment: false).RunAsync(["lineage", "where", "orders"], new CliContext(output, error, TextReader.Null)));
    }
}
