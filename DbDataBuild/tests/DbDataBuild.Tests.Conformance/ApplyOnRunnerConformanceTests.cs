using DbDataBuild.Cli;
using DbDataBuild.Cli.Runner;
using DbDataBuild.Execution;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Testing;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// (A part of the apply group.) Plan 28c E21 on real engines: the person-driven writes as the manual-only runner tasks of <see cref="BuildOperations"/>, run through the kit's run simulator in this process (the real task entry, the real command, the
/// real tracking tables; the runner's slots are the process environment, as they are in a task process). A plan made by a person is applied by <see cref="ApplyPlanJob"/> with the person the runner recorded
/// (<c>DBDATAKIT_RUN_REQUESTED_BY</c>): the tracking tables name that person and nobody else; an acknowledgement is one <c>block_log</c> row naming them; a refused plan fails the run naming the diagnostic; a run for nobody names the run.
/// What is not proved here is the process a runner starts: that is <c>BuildOperationsOnRunnerTests</c> (the published tool, a real runner), which needs the runtime package.
/// </summary>
public partial class ApplyConformanceTests
{
    private const string Person = "Ada via Main console (sent by Build server)";

    /// <summary>The environment a task process has: the logins of the slots (what the runner injects), the package, the scratch directory and who asked. Restored when disposed.</summary>
    private sealed class TaskEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _before = [];

        public string Scratch { get; } = Directory.CreateTempSubdirectory("ddb-e21-scratch-").FullName;

        public TaskEnvironment(string package, string connection, string connectionString, string? requestedBy)
        {
            Set("DBDATAKIT_PACKAGE_DIR", package);
            Set("DBDATAKIT_SCRATCH_DIR", Scratch);
            Set("DBDATAKIT_RUN_REQUESTED_BY", requestedBy);
            Set(LoginSettings.VariableName(connection, Login.Read), connectionString);
            Set(LoginSettings.VariableName(connection, Login.Write), connectionString);
        }

        private void Set(string name, string? value)
        {
            _before[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _before) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(Scratch, recursive: true); } catch (IOException) { /* temp */ }
        }
    }

    private static async Task<SimulatedAttempt> RunJobAsync<TJob>(object input) where TJob : DbDataKit.Runner.Kit.RunnerJobBase, new()
    {
        using var simulator = new RunSimulator<TJob>(input, package: "sales-ops", channel: "prod");
        return await simulator.RunAsync();
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_plan_a_person_made_is_applied_by_the_task_and_the_tracking_tables_name_that_person_and_an_acknowledgement_is_a_block_log_row_naming_them(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("connection", "init", "--apply"), "init");
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);
            var planId = Path.GetFileName(planFile).Replace(".plan.yml", "", StringComparison.Ordinal);

            // the task is given the plan's text, as the Build server gives it a plan that lives in a file of the project
            using (var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, Person))
            {
                var applied = await RunJobAsync<ApplyPlanJob>(new ApplyPlanInput(name, planId, File.ReadAllText(planFile)));
                Assert.True(applied.Outcome == RunOutcome.Succeeded, applied.Result?.End.Failure?.Message ?? applied.Outcome.ToString());
            }

            var tracking = run.Q("dbdatabuild") + ".";
            var events = tracking + run.Q("migration_log");
            Assert.True(await CountAsync(run, events, run.Q("applied_by") + $" = '{Person}'") > 0);
            Assert.Equal(0, await CountAsync(run, events, run.Q("applied_by") + $" <> '{Person}'"));
            var ddl = tracking + run.Q("ddl_log");
            Assert.True(await CountAsync(run, ddl, run.Q("invoker") + $" = '{Person}'") > 0);
            Assert.Equal(0, await CountAsync(run, ddl, run.Q("invoker") + $" <> '{Person}'"));

            // a completed plan is refused by the command, and the run fails naming it (no repeat of a write)
            using (var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, Person))
            {
                var again = await RunJobAsync<ApplyPlanJob>(new ApplyPlanInput(name, planId, File.ReadAllText(planFile)));
                Assert.Equal(RunOutcome.Failed, again.Outcome);
                Assert.Contains("deploy", again.Result!.End.Failure!.Message);
            }

            // an acknowledgement: a column added outside the tool blocks; the task records the decision, one block_log row naming the person and the reason
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("sneaky")} {engine.ColumnType("VARCHAR(5)")} NULL");
            using (var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, Person))
            {
                var acked = await RunJobAsync<AckJob>(new AckInput(name, "drift", "marts.fct_orders", "DBA added a scratch column"));
                Assert.True(acked.Outcome == RunOutcome.Succeeded, acked.Result?.End.Failure?.Message ?? acked.Outcome.ToString());
                var blocks = tracking + run.Q("block_log");
                Assert.Equal(1, await CountAsync(run, blocks, run.Q("ack_by") + $" = '{Person}' AND " + run.Q("ack_reason") + " = 'DBA added a scratch column'"));
                Assert.Equal(1, await CountAsync(run, blocks, "ack_utc IS NOT NULL"));

                // an acknowledgement without a reason never reaches the command
                var none = await RunJobAsync<AckJob>(new AckInput(name, "drift", "marts.fct_orders", " "));
                Assert.Equal(RunOutcome.Failed, none.Outcome);
                Assert.Contains("reason", none.Result!.End.Failure!.Message);
            }

            // a dry run of the task records nothing, whatever the operation
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("sneaky2")} {engine.ColumnType("VARCHAR(5)")} NULL");
            using (var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, Person))
            {
                var before = await CountAsync(run, tracking + run.Q("block_log"));
                using var dry = new RunSimulator<AckJob>(new AckInput(name, "drift", "marts.fct_orders", "only looking"), package: "sales-ops", channel: "prod", dryRun: true);
                Assert.Equal(RunOutcome.Succeeded, (await dry.RunAsync()).Outcome);
                Assert.Equal(before, await CountAsync(run, tracking + run.Q("block_log")));
            }

            // a run nobody asked for (no person recorded) is the run's own name, never the account the process runs as
            using (var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, null))
            {
                var anonymous = await RunJobAsync<AckJob>(new AckInput(name, "drift", "marts.fct_orders", "second one"));
                Assert.True(anonymous.Outcome == RunOutcome.Succeeded, anonymous.Result?.End.Failure?.Message ?? anonymous.Outcome.ToString());
                Assert.Equal(1, await CountAsync(run, tracking + run.Q("block_log"), run.Q("ack_by") + " = 'runner sales-ops/prod'"));
            }
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
