namespace DbDataSync.State.Tests;

public sealed class StateDatabaseTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Constructing_CreatesTheModulesTables()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        var database = new StateDatabase(dbPath);

        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name != 'sqlite_sequence' ORDER BY name;");
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        // Sync's own module and the shared ones it uses; the sign-in, the old queue and the old position tables of the structure before the modules are not there
        foreach (var expected in new[]
        {
            "dbdatakit_modules",
            "dbdatakit_sync_state_main_run_detail", "dbdatakit_sync_state_main_change_check", "dbdatakit_sync_state_main_verification_result", "dbdatakit_sync_state_main_bulk_load_batch",
            "dbdatakit_runs_run", "dbdatakit_runs_run_log", "dbdatakit_scheduling_work_item", "dbdatakit_scheduling_run_lock", "dbdatakit_scheduling_job_pause",
            "dbdatakit_notifications_notification", "dbdatakit_audit_entry", "dbdatakit_runner_progress_main_checkpoint_head", "dbdatakit_runner_progress_main_checkpoint_part",
        })
            Assert.Contains(expected, tables);
        foreach (var gone in new[] { "TaskRuns", "ChangeWatermarks", "WorkQueue", "RunLocks", "Logs", "PauseEvents", "Tasks", "Users" })
            Assert.DoesNotContain(gone, tables);
    }

    [Fact]
    public void ShowListsTheSyncModulesAndTheirVersions()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));

        var modules = database.RegisteredModules().ToDictionary(m => m.Module, m => m);

        foreach (var module in SyncState.Modules)
            Assert.Equal(module.Version, modules[module.Name].Version);
        Assert.Equal("main", modules[SyncStateModule.Name].Scope);
        Assert.Equal("install", modules[RunsModule.Name].Scope);
    }

    [Fact]
    public void ReopeningExistingDatabase_DoesNotRerunMigrations()
    {
        var dbPath = Path.Combine(_tempDir, "state.db");
        var first = new StateDatabase(dbPath);
        var versionsAfterFirstOpen = first.RegisteredModules().Select(m => (m.Module, m.Version)).OrderBy(m => m.Module).ToList();

        // A second StateDatabase against the same file must not fail by trying to CREATE TABLE again.
        var reopened = new StateDatabase(dbPath);

        // Asserted against the first open's own versions rather than hardcoded numbers, so that adding a migration doesn't require editing this test to keep testing the same thing.
        Assert.True(versionsAfterFirstOpen.Count >= 6 && versionsAfterFirstOpen.All(m => m.Version > 0), "Migrations should have been applied on first open.");
        Assert.Equal(versionsAfterFirstOpen, reopened.RegisteredModules().Select(m => (m.Module, m.Version)).OrderBy(m => m.Module).ToList());
    }

    [Fact]
    public void TheSyncStateModule_KeepsTheStateConventions_AndTheRetentionTheOwnerDecided()
    {
        Assert.Empty(StateConventions.Violations(SyncStateModule.Module));

        // plan 28c QE9: a run's detail goes with its run after 30 days, the polling gate's trail after 7
        var rules = SyncStateModule.Module.Retention.ToDictionary(r => r.Table);
        Assert.Equal(30, rules["run_detail"].DefaultDays);
        Assert.Equal(7, rules["change_check"].DefaultDays);
        Assert.Equal(30, RunsModule.Module.Retention.Single(r => r.Table == "run").DefaultDays);
        Assert.Equal(StateScopeKind.Group, SyncStateModule.Module.Scope);
    }

    [Fact]
    public void OpenConnection_SetsBusyTimeout()
    {
        // Not WAL mode — see the XML doc on StateDatabase.OpenConnection for why: WAL's cross-process
        // shared-memory coordination proved unreliable in this project's sandboxed dev environment.
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "PRAGMA busy_timeout;");
        Assert.Equal(5000L, Convert.ToInt64(cmd.ExecuteScalar()));
    }
}
