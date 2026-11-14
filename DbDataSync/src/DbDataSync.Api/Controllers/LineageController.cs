using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Lineage;
using DbDataSync.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Sync's lineage (plan 18, G07, G07b, G09 and G16): what its replications say can flow, as a graph in the form every server sends a console (<see cref="LineageJson"/>), so the console can join it with the other servers' by identity, and the newest finished run
/// of each mapping laid along its edge (<c>runs</c>, <see cref="SyncLineageRuns"/>). The design graph is kept in the <c>lineage</c> module of Sync's state between requests (<see cref="LineageKeeper"/>: replaced only when a definition changed), and so are the
/// runs, for as long as Sync keeps run detail, so an edge shows its last run after the run history has been pruned. A replication or mapping that cannot be read is left out and named in <c>problems</c>; it does not take the rest with it.
/// </summary>
[ApiController]
[Route("api/lineage")]
public sealed class LineageController(ConfigRepository configRepository, TaskRunStore taskRunStore, LineageStore lineageStore, DbDataSync.Api.Runner.RunnerRunSource runnerRuns) : ControllerBase
{
    /// <summary>How many of the runner's newest runs of each replication are read for the runs of its edges.</summary>
    internal const int RunnerPasses = 200;

    /// <summary>The owner of Sync's graph in its own lineage store.</summary>
    public const string Owner = "sync";

    /// <summary>
    /// How often an edge should run, for the rule <c>Overdue</c> (plan 18 G10b): a replication that loops with a frequency (`Continuous`, `FrequencySeconds`) says how often it passes, and the edge is late after two of them (one pass missed is not yet late); a replication on a cron
    /// expression has no fixed interval to be late at.
    /// </summary>
    internal static Func<EdgeRun, TimeSpan?> ExpectedEvery(LineageGraph graph, IReadOnlyList<SyncReplication> replications)
    {
        var every = replications.Where(r => r.Task.Scheduling is { Mode: ScheduleMode.Continuous, FrequencySeconds: > 0 }).ToDictionary(r => r.Task.Name, r => TimeSpan.FromSeconds(2.0 * r.Task.Scheduling.FrequencySeconds!.Value), StringComparer.Ordinal);
        var detail = graph.Edges.Where(e => e.Kind == EdgeKind.Replicates && e.Granularity == Granularity.Table && e.Detail is { } d && d.Contains('/')).ToDictionary(e => e.Key, e => e.Detail![..e.Detail!.IndexOf('/')], StringComparer.Ordinal);
        return run => detail.TryGetValue(run.EdgeKey, out var task) && every.TryGetValue(task, out var span) ? span : null;
    }

    [Authorize(Policies.Viewer)]
    [HttpGet]
    public async Task<ActionResult<JsonObject>> Get(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var (replications, connections) = SyncLineage.Load(configRepository, problems);

        LineageGraph graph;
        try { graph = SyncLineage.Design(replications, connections, problems); }
        catch (LineageGraphException ex) { return UnprocessableEntity(new JsonObject { ["error"] = $"The replications' lineage cannot be joined: {ex.Message}" }); }
        var document = LineageJson.Write(graph);
        document["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());

        // plan 28c E22 P5: the runner runs every replication, so its newest passes of each are laid along the edges with what this server kept from before
        var runner = new List<TaskRunRecord>();
        foreach (var replication in replications)
            runner.AddRange(await runnerRuns.RecentAsync(replication.Task.Name, RunnerPasses, cancellationToken));

        // keep what is known and answer with the runs kept: a failure of the store never takes the graph away (the answer falls back to the runs of the run history)
        IReadOnlyList<EdgeRun> runs;
        try
        {
            var keeper = new LineageKeeper(lineageStore, Owner);
            keeper.Keep(graph, SyncLineageRuns.Runs(graph, taskRunStore, runner), TimeSpan.FromDays(SyncStateModule.RunDetailDays));
            runs = keeper.Read().Runs;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or IOException)
        {
            problems.Add($"The lineage could not be kept in the state ({ex.GetType().Name}), so its runs are those of the run history.");
            document["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            runs = SyncLineageRuns.Runs(graph, taskRunStore, runner);
        }
        document["runs"] = LineageJson.WriteRuns(LineageFreshness.Mark(runs, ExpectedEvery(graph, replications)));
        return Ok(document);
    }
}
