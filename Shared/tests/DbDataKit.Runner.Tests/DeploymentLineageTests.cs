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

    [Theory, MemberData(nameof(Engines))]
    public void The_outside_systems_a_task_reported_are_nodes_beside_its_package_once_whatever_the_reruns_and_a_dataset_line_or_a_package_that_is_gone_adds_none(string engine)
    {
        using var rig = new Rig(engine);
        rig.Publish("ada", PublishMode.Serve);
        var store = new TaskLineageStore(rig.Database);
        var run = Guid.NewGuid();
        // the task reads an API and writes a table and a share; a rerun says the same; a table of its own naming and a dataset identity are not outside systems
        for (var i = 0; i < 3; i++)
            store.Record(Name, "prod", "pull", run, ["external://http-api/crm", "crm.orders"], ["sqlserver://wh/Sales.dbo.orders", "external://sftp/exports"]);
        store.Record("gone", "prod", "pull", run, ["external://http-api/ghost"], []);       // its package is not deployed any more

        var (graph, problems) = RunnerDeploymentLineage.BuildWithProblems(rig.Database, Group, Lease);

        Assert.Empty(problems);
        var api = LineageIdentity.ExternalSystem("http-api", "crm");
        var share = LineageIdentity.ExternalSystem("sftp", "exports");
        Assert.Equal([api, share], graph.Nodes.Where(n => n.Kind == NodeKind.ExternalSystem).Select(n => n.Id).Order());
        var job = LineageIdentity.SuiteObject(Group, "task", $"{Name}/pull");
        Assert.Contains(graph.Edges, e => e.From == api && e.To == job && e.Kind == EdgeKind.Reads);
        Assert.Contains(graph.Edges, e => e.From == job && e.To == share && e.Kind == EdgeKind.Serves && e.Detail == "wrote to");
        Assert.Contains(graph.Edges, e => e.From == DeploymentIds.Package(Group, Kind, Name) && e.To == job && e.Kind == EdgeKind.Deploys);
        // what the API is upstream of, as `lineage provenance` asks: the share is downstream of the API through the task
        Assert.Contains(share, graph.Impact(api).Reached.Select(r => r.Node.Id));
        Assert.True(graph.Node(api)!.UnknownUpstream);
        Assert.DoesNotContain(graph.Nodes, n => n.Id.Contains("ghost", StringComparison.Ordinal));
        Assert.Equal(graph.Fingerprint(), LineageJson.Read(LineageJson.Write(graph)).Fingerprint());      // the wire form carries the kind
        Assert.Equal(graph.Fingerprint(), RunnerDeploymentLineage.Build(rig.Database, Group, Lease).Fingerprint());   // asking again adds nothing
    }

    [Theory, MemberData(nameof(Engines))]
    public void Tasks_whose_outside_systems_would_close_a_circle_are_named_and_left_out_not_a_failure_of_the_graph(string engine)
    {
        using var rig = new Rig(engine);
        rig.Publish("ada", PublishMode.Serve);
        var store = new TaskLineageStore(rig.Database);
        store.Record(Name, "prod", "a", Guid.NewGuid(), ["external://queue/x"], ["external://queue/y"]);
        store.Record(Name, "prod", "b", Guid.NewGuid(), ["external://queue/y"], ["external://queue/x"]);

        var (graph, problems) = RunnerDeploymentLineage.BuildWithProblems(rig.Database, Group, Lease);

        var problem = Assert.Single(problems);
        Assert.Contains($"{Name}/b", problem);
        Assert.Contains(graph.Nodes, n => n.Id == LineageIdentity.SuiteObject(Group, "task", $"{Name}/a"));
        Assert.DoesNotContain(graph.Nodes, n => n.Id == LineageIdentity.SuiteObject(Group, "task", $"{Name}/b"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_package_carries_its_design_lineage_named_by_the_bound_connection_with_the_newest_run_along_it_and_an_unbound_slot_is_said(string engine)
    {
        // plan 18 G18: what the deleted Build and Sync servers served (models, column edges, replications) comes with the package; a table named by its slot is named by what the channel binds
        using var rig = new Rig(engine);
        var slot = DbDataKit.Lineage.PackageLineage.SlotConnection("wh", "sqlserver");
        var warehouse = new DbDataKit.Connections.ConnectionDefinition { Name = "warehouse", Engine = "sqlserver", Address = new DbDataKit.Connections.AddressSpec { Host = "wh.example", Database = "dw" } };
        var (raw, orders) = (LineageIdentity.Dataset(slot, "dbo", "raw"), LineageIdentity.Dataset(slot, "dbo", "orders"));
        var design = new LineageGraph(
            [new LineageNode(raw.ToString(), NodeKind.Dataset, "dbo.raw", true), new LineageNode(orders.ToString(), NodeKind.Dataset, "dbo.orders"),
             new LineageNode(LineageIdentity.Column(slot, raw, "id"), NodeKind.Column, "id", true), new LineageNode(LineageIdentity.Column(slot, orders, "id"), NodeKind.Column, "id")],
            [new LineageEdge(raw.ToString(), orders.ToString(), EdgeKind.Derives, Granularity.Table, Confidence.Derived),
             new LineageEdge(LineageIdentity.Column(slot, raw, "id"), LineageIdentity.Column(slot, orders, "id"), EdgeKind.Derives, Granularity.Column, Confidence.Derived, "identity")]);
        var files = new PublishArtifacts(new PayloadStore(rig.Database), rig.Publisher, "ada");
        var artifact = files.Add(DbDataKit.Lineage.PackageLineage.Path, DbDataKit.Lineage.PackageLineage.Bytes(DbDataKit.Lineage.PackageLineage.Write(design)));
        rig.Publisher.Publish(new PublishRequest(Kind, Name, "manifest", "ada", "prod", PublishMode.Serve, Artifacts: [artifact]));

        // not bound yet: the tables keep the slot's name, and that is said
        var (unbound, said, _) = RunnerDeploymentLineage.BuildWithDesign(rig.Database, Group, Lease, _ => warehouse);
        Assert.NotNull(unbound.Node(orders.ToString()));
        Assert.Contains(said, p => p.Contains("`wh`", StringComparison.Ordinal) && p.Contains($"{Name} v1 on prod", StringComparison.Ordinal));

        rig.Publisher.Bind("prod", Kind, Name, "wh", "warehouse", "ada");
        var runs = new RunStore(rig.Database, RunnerNames.EngineService);
        var run = runs.Start("refresh", at: rig.Clock.GetUtcNow());
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        runs.Finish(run, DbDataKit.Monitoring.RunOutcome.Succeeded, at: rig.Clock.GetUtcNow());
        var real = LineageIdentity.Dataset(warehouse, "dbo", "orders");
        new TaskLineageStore(rig.Database).Record(Name, "prod", "refresh", run, [], [real.ToString()]);

        var (graph, problems, laid) = RunnerDeploymentLineage.BuildWithDesign(rig.Database, Group, Lease, name => name == "warehouse" ? warehouse : null);
        Assert.Empty(problems);
        Assert.NotNull(graph.Node(real.ToString()));
        Assert.Contains(graph.Edges, e => e.To == LineageIdentity.Column(warehouse, real, "id") && e.Granularity == Granularity.Column);
        Assert.DoesNotContain(graph.Nodes, n => n.Id.Contains("://@", StringComparison.Ordinal));
        Assert.NotNull(graph.Node(DeploymentIds.Package(Group, Kind, Name)));      // beside the deployment
        Assert.Equal(2, laid.Count);
        Assert.All(laid, r => Assert.Equal(DbDataKit.Lineage.RunOutcome.Succeeded, r.Outcome));
        Assert.Contains(laid, r => r.To == real.ToString() && r.Kind == EdgeKind.Derives);

        // a file the runner cannot read is said, and the rest of the graph stands
        var broken = files.Add(DbDataKit.Lineage.PackageLineage.Path, "{\"schema\":\"package-lineage/9\"}"u8);
        rig.Publisher.Publish(new PublishRequest(Kind, Name, "manifest 2", "ada", "prod", PublishMode.Serve, Artifacts: [broken]));
        var (_, after, _) = RunnerDeploymentLineage.BuildWithDesign(rig.Database, Group, Lease, _ => warehouse);
        Assert.Contains(after, p => p.Contains("package-lineage/9", StringComparison.Ordinal) && p.Contains($"{Name} v2 on prod", StringComparison.Ordinal));
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
