using DbDataKit.Monitoring;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.State.Engines.Housekeeping;
using DbDataKit.State.Engines.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>Plan 35 I1: the console keeps its own state in its own process (the shared <see cref="StateHousekeeping"/>): retention and a snapshot on a short interval, each run recorded, over SQLite, PostgreSQL and SQL Server.</summary>
public sealed class ConsoleHousekeepingTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _databases = [];

    public static TheoryData<string> Engines => ["sqlite", "postgres", "mssql"];

    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
        foreach (var directory in _directories) try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_console_runs_retention_and_a_snapshot_of_its_state_on_its_interval_and_records_each_run(string engine)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ddk-console-housekeeping-" + Guid.NewGuid().ToString("N")[..8]);
        _directories.Add(directory);
        var state = new StateSettings();
        if (engine != "sqlite")
        {
            var scratch = ScratchStateDatabase.For(engine);
            _databases.Add(scratch);
            state = new StateSettings(engine, scratch.Connection);
        }

        state = state with { Housekeeping = new StateHousekeepingSettings { RetentionEvery = TimeSpan.FromSeconds(1), BackupDirectory = "backups", BackupEvery = TimeSpan.FromSeconds(1), BackupKeep = 2 } };
        var settings = new ConsoleSettings(directory, new Uri("https://localhost:0"), State: state);
        var app = ConsoleHost.Build(settings, ConsoleHost.Certificate(directory), _ => { }, webRoot: directory);
        await app.StartAsync();
        try
        {
            var runs = new RunStore(app.Services.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId);
            int Succeeded(string job) => runs.Read(new RunQuery(ConsoleState.ServiceId, job, RunOutcome.Succeeded)).Count;
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until && (Succeeded(StateHousekeeping.RetentionJobId) < 2 || Succeeded(StateHousekeeping.BackupJobId) < 2)) await Task.Delay(200);

            Assert.True(Succeeded(StateHousekeeping.RetentionJobId) >= 2, "retention ran twice and was recorded");
            Assert.True(Succeeded(StateHousekeeping.BackupJobId) >= 2, "the snapshot ran twice and was recorded");
            var kept = StateBackupJob.List(Path.Combine(directory, "backups"));
            Assert.InRange(kept.Count, 1, 2);
            Assert.All(kept, k => Assert.True(File.Exists(Path.Combine(k, "manifest.json"))));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
