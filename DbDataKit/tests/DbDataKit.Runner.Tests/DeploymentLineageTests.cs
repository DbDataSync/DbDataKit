using DbDataKit.Lineage;
using DbDataKit.Runner.Deployments;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 28c E24 (ED18): the runner's definitions events, instance states and runs, laid as nodes and edges of the plan 18 graph, and the two questions asked of it: where is this deployed, what is deployed on this runner.</summary>
public sealed class DeploymentLineageTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private const string Group = "main";
    private const string Kind = DefinitionVersionSource.Kind;
    private const string Name = "orders";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(90);

    private sealed class Rig : IDisposable
    {
        private readonly StateEngines _engines = new();

        public Rig(string engine)
        {
            Database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, RunnerModule.Module, RunsModule.Module], StateScope.Standalone, time: Clock);
            Publisher = new DefinitionPublisher(Database, null);
            Instances = new RunnerInstances(Database);
        }

        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        public StateDatabase Database { get; }
        public DefinitionPublisher Publisher { get; }
        public RunnerInstances Instances { get; }

        public Guid Runner(string host) => Instances.Register("dbdatarunner", host, "1.0", moduleVersions: null);

        private int _published;

        public int Publish(string by, PublishMode mode) => Publisher.Publish(new PublishRequest(Kind, Name, $"manifest {++_published}", by, "prod", mode)).Version.Version;

        public void Report(Guid instance, int version, string state, string? error = null) =>
            Publisher.Status.Report(instance.ToString("D"), Kind, Name, "prod", version, state, error);

        public LineageGraph Graph() => RunnerDeploymentLineage.Build(Database, Group, Lease);

        public void Dispose() => _engines.Dispose();
    }

    [Theory, MemberData(nameof(Engines))]
    public void Activating_version_2_shows_two_runners_active_on_2_and_a_stalled_one_ready_on_1_with_who_and_when_and_a_rollback_shows_1_again(string engine)
    {
        using var rig = new Rig(engine);
        var (a, b, c) = (rig.Runner("host-a"), rig.Runner("host-b"), rig.Runner("host-c"));
        rig.Publish("ada", PublishMode.Serve);                       // version 1, active at once
        foreach (var r in new[] { a, b, c }) rig.Report(r, 1, InstanceStates.Active);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, rig.Publish("ada", PublishMode.Stage));
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Publisher.Activate(Kind, Name, "grace", "prod");
        rig.Report(a, 2, InstanceStates.Active);
        rig.Report(b, 2, InstanceStates.Active);
        rig.Report(c, 1, InstanceStates.Ready);                      // the stalled one

        var channel = Assert.Single(rig.Graph().WhereDeployed(Name));
        Assert.Equal((2, 1, null), (channel.Active, channel.Previous, channel.Staged));
        Assert.Equal(("activate", "grace"), (channel.LastEvent, channel.LastBy));
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 9, 10, 0, TimeSpan.Zero), channel.LastAt);
        Assert.Equal(["host-a", "host-b"], channel.OnActive.Select(i => i.Host).Order());
        var behind = Assert.Single(channel.Behind);
        Assert.Equal(("host-c", 1, "ready"), (behind.Host, behind.Version, behind.State));
        Assert.All(channel.Instances, i => Assert.Equal("1.0", i.Runtime));

        // the same from the runner's side: what is deployed on host-c, and that the channel is ahead of it
        var onC = Assert.Single(rig.Graph().OnRunner("host-c"));
        var holding = Assert.Single(onC.Holdings);
        Assert.Equal((Kind, Name, "prod", 1, "ready", 2), (holding.Kind, holding.Name, holding.Channel, holding.Version, holding.State, holding.ChannelActive));
        Assert.Equal(rig.Graph().OnRunner(a.ToString("D")).Single().InstanceId, a.ToString("D"));      // by id as well

        // a rollback: the channel is on 1 again and the event says who
        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Publisher.Rollback(Kind, Name, "hopper", "prod");
        var after = Assert.Single(rig.Graph().WhereDeployed(Name));
        Assert.Equal((1, 2), (after.Active, after.Previous));
        Assert.Equal(("rollback", "hopper"), (after.LastEvent, after.LastBy));
        Assert.StartsWith("2026-11-07T09:15", after.History);
        Assert.Contains("activate v2 by grace", after.History);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_run_names_its_version_and_the_graph_joins_it_to_that_version_node(string engine)
    {
        using var rig = new Rig(engine);
        rig.Publish("ada", PublishMode.Serve);
        rig.Publish("ada", PublishMode.Stage);
        rig.Publisher.Activate(Kind, Name, "grace", "prod");
        var runs = new RunStore(rig.Database, RunnerNames.EngineService);
        var old = runs.Start($"{Name}/prod/load", package: Name, packageVersion: "1", channel: "prod");
        runs.Start($"{Name}/prod/load", package: Name, packageVersion: "2", channel: "prod");

        var graph = rig.Graph();

        Assert.Single(graph.RunsOf(Name, 1));
        var two = Assert.Single(graph.RunsOf($"{Kind}/{Name}", 2));
        Assert.Equal($"{Name}/prod/load", two.Name);
        Assert.Equal(DeploymentIds.Run(Group, old.ToString()), Assert.Single(graph.RunsOf(Name, 1)).Id);
        Assert.Empty(graph.RunsOf(Name, 3));
        // the version node says who published it
        Assert.Equal("ada", graph.Node(DeploymentIds.Version(Group, Kind, Name, 2))!.PropertyMap[DeploymentProperties.PublishedBy]);
        // and the version reaches its channel, runners and runs downstream (impact), the package upstream (provenance)
        var impact = graph.Impact(DeploymentIds.Version(Group, Kind, Name, 2));
        Assert.Contains(impact.Reached, r => r.Node.PropertyMap.GetValueOrDefault(DeploymentProperties.Type) == DeploymentTypes.Channel);
        Assert.Contains(impact.Reached, r => r.Node.Kind == NodeKind.Job);
        Assert.Contains(graph.Provenance(DeploymentIds.Version(Group, Kind, Name, 2)).Reached, r => r.Node.Id == DeploymentIds.Package(Group, Kind, Name));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_failed_instance_and_an_instance_that_left_are_said_and_the_graph_survives_the_wire_form(string engine)
    {
        using var rig = new Rig(engine);
        var (a, b) = (rig.Runner("host-a"), rig.Runner("host-b"));
        rig.Publish("ada", PublishMode.Serve);
        rig.Publish("ada", PublishMode.Stage);
        rig.Report(a, 2, InstanceStates.Failed, "needs the label gpu");
        rig.Report(b, 2, InstanceStates.Ready);
        rig.Instances.MarkGone(b);
        rig.Publisher.Status.MarkGone(b.ToString("D"));

        var graph = LineageJson.Read(LineageJson.Write(rig.Graph()));
        var channel = Assert.Single(graph.WhereDeployed(Name, "prod"));

        Assert.Equal(2, channel.Staged);
        var failed = Assert.Single(channel.Instances, i => i.Host == "host-a");
        Assert.Equal(("failed", "needs the label gpu"), (failed.State, failed.Error));
        Assert.Single(channel.Instances, i => i.Host == "host-b" && i.Gone);
        Assert.Equal(2, channel.Behind.Count + channel.OnActive.Count + 1);          // the gone one is in neither list
        Assert.Empty(graph.WhereDeployed("nothing"));
        Assert.Empty(graph.OnRunner("nowhere"));
        Assert.Empty(graph.WhereDeployed(Name, "staging"));
    }

    [Fact]
    public void A_detail_survives_its_text_with_an_error_that_has_the_separator()
    {
        var at = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
        var parsed = DeploymentDetail.Parse(new DeploymentDetail(3, "failed", at, "a|b\nc", true).Format());
        Assert.Equal(new DeploymentDetail(3, "failed", at, "a/b c", true), parsed);
        Assert.Null(DeploymentDetail.Parse("nonsense"));
    }
}
