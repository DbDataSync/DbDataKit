using DbDataKit.Monitoring;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.State.Engines.Housekeeping;
using DbDataKit.State.Engines.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 35 I1 and I2: the console keeps its own state in its own process (the shared <see cref="StateHousekeeping"/>): retention and a snapshot on a short interval, each run recorded, over SQLite, PostgreSQL and SQL Server, with no Runner paired
/// and no runner module in its state. The embedded runner is gone: a <c>runner:</c> section of <c>settings.yaml</c> stops the console naming the file, and the <c>runner</c> commands say where their feature went.
/// </summary>
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

            // no runner is paired and the console's state holds no module of a runner's (plan 35 I2)
            Assert.Empty(app.Services.GetRequiredService<ConsoleInstances>().Registry.All());
            var layout = app.Services.GetRequiredService<StateDatabase>().Layout!;
            foreach (var module in new[] { RunnerModule.Name, DefinitionsModule.Name, DbDataKit.Scheduling.SchedulingModule.Name })
                Assert.False(layout.Has(module), $"the console's state has the runner's module {module}");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void A_settings_file_with_the_runner_section_of_the_embedded_runner_stops_the_console_naming_the_file_and_where_its_settings_went()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ddk-console-no-runner-" + Guid.NewGuid().ToString("N")[..8]);
        _directories.Add(directory);
        Directory.CreateDirectory(directory);
        var file = ConsoleState.SettingsFileIn(directory);
        File.WriteAllText(file, "runner:\n  enabled: true\n  backup_directory: backups\n");

        var refused = Assert.Throws<InvalidOperationException>(() => ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: directory));

        Assert.Contains(file, refused.Message, StringComparison.Ordinal);
        Assert.Contains("state.housekeeping", refused.Message, StringComparison.Ordinal);
        Assert.Contains("DbDataRunner", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_runner_commands_of_the_embedded_runner_say_where_their_feature_went()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await ConsoleCli.RunAsync(["runner", "accept-production", "--yes"], new DbDataKit.Cli.CliContext(output, error, TextReader.Null));

        Assert.Equal(DbDataKit.Cli.CliExit.Usage, exit);
        Assert.Contains("'dbdatakit runner' is gone", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("DbDataRunner server", error.ToString(), StringComparison.Ordinal);
    }
}
