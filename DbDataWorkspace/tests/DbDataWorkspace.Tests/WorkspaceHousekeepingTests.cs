using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.State.Engines.Housekeeping;
using DbDataKit.State.Engines.Snapshots;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>Plan 35 I1: the Workspace keeps its own state in its own process: retention and a snapshot on the interval its <c>state.housekeeping</c> settings say, each run recorded in its state.</summary>
public sealed class WorkspaceHousekeepingTests
{
    public static TheoryData<string> Engines => ["sqlite", "postgres", "mssql"];

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_workspace_runs_retention_and_a_snapshot_on_its_interval_and_records_each_run(string engine)
    {
        await using var server = await WorkspaceHarness.StartAsync(engine,
            housekeeping: new StateHousekeepingSettings { RetentionEvery = TimeSpan.FromSeconds(1), BackupDirectory = "backups", BackupEvery = TimeSpan.FromSeconds(1), BackupKeep = 2 });
        var state = WorkspaceState.Open(server.Directory, server.Settings.State, server.Scratch is null ? null : server.Scratch.Environment);
        var runs = new RunStore(state, WorkspaceState.ServiceId);
        int Succeeded(string job) => runs.Read(new RunQuery(WorkspaceState.ServiceId, job, RunOutcome.Succeeded)).Count;

        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && (Succeeded(StateHousekeeping.RetentionJobId) < 2 || Succeeded(StateHousekeeping.BackupJobId) < 2)) await Task.Delay(200);

        Assert.True(Succeeded(StateHousekeeping.RetentionJobId) >= 2, "retention ran twice and was recorded");
        Assert.True(Succeeded(StateHousekeeping.BackupJobId) >= 2, "the snapshot ran twice and was recorded");
        var kept = StateBackupJob.List(Path.Combine(server.Directory, "backups"));
        Assert.InRange(kept.Count, 1, 2);
        Assert.All(kept, k => Assert.True(File.Exists(Path.Combine(k, "manifest.json"))));
    }
}
