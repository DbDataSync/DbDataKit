using DbDataKit.State.Tests;

namespace DbDataKit.Lineage.Tests;

/// <summary>
/// Plan 18 G07b and G16 on every state engine: a server keeps its graph and the newest runs along its edges between requests, a graph that did not change writes nothing, a run of an edge the design does not have is left out and counted, and the runs older than
/// the retention of run detail are gone after a keep (a prune that the server calls, besides the shared retention job).
/// </summary>
public sealed class LineageKeeperTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private (LineageKeeper Keeper, LineageStore Store, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var store = new LineageStore(_engines.Provision(engine).Open([LineageModule.Module], time: clock), "dbdatasync");
        return (new LineageKeeper(store, "sync", clock), store, clock);
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_graph_and_the_newest_run_of_each_edge_are_kept_and_read_back_and_a_graph_that_did_not_change_writes_nothing(string engine)
    {
        var (keeper, store, _) = Open(engine);
        var design = LineageGraphTests.Fixture();
        var run = new EdgeRun("src", "stage", EdgeKind.Replicates, Start.AddHours(-1), RunOutcome.Failed, 12);

        var first = keeper.Keep(design, [run]);
        Assert.Equal(new LineageKept(true, 1, 0, 0), first);
        var version = store.Version("sync");
        var second = keeper.Keep(LineageGraphTests.Fixture(), [run]);
        Assert.Equal(new LineageKept(false, 1, 0, 0), second);       // the run is stored again at the same instant, once
        Assert.Equal(version, store.Version("sync"));

        var (graph, runs) = keeper.Read();
        Assert.Equal(design.Fingerprint(), graph.Fingerprint());
        var kept = Assert.Single(runs);
        Assert.Equal((RunOutcome.Failed, 12L), (kept.Outcome, kept.Rows));
        // what the wire form sends
        var document = LineageJson.Write(graph);
        document["runs"] = LineageJson.WriteRuns(runs);
        Assert.Single(LineageJson.ReadRuns(document));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_run_of_an_edge_the_design_does_not_have_is_left_out_and_counted_and_the_runs_of_an_edge_that_is_gone_go_with_it(string engine)
    {
        var (keeper, _, _) = Open(engine);
        var design = LineageGraphTests.Fixture();
        var kept = keeper.Keep(design, [new EdgeRun("src", "stage", EdgeKind.Replicates, Start, RunOutcome.Succeeded), new EdgeRun("src", "report", EdgeKind.Replicates, Start, RunOutcome.Succeeded)]);
        Assert.Equal((1, 1), (kept.RunsAdded, kept.RunsLeftOut));
        Assert.Single(keeper.Read().Runs);

        var smaller = new LineageGraph(design.Nodes.Where(n => n.Id is "stage" or "mart"), design.Edges.Where(e => e.From == "stage" && e.To == "mart"));
        Assert.True(keeper.Keep(smaller, []).DesignChanged);
        Assert.Empty(keeper.Read().Runs);
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_runs_older_than_the_retention_are_not_kept_and_are_pruned_when_they_age(string engine)
    {
        var (keeper, store, clock) = Open(engine);
        var design = LineageGraphTests.Fixture();
        var retention = TimeSpan.FromDays(30);
        var old = new EdgeRun("src", "stage", EdgeKind.Replicates, Start.AddDays(-40), RunOutcome.Succeeded);
        var recent = new EdgeRun("stage", "mart", EdgeKind.Derives, Start.AddDays(-1), RunOutcome.Succeeded, 3);

        var first = keeper.Keep(design, [old, recent], retention);
        Assert.Equal((1, 0), (first.RunsAdded, first.RunsPruned));        // the old one was past the retention and is not stored
        Assert.Equal(["stage"], store.LatestRuns("sync").Values.Select(r => r.From));

        clock.Advance(TimeSpan.FromDays(35));
        var later = keeper.Keep(design, [], retention);
        Assert.Equal(1, later.RunsPruned);                                // the run of "stage" is 36 days old now
        Assert.Empty(store.LatestRuns("sync"));
        Assert.Equal(6, keeper.Read().Graph.Nodes.Count);                 // the design stays
    }
}
