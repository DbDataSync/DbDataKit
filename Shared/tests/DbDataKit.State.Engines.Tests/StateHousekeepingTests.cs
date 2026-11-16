using DbDataKit.Monitoring;
using DbDataKit.State.Engines.Housekeeping;
using DbDataKit.State.Engines.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Plan 35 I1: a server's own state is kept in its own process: retention and a snapshot on an interval, each run recorded in the <c>runs</c> module; the settings are the <c>housekeeping:</c> part of the state section.</summary>
public sealed class StateHousekeepingTests : IDisposable
{
    private readonly ServiceScratch _scratch = new();
    private static readonly StateModule[] Modules = ServiceData.Modules;
    private static readonly StateScope Scope = StateScope.Standalone;

    public void Dispose() => _scratch.Dispose();

    private StateDatabase Open(string engine)
    {
        var endpoint = _scratch.Provision(engine);
        return StateOpener.Open(endpoint.Settings, endpoint.SqliteFile!, Modules, Scope, _scratch.Resolver);
    }

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task Retention_and_a_snapshot_run_on_their_interval_and_each_run_is_recorded(string engine)
    {
        var database = Open(engine);
        ServiceData.Fill(database);
        // a run of another job, older than the runs module's 30 days: retention removes it
        var old = new RunStore(database, "elsewhere");
        var oldRun = old.Start("old-job", at: database.Time.GetUtcNow() - TimeSpan.FromDays(40));
        old.Finish(oldRun, RunOutcome.Succeeded, at: database.Time.GetUtcNow() - TimeSpan.FromDays(40));
        var settings = new StateSettings
        {
            Housekeeping = new StateHousekeepingSettings { RetentionEvery = TimeSpan.FromSeconds(1), BackupDirectory = "backups", BackupEvery = TimeSpan.FromSeconds(1), BackupKeep = 2 },
        };
        await using var housekeeping = new StateHousekeeping(database, Modules, Scope, "dbdatatest", settings, _scratch.Directory);
        Assert.Equal(Path.Combine(_scratch.Directory, "backups"), housekeeping.BackupDirectory);

        await housekeeping.StartAsync(CancellationToken.None);
        // the state is read only after the loop stopped: a read while a snapshot holds its transaction is what this suite's SQLite lock guard refuses; the folder says how far it got
        // (each turn runs retention before the snapshot, so two snapshots mean two retentions)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline && StateBackupJob.List(Path.Combine(_scratch.Directory, "backups")).Count < 2)
            await Task.Delay(200);
        await housekeeping.StopAsync(CancellationToken.None);
        var runs = new RunStore(database, "dbdatatest");

        var retention = runs.Read(new RunQuery("dbdatatest", StateHousekeeping.RetentionJobId));
        Assert.True(retention.Count(r => r.Run.Outcome == RunOutcome.Succeeded) >= 2, $"retention ran {retention.Count} times");
        Assert.Null(old.Get(oldRun));
        Assert.Contains(retention, r => runs.Logs(r.Id).Any(l => l.Message.Contains("runs.run: ", StringComparison.Ordinal)));
        var backups = runs.Read(new RunQuery("dbdatatest", StateHousekeeping.BackupJobId, RunOutcome.Succeeded));
        Assert.True(backups.Count >= 2, $"the snapshot ran {backups.Count} times");
        Assert.All(backups, b => Assert.True(b.Run.Rows > 0));
        var kept = StateBackupJob.List(housekeeping.BackupDirectory!);
        Assert.InRange(kept.Count, 1, 2);
        Assert.All(kept, k => Assert.True(File.Exists(Path.Combine(k, "manifest.json"))));
    }

