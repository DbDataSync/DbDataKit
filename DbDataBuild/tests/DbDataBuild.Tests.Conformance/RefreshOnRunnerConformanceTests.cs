using DbDataKit.Monitoring;
using DbDataKit.Runner;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 28c, E01 and E05: one refresh plan as a task of a real dbdatarunner (in process, its task process real): the engine is published as the runtime package `dbdatabuild-runtime` and the project's committed files as a package
/// that names it by range (<see cref="BuildOnRunner"/>), run on the runner's schedule and by run now, beside the old path. The tracking rows of the target are what `connection refresh` writes, a failed load is a failed run that names it, a plan that does not exist is refused with
/// DDB-450, and the layout of the tracking tables is the one a run without the runner leaves.
/// </summary>
public partial class ApplyConformanceTests
{
    /// <summary>What an event of the plan left in the tracking tables, without its identity, times and the plan id: one line for the event, one for each step.</summary>
    private static async Task<List<string>> EventSignaturesAsync(Run run)
    {
        var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
        var events = await run.Engine.RowsAsync($"SELECT {run.Q("plan_id")} FROM {T("migration_log")} WHERE {run.Q("refresh_plan")} = 'p' AND status <> 'started' ORDER BY {run.Q("applied_utc")}");
        var signatures = new List<string>();
        foreach (var id in events)
        {
            var head = await run.Engine.RowsAsync($"SELECT CONCAT({run.Q("lane")}, '|', status, '|', {run.Q("project_hash")}, '|', {run.Q("refresh_plan")}, '|', {run.Q("selection")}, '|', {run.Q("hash_before")}, '|', {run.Q("hash_after")}) FROM {T("migration_log")} WHERE {run.Q("plan_id")} = '{id}' AND status <> 'started'");
            var steps = await run.Engine.RowsAsync($"SELECT CONCAT({run.Q("model")}, '|', {run.Q("operation")}, '|', status, '|', rows_affected, '|', {run.Q("membership")}, '|', {run.Q("load_plan_hash")}, '|', {run.Q("attempt")}, '|', {run.Q("definition_hash")}) FROM {T("run_log")} WHERE {run.Q("plan_id")} = '{id}' ORDER BY {run.Q("model")}, {run.Q("operation")}");
            var members = await run.Engine.RowsAsync($"SELECT CONCAT({run.Q("membership")}, '|', {run.Q("model")}, '|', {run.Q("load_plan_hash")}, '|', {run.Q("selected")}) FROM {T("refresh_plan_member")} WHERE {run.Q("plan_id")} = '{id}' ORDER BY {run.Q("position")}");
            signatures.Add(string.Join("\n", [.. head, .. steps, .. members]));
        }

        return signatures;
    }

    private static async Task<string> TrackingLayoutAsync(Run run) =>
        string.Join("\n", await run.Engine.RowsAsync($"SELECT CONCAT(table_name, '.', column_name, '.', data_type, '.', CAST(ordinal_position AS VARCHAR(10)), '.', is_nullable) FROM information_schema.columns WHERE table_schema = 'dbdatabuild' ORDER BY table_name, ordinal_position"));

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_refresh_plan_runs_as_a_runner_task_on_its_schedule_and_by_run_now_and_leaves_the_rows_the_command_leaves(string name)
    {
        var run = await FailureSetUp(name, "    p: { on_failure: continue }\n    q: { on_failure: continue }\n", "refresh: { p: default, q: default }\n");
        await using var engine = run.Engine;
        try
        {
            // the old path: the command, which is what the runner's task must leave behind
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            Ok(run.Cli("connection", "refresh", name, "p"), "the command");
            var baseline = await EventSignaturesAsync(run);
            Assert.Single(baseline);
            Assert.StartsWith("refresh|completed|", baseline[0]);
            var layout = await TrackingLayoutAsync(run);

            await using var runner = await BuildOnRunner.StartAsync(run.Dir, name, run.Engine.ConnectionString, every: TimeSpan.FromSeconds(2));

            // on the runner's schedule: nothing asked for it
            var scheduled = (await runner.WaitForRunsAsync(1, "the first scheduled run")).First();
            runner.Engine.Control!.Pause(BuildOnRunner.Task, RunnerActor.Local());
            await runner.WaitForRunsAsync(1, "the runs in flight to end");   // a run may have started before the pause: it finishes
            runner.Expect(RunOutcome.Succeeded, scheduled);

            // the engine's first start unpacked its natives into the runtime's directory under the runner's cache (not into the read-only view, not under the home directory), and the objects of the cache were not touched
            var extracted = BuildOnRunner.Snapshot(runner.ExtractDirectory);
            Assert.Contains("duckdb", extracted, StringComparison.OrdinalIgnoreCase);
            var objects = BuildOnRunner.Snapshot(Path.Combine(runner.DataDirectory, "cache", "objects"));
            Assert.NotEmpty(objects);

            // by run now (the task is paused: a person's run goes ahead)
            var now = await runner.RunNowAsync();
            runner.Expect(RunOutcome.Succeeded, now);

            // the runner's run row: package, version, channel, outcome, the rows moved
            foreach (var r in runner.Runs())
            {
                Assert.Equal(BuildOnRunner.DefinitionPackage, r.Package);
                Assert.Equal("dev", r.Channel);
                Assert.False(string.IsNullOrEmpty(r.PackageVersion));
                runner.Expect(RunOutcome.Succeeded, r);
                Assert.True(r.Run.Rows > 0, "the run row counts the rows the loads moved");
            }

            // the target's rows: every event of the runner's runs equals the command's (the same plan, the same hashes, the same rows, the same status, the lane `refresh`)
            var signatures = await EventSignaturesAsync(run);
            Assert.Equal(1 + runner.Runs().Count, signatures.Count);
            foreach (var signature in signatures.Skip(1)) Assert.Equal(baseline[0], signature);
            Assert.Equal(layout, await TrackingLayoutAsync(run));
            Assert.Equal(signatures.Count, await CountAsync(run, $"{run.Q("dbdatabuild")}.{run.Q("migration_log")}", $"{run.Q("refresh_plan")} = 'p' AND status = 'completed'"));

            // a failing load: the run is a completed-with-failures run that names it (p: the others ran), and a failed run when none finished (q)
            await engine.ExecAsync("INSERT INTO staging.flag VALUES (1)");
            var partial = await runner.RunNowAsync();
            runner.Expect(RunOutcome.CompletedWithFailures, partial);
            Assert.Equal("marts.a_orders.default", partial.Run.FailedItem);
            var failed = await runner.RunNowAsync("q");
            runner.Expect(RunOutcome.Failed, failed);
            Assert.Contains("marts.a_orders.default", failed.Run.FailedItem + failed.Run.Failure?.Message);

            // a plan that does not exist is refused at the entry, and the run says the diagnostic
            var unknown = await runner.RunNowAsync("nope");
            runner.Expect(RunOutcome.Failed, unknown);
            Assert.Contains("DDB-450", unknown.Run.Failure?.Message);
            Assert.Equal(layout, await TrackingLayoutAsync(run));

            // every later run, a scheduled one and the others, reused what the first unpacked: nothing in the extraction directory was written again, and the stored objects stayed as they were; no view outlives its run
            await runner.WaitForRunsAsync(runner.Runs().Count, "the runs to end");
            Assert.Equal(extracted, BuildOnRunner.Snapshot(runner.ExtractDirectory));
            Assert.Equal(objects, BuildOnRunner.Snapshot(Path.Combine(runner.DataDirectory, "cache", "objects")));
            await runner.WaitForNoViewsAsync();
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
