using DbDataBuild.Cli.Runner;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Testing;
using DbDataKit.State;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// (A part of the apply group.) Plan 35 I3 on real engines: the runtime's <c>refresh</c> task, run through the kit's run simulator in this process (the real task entry, the real command, the in-process kit over the simulator's state), evaluates
/// the checks of the models it loaded after the loads, with no results directory, and records them in the runner's <c>quality</c> module under the run's own id: a failing block check blocks, and fixing the data and refreshing again clears it.
/// Over the protocol the same results go through <c>QualityRecord</c> (<c>QualityProtocolTests</c> in the kit's tests).
/// </summary>
public partial class ApplyConformanceTests
{
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_refresh_on_the_runner_evaluates_the_checks_of_the_models_it_loaded_and_the_runner_keeps_the_results(string name)
    {
        var (run, _) = await ChecksSetUp(name);
        await using var engine = run.Engine;
        try
        {
            using var env = new TaskEnvironment(run.Dir, name, engine.ConnectionString, null);
            using var simulator = new RunSimulator<RefreshJob>(new RefreshInput(name), package: "sales", channel: "prod");
            var first = await simulator.RunAsync();

            Assert.True(first.Outcome == RunOutcome.Succeeded, first.Result?.End.Failure?.Message ?? first.Outcome.ToString());    // a failed check is a finding, not a failure of the refresh
            var identity = Identity(run, name, "marts", "a_orders");
            var store = new QualityStore(simulator.Database);
            var failed = Assert.Single(store.History(identity, "amount_present"));
            Assert.Equal((CheckOutcome.Fail, first.RunId.ToString("D")), (failed.Outcome, failed.RunId));       // under the runner's run, not the refresh's tracking id
            Assert.Equal(["order_id=3"], failed.SampleKeys);
            Assert.Equal(["amount_present"], store.BlocksOf(identity).Select(b => b.CheckId));
            Assert.Equal(4, store.StatusesOf(identity).Count);

            await engine.ExecAsync("UPDATE staging.orders SET amount = 30.00 WHERE amount IS NULL");
            var second = await simulator.RunAsync();

            Assert.True(second.Outcome == RunOutcome.Succeeded, second.Result?.End.Failure?.Message ?? second.Outcome.ToString());
            Assert.Empty(store.BlocksOf(identity));
            Assert.Equal([CheckOutcome.Pass, CheckOutcome.Fail], store.History(identity, "amount_present").Select(r => r.Outcome));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
