using System.Text.Json.Nodes;
using DbDataBuild.Cli.Runner;
using DbDataKit.Monitoring;
using DbDataKit.Quality;
using DbDataKit.Runner.Kit.Testing;
using DbDataKit.State;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// (A part of the apply group.) Plan 35 I3a on real engines: the refresh package's <c>check</c> task, run through the kit's run simulator in this process over the same runner state as the refresh before it. The refresh blocks
/// <c>marts.a_orders</c> on a failing not-null check; the rows are fixed in the table by hand; <c>check</c> evaluates the package's checks again, read only, records them under its own run, and the block clears, with no load: the tracking
/// tables have no new row and the source still holds the bad row (a reload would have brought it back).
/// </summary>
public partial class ApplyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_check_task_evaluates_the_checks_again_without_a_load_and_the_block_clears_when_the_data_was_fixed(string name)
    {
        var (run, _) = await ChecksSetUp(name);
        await using var engine = run.Engine;
        try
        {
            using var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, null);
            using var refresh = new RunSimulator<RefreshJob>(new RefreshInput(name), package: "sales", channel: "prod");
            var loaded = await refresh.RunAsync();
            Assert.True(loaded.Outcome == RunOutcome.Succeeded, loaded.Result?.End.Failure?.Message ?? loaded.Outcome.ToString());
            var identity = Identity(run, name, "marts", "a_orders");
            var store = new QualityStore(refresh.Database);
            Assert.Equal(["amount_present"], store.BlocksOf(identity).Select(b => b.CheckId));

            var runLog = $"{run.Q("dbdatabuild")}.{run.Q("run_log")}";
            var loadsBefore = await CountAsync(run, runLog);

            // the data is still bad: the re-check records a second fail, under its own run, and the block stays
            using (var still = new RunSimulator<CheckJob>(new QualityCheckInput(), package: "sales", channel: "prod", database: refresh.Database))
            {
                var attempt = await still.RunAsync();
                Assert.True(attempt.Outcome == RunOutcome.Succeeded, attempt.Result?.End.Failure?.Message ?? attempt.Outcome.ToString());     // a failing check is a finding
                Assert.Equal(attempt.RunId.ToString("D"), store.History(identity, "amount_present").First().RunId);
                Assert.Equal(["amount_present"], store.BlocksOf(identity).Select(b => b.CheckId));
                var document = CheckResult(still);
                Assert.Equal((4, 1, 1), ((int)document["evaluated"]!, (int)document["failed"]!, (int)document["blocking"]!));
            }

            // someone fixes the rows in the table at 10:00; Re-check, for that dataset only, by its model's name
            await engine.ExecAsync("UPDATE marts.a_orders SET amount = 30.00 WHERE amount IS NULL");
            using var check = new RunSimulator<CheckJob>(new QualityCheckInput("marts.a_orders"), package: "sales", channel: "prod", database: refresh.Database);
            var second = await check.RunAsync();

            Assert.True(second.Outcome == RunOutcome.Succeeded, second.Result?.End.Failure?.Message ?? second.Outcome.ToString());
            Assert.Empty(store.BlocksOf(identity));
            Assert.Equal([CheckOutcome.Pass, CheckOutcome.Fail, CheckOutcome.Fail], store.History(identity, "amount_present").Select(r => r.Outcome));
            Assert.Equal(second.RunId.ToString("D"), store.History(identity, "amount_present").First().RunId);
            var result = CheckResult(check);
            Assert.Equal(("marts.a_orders", 4, 4, 0), ((string)result["dataset"]!, (int)result["evaluated"]!, (int)result["passed"]!, (int)result["blocking"]!));   // the freshness check read the last load from the tracking tables
            Assert.All(result["results"]!.AsArray(), r => Assert.Equal("marts.a_orders", (string)r!["name"]!));

            // nothing was loaded: no new tracking row, and the source still has the row a reload would have copied back
            Assert.Equal(loadsBefore, await CountAsync(run, runLog));
            Assert.Equal(1, await CountAsync(run, "staging.orders", $"{run.Q("amount")} IS NULL"));

            // a dataset the package does not have fails the run, naming what it has
            using var unknown = new RunSimulator<CheckJob>(new QualityCheckInput("marts.nothing"), package: "sales", channel: "prod", database: refresh.Database);
            var refused = await unknown.RunAsync();
            Assert.Equal(RunOutcome.Failed, refused.Outcome);
            Assert.Contains("marts.a_orders", refused.Result!.End.Failure!.Message);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    private static JsonObject CheckResult(RunSimulator<CheckJob> simulator)
    {
        var step = Assert.Single(simulator.StoredSteps(), s => s.Name == QualityCheckTask.StepName);
        return (JsonObject)JsonNode.Parse(step.ResultJson!)!;
    }
}