    [Fact]
    public async Task A_failed_snapshot_is_recorded_as_failed_and_retention_goes_on()
    {
        var database = Open("sqlite");
        var blocker = Path.Combine(_scratch.Directory, "not-a-folder");
        File.WriteAllText(blocker, "a file where the backup folder should be");
        var settings = new StateSettings { Housekeeping = new StateHousekeepingSettings { BackupDirectory = blocker } };
        await using var housekeeping = new StateHousekeeping(database, Modules, Scope, "dbdatatest", settings, _scratch.Directory);

        await Assert.ThrowsAnyAsync<IOException>(() => housekeeping.RunBackupAsync());
        housekeeping.RunRetention();

        var runs = new RunStore(database, "dbdatatest");
        var failed = Assert.Single(runs.Read(new RunQuery("dbdatatest", StateHousekeeping.BackupJobId)));
        Assert.Equal(RunOutcome.Failed, failed.Run.Outcome);
        Assert.NotNull(failed.Run.Failure);
        Assert.Equal(RunOutcome.Succeeded, Assert.Single(runs.Read(new RunQuery("dbdatatest", StateHousekeeping.RetentionJobId))).Run.Outcome);
    }

    [Fact]
    public void A_job_is_next_due_one_interval_after_its_last_run_so_a_restart_does_not_run_it_early()
    {
        var database = Open("sqlite");
        var housekeeping = new StateHousekeeping(database, Modules, Scope, "dbdatatest", new StateSettings(), _scratch.Directory);
        housekeeping.RunRetention();
        var last = new RunStore(database, "dbdatatest").LastRun(StateHousekeeping.RetentionJobId)!;

        var restarted = new StateHousekeeping(database, Modules, Scope, "dbdatatest", new StateSettings(), _scratch.Directory);

        Assert.Equal(last.Run.Started + TimeSpan.FromHours(1), restarted.NextDue(StateHousekeeping.RetentionJobId, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_snapshot_without_a_folder_is_refused_naming_the_setting_and_a_state_without_runs_is_refused()
    {
        var database = Open("sqlite");
        var housekeeping = new StateHousekeeping(database, Modules, Scope, "dbdatatest", new StateSettings(), _scratch.Directory);
        Assert.Null(housekeeping.BackupDirectory);
        var refused = Assert.Throws<InvalidOperationException>(() => housekeeping.RunBackupAsync().GetAwaiter().GetResult());
        Assert.Contains("state.housekeeping.backup_directory", refused.Message, StringComparison.Ordinal);

        var endpoint = _scratch.Provision("sqlite");
        var withoutRuns = StateOpener.Open(endpoint.Settings, endpoint.SqliteFile!, [IdentityModule.Module], Scope, _scratch.Resolver);
        var error = Assert.Throws<InvalidOperationException>(() => new StateHousekeeping(withoutRuns, [IdentityModule.Module], Scope, "dbdatatest", null, _scratch.Directory));
        Assert.Contains("'runs'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_housekeeping_settings_are_read_from_the_state_section_strictly_and_written_back()
    {
        var section = new Dictionary<string, object?>
        {
            ["housekeeping"] = new Dictionary<string, object?> { ["retention_every_minutes"] = "15", ["backup_directory"] = "snapshots", ["backup_every_hours"] = "6", ["backup_keep"] = "3" },
        };
        var settings = StateSettingsReader.Read(section);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.Housekeeping.RetentionEvery);
        Assert.Equal("snapshots", settings.Housekeeping.BackupDirectory);
        Assert.Equal(TimeSpan.FromHours(6), settings.Housekeeping.BackupEvery);
        Assert.Equal(3, settings.Housekeeping.BackupKeep);
        Assert.Equal(StateHousekeepingSettings.Defaults, StateSettingsReader.Read(null).Housekeeping);

        var file = Path.Combine(_scratch.Directory, "server.yaml");
        new StateSettingsWriter().Write(file, settings);
        Assert.Contains("  housekeeping:", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(settings.Housekeeping, StateSettingsWriter.Load(file).Housekeeping);

        var unknown = Assert.Throws<InvalidOperationException>(() => StateSettingsReader.Read(new Dictionary<string, object?> { ["housekeeping"] = new Dictionary<string, object?> { ["backup_kept"] = "3" } }));
        Assert.Contains("state.housekeeping.backup_kept", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("backup_keep", unknown.Message, StringComparison.Ordinal);
        var zero = Assert.Throws<InvalidOperationException>(() => StateSettingsReader.Read(new Dictionary<string, object?> { ["housekeeping"] = new Dictionary<string, object?> { ["retention_every_minutes"] = "0" } }));
        Assert.Contains("state.housekeeping.retention_every_minutes", zero.Message, StringComparison.Ordinal);
    }
}
