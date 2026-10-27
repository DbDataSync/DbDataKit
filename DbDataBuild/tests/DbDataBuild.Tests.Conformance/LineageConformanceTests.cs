using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataBuild.State;
using DbDataKit.Lineage;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Run lineage from the tracking tables (plan 18, G06) on the real engines: after a refresh, each load edge carries its last run.</summary>
public partial class ApplyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task After_a_refresh_each_load_edge_carries_its_last_run_from_the_tracking_tables(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            foreach (var ext in new[] { "yml", "sql" }) File.Delete(Path.Combine(run.Dir, $"models/marts/v_orders.{ext}"));
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "refresh:\n  plans:\n    hourly: { title: Orders, expected_every: 1h }\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "refresh:\n  hourly: default\n");
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan"); Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            Ok(run.Cli("connection", "refresh", name, "hourly"), "refresh hourly");

            var ctx = ProjectContext.Load(run.Dir);
            var identity = BuildLineage.IdentityOf(ctx.Config, run.Env);
            var design = BuildLineage.Design(ctx, identity);
            var loadEdges = design.Edges.Where(e => e.Granularity == Granularity.Table && e.Kind == EdgeKind.Derives).ToList();
            Assert.NotEmpty(loadEdges);

            IReadOnlyList<AuditLog.LastLoad> last;
            await using (var read = await ReadSession.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Read, run.Env).Settings!))
                last = await BuildLineage.LastLoadsAsync(read, new TrackingScope(name, "dbdatabuild", name));
            var runs = BuildLineage.Runs(design, ctx, name, identity(name), last);

            // every load edge into a model that ran has its run, and the run says when, how it ended and how many rows
            foreach (var edge in loadEdges.Where(e => design.Node(e.To)!.Name == "marts.fct_orders"))
            {
                var carried = Assert.Single(runs, r => r.From == edge.From && r.To == edge.To && r.Kind == edge.Kind);
                Assert.Equal(RunOutcome.Succeeded, carried.Outcome);
                Assert.Equal(4, carried.Rows);
                Assert.True(carried.At > DateTimeOffset.UtcNow.AddHours(-1));
            }
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
