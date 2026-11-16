using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.State.Engines.Housekeeping;
using DbDataKit.State.Engines.Snapshots;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>Plan 35 I1: the runner keeps its own state in its own process (not as a task): retention and a snapshot on the interval of its <c>state.housekeeping</c> settings, each run recorded under the runner's service name.</summary>
public sealed class RunnerHousekeepingTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_runner_runs_retention_and_a_snapshot_of_its_state_on_its_interval_and_records_each_run(string engine)
    {
        await using var runner = new RunnerHarness(engine);
        runner.Settings = runner.Settings with
        {
            State = runner.Settings.State with { Housekeeping = new StateHousekeepingSettings { RetentionEvery = TimeSpan.FromSeconds(1), BackupDirectory = "backups", BackupEvery = TimeSpan.FromSeconds(1), BackupKeep = 2 } },
        };
        await runner.StartAsync();
        var runs = new RunStore(runner.State, RunnerState.ServiceId);
        int Succeeded(string job) => runs.Read(new RunQuery(RunnerState.ServiceId, job, RunOutcome.Succeeded)).Count;

        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && (Succeeded(StateHousekeeping.RetentionJobId) < 2 || Succeeded(StateHousekeeping.BackupJobId) < 2)) await Task.Delay(200);

        Assert.True(Succeeded(StateHousekeeping.RetentionJobId) >= 2, "retention ran twice and was recorded");
        Assert.True(Succeeded(StateHousekeeping.BackupJobId) >= 2, "the snapshot ran twice and was recorded");
        // the task runs of the engine are another service: a housekeeping run is never one of them
        Assert.DoesNotContain(runner.Runs.Read(new RunQuery(RunnerState.EngineService)), r => r.Run.JobId.StartsWith("state-", StringComparison.Ordinal));
        var kept = StateBackupJob.List(Path.Combine(runner.Directory, "backups"));
        Assert.InRange(kept.Count, 1, 2);
        Assert.All(kept, k => Assert.True(File.Exists(Path.Combine(k, "manifest.json"))));
    }
}
