using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Testing;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// (A part of the apply group.) Plan 24a UC13 on real engines: the runtime's <c>refresh</c> task, run through the kit's run simulator in this process (the real task entry, the real command), reports the models it loaded as the run's
/// lineage, under the names the Build server's <c>data.changed</c> used (<see cref="BuildLineage.LoadedDatasets"/>), so the Runner's <c>RunnerDataChanges</c> tells the serving groups.
/// </summary>
public partial class ApplyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_refresh_on_the_runner_reports_the_models_it_loaded_as_the_runs_lineage(string name)
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

            using var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, null);
            using var simulator = new RunSimulator<RefreshJob>(new RefreshInput(name, "hourly"), package: "sales", channel: "prod");
            var attempt = await simulator.RunAsync();

            Assert.True(attempt.Outcome == RunOutcome.Succeeded, attempt.Result?.End.Failure?.Message ?? attempt.Outcome.ToString());
            var (reads, writes) = Assert.Single(simulator.Lineage);
            Assert.Empty(reads);
            var written = Assert.Single(writes);
            Assert.Contains("://", written, StringComparison.Ordinal);
            Assert.EndsWith("marts.fct_orders", written, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BuildLineage.LoadedDatasets(ProjectContext.Load(run.Dir).Config, Environment.GetEnvironmentVariable, [(name, "marts.fct_orders")]), writes);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
