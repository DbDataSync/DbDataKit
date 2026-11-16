using System.Text.Json.Nodes;
using DbDataBuild.Cli.Runner;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Testing;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// (A part of the apply group.) Plan 33 SC20p on real engines: the database-facing views as the read-only manual tasks of the refresh package, run through the kit's run simulator in this process (the real task entry and the real command;
/// the runner's slots are the process environment): the status of a connection, compare and the project's tests each record the command's document as the step <c>view-result</c>, which the Workspace's Runs tab reads; a finding is the
/// document's, not a failure; nothing is written to the target.
/// </summary>
public partial class ApplyConformanceTests
{
    private static JsonObject ViewResult<TJob>(RunSimulator<TJob> simulator) where TJob : DbDataKit.Runner.Kit.RunnerJobBase, new()
    {
        var step = Assert.Single(simulator.StoredSteps(), s => s.Name == BuildViews.StepName);
        return (JsonObject)JsonNode.Parse(step.ResultJson!)!;
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_status_of_a_connection_compare_and_the_projects_tests_run_as_read_only_tasks_and_record_their_documents(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            Ok(run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out)), "apply");
            var tracking = run.Q("dbdatabuild") + "." + run.Q("migration_log");
            var before = await CountAsync(run, tracking);

            using var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, Person);

            using (var status = new RunSimulator<StatusViewJob>(new StatusViewInput(name), package: "sales", channel: "prod"))
            {
                var attempt = await status.RunAsync();
                Assert.True(attempt.Outcome == RunOutcome.Succeeded, attempt.Result?.End.Failure?.Message ?? attempt.Outcome.ToString());
                var result = ViewResult(status);
                Assert.Equal("status", (string)result["view"]!);
                Assert.NotNull(result["document"]);
                Assert.Null(result["truncated"]);
            }

            // a copy made outside the tool, compared with the model's table
            await engine.ExecAsync(name == "sqlserver" ? "SELECT * INTO marts.fct_orders_copy FROM marts.fct_orders" : "CREATE TABLE marts.fct_orders_copy AS SELECT * FROM marts.fct_orders");
            using (var compare = new RunSimulator<CompareViewJob>(new CompareViewInput(name, "marts.fct_orders", Against: "marts.fct_orders_copy"), package: "sales", channel: "prod"))
            {
                var attempt = await compare.RunAsync();
                Assert.True(attempt.Outcome == RunOutcome.Succeeded, attempt.Result?.End.Failure?.Message ?? attempt.Outcome.ToString());
                Assert.Equal("compare", (string)ViewResult(compare)["view"]!);
            }

            using (var tests = new RunSimulator<TestsViewJob>(new TestsViewInput(), package: "sales", channel: "prod"))
            {
                var attempt = await tests.RunAsync();
                Assert.True(attempt.Outcome == RunOutcome.Succeeded, attempt.Result?.End.Failure?.Message ?? attempt.Outcome.ToString());
                Assert.NotNull(ViewResult(tests)["document"]);
            }

            // an option in the place of a connection is refused before the command runs
            using (var refused = new RunSimulator<StatusViewJob>(new StatusViewInput("--apply"), package: "sales", channel: "prod"))
            {
                Assert.Equal(RunOutcome.Failed, (await refused.RunAsync()).Outcome);
                Assert.Contains("connection", refused.Failure!.Message);
            }

            Assert.Equal(before, await CountAsync(run, tracking));      // a view writes nothing
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
