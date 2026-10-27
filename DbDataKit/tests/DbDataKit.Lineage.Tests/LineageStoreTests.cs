using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Lineage.Tests;

/// <summary>The lineage store on every state engine (plan 18, G01): a graph stored and read back, replaced whole, its version moving only when it changes, its runs along edges, and one database holding several owners.</summary>
public sealed class LineageStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private LineageStore Open(string engine, string service = "dbdatabuild") => new(_engines.Open(engine, new StateSchema([LineageSchema.Tables])), service);

    [Theory, MemberData(nameof(Engines))]
    public void A_graph_is_stored_and_read_back_whole(string engine)
    {
        var store = Open(engine);
        Assert.Null(store.Version("build"));
        Assert.Empty(store.Load("build").Nodes);

        var graph = new LineageGraph(
            [new LineageNode("postgres://db/app.public.orders", NodeKind.Dataset, "orders", Properties: new Dictionary<string, string> { ["schema"] = "public" }), new LineageNode("suite://b/model/m", NodeKind.Job, "m", UnknownUpstream: true)],
            [new LineageEdge("postgres://db/app.public.orders", "suite://b/model/m", EdgeKind.Derives, Granularity.Column, Confidence.Derived, "aggregate")]);
        Assert.True(store.Replace("build", graph));

        var loaded = store.Load("build");
        Assert.Equal(graph.Fingerprint(), loaded.Fingerprint());
        Assert.Equal(graph.Fingerprint(), store.Version("build"));
        Assert.Equal("public", loaded.Node("postgres://db/app.public.orders")!.PropertyMap["schema"]);
        Assert.True(loaded.Node("suite://b/model/m")!.UnknownUpstream);
        Assert.Equal("aggregate", Assert.Single(loaded.Edges).Detail);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Storing_the_same_graph_writes_nothing_and_a_different_one_replaces_it(string engine)
    {
        var store = Open(engine);
        var first = LineageGraphTests.Fixture();
        Assert.True(store.Replace("build", first));
        var version = store.Version("build");
        Assert.False(store.Replace("build", LineageGraphTests.Fixture()));          // nothing changed: same version, no write

        var smaller = new LineageGraph(first.Nodes.Where(n => n.Id is "src" or "stage"), first.Edges.Where(e => e.From == "src"));
        Assert.True(store.Replace("build", smaller));
        Assert.NotEqual(version, store.Version("build"));
        Assert.Equal(["src", "stage"], store.Load("build").Nodes.Select(n => n.Id).Order());
    }

    [Theory, MemberData(nameof(Engines))]
    public void One_database_keeps_the_graphs_of_several_owners_and_services_apart(string engine)
    {
        var database = _engines.Open(engine, new StateSchema([LineageSchema.Tables]));
        var build = new LineageStore(database, "dbdatabuild");
        var sync = new LineageStore(database, "dbdatasync");
        build.Replace("one", LineageGraphTests.Fixture());
        build.Replace("two", new LineageGraph([LineageGraphTests.N("only")], []));

        Assert.Equal(6, build.Load("one").Nodes.Count);
        Assert.Equal(["only"], build.Load("two").Nodes.Select(n => n.Id));
        Assert.Empty(sync.Load("one").Nodes);
        Assert.Null(sync.Version("one"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Runs_attach_to_edges_the_newest_is_the_latest_and_old_ones_are_pruned(string engine)
    {
        var store = Open(engine);
        store.Replace("build", LineageGraphTests.Fixture());
        var t = new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

        store.AddRun("build", new EdgeRun("src", "stage", EdgeKind.Replicates, t, RunOutcome.Succeeded, 1_000_000_000_000));
        store.AddRun("build", new EdgeRun("src", "stage", EdgeKind.Replicates, t.AddHours(1), RunOutcome.Failed));
        store.AddRun("build", new EdgeRun("stage", "mart", EdgeKind.Derives, t.AddHours(2), RunOutcome.Succeeded, 40));

        var latest = store.LatestRuns("build");
        Assert.Equal((RunOutcome.Failed, null), (latest["src\nstage\nReplicates"].Outcome, latest["src\nstage\nReplicates"].Rows));
        Assert.Equal(40, latest["stage\nmart\nDerives"].Rows);
        Assert.Equal(1, store.PruneRuns("build", t.AddMinutes(30)));            // the first run only
        Assert.Equal(RunOutcome.Failed, store.LatestRuns("build")["src\nstage\nReplicates"].Outcome);

        var big = new EdgeRun("src", "stage", EdgeKind.Replicates, t.AddHours(3), RunOutcome.Succeeded, 1_000_000_000_000);
        store.AddRun("build", big);
        Assert.Equal(1_000_000_000_000, store.LatestRuns("build")["src\nstage\nReplicates"].Rows);       // row counts are 64-bit
        Assert.Throws<InvalidOperationException>(() => store.AddRun("build", new EdgeRun("src", "report", EdgeKind.Reads, t, RunOutcome.Succeeded)));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_runs_of_an_edge_that_a_new_design_drops_go_with_it(string engine)
    {
        var store = Open(engine);
        store.Replace("build", LineageGraphTests.Fixture());
        var t = new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
        store.AddRun("build", new EdgeRun("stage", "audit", EdgeKind.Derives, t, RunOutcome.Succeeded, 3));
        store.AddRun("build", new EdgeRun("stage", "mart", EdgeKind.Derives, t, RunOutcome.Succeeded, 4));

        store.Replace("build", new LineageGraph(LineageGraphTests.Fixture().Nodes, LineageGraphTests.Fixture().Edges.Where(e => e.To != "audit")));

        Assert.Equal(["stage\nmart\nDerives"], store.LatestRuns("build").Keys);
    }
}
