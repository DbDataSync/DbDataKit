using DbDataSync.Core.Sql;
using DbDataSync.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataKit.Config.Git;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.ArrowFlightSql;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.MySql;
using DbDataSync.Drivers.Oracle;
using DbDataSync.Drivers.Postgres;
using DbDataSync.Libraries;
using DbDataSync.Scripting;
using DbDataSync.State;
using DbDataSync.State.Remote;

namespace DbDataSync.TaskRunner;

/// <summary>
/// What a worker process does: parse its arguments, compose the drivers, libraries and scripts, claim work for one replication and
/// run it. Called by this project's own executable and by <c>dbdatasync internal worker</c>, which is how the API starts a worker without
/// needing the shared .NET runtime on the command line (<c>dotnet exec</c>).
/// </summary>
public static class WorkerEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TaskRunnerOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"Argument error: {parseError}");
            Console.Error.WriteLine(
                "Usage: DbDataSync.TaskRunner --repo-root <path> --state-db <path> --replication <name> " +
                "[--degree-of-parallelism <n>] [--bulk-load-parallelism <n>]");
            return (int)ExitCode.ConfigError;
        }

        var secretStore = SecretStore.Open(options!.RepoRoot);
        var configRepository = new ConfigRepository(options!.ConfigRoot, new GitCommitService(options.RepoRoot), secretStore);
        var engine = EngineComposition.Compose(
            configRepository, options.RepoRoot, ScriptCacheDirectory.BesideStateDatabase(options.StateDbPath));
        var scriptHost = engine.Scripts;
        var libraryRegistry = engine.Libraries;
        var driverRegistry = engine.Drivers;

        // Phase 39: a runner spawned by the API never opens the state file. It applies its changes over
        // loopback to the process that owns it, and journals to disk if that process goes away mid-run.
        // Phase 94: the same is true of the one config write a run makes — the target shape its own
        // provisioning produced. It reports it; the API commits it.
        var (state, runnerConfig, disposeState) = RunnerStateFactory.Create(options);
        using var _stateScope = disposeState;
        var executor = new RunExecutor(
            configRepository, driverRegistry, secretStore, libraryRegistry, options.RepoRoot, state, runnerConfig,
            scriptHost, options.StateDbPath);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        ExitCode exitCode;
        try
        {
            exitCode = await executor.ExecuteWorkerAsync(options.Replication, options.Lanes, cts.Token);
        }
        catch (StateOwnerUnavailableException ex)
        {
            // Not a failure of the work — the owner went away. Whatever this run had already achieved is in a
            // journal beside the state database, and the owner applies it before scheduling anything new for
            // this replication.
            Console.Error.WriteLine($"State owner unavailable: {ex.Message}");
            exitCode = ExitCode.StateOwnerUnavailable;
        }

        // Flush before the process ends: buffered log lines either reach the owner or reach the journal, and
        // silently dropping them is the one outcome that leaves an operator with nothing.
        if (state is RemoteRunnerState remote)
        {
            remote.Flush();
            if (remote.OwnerLost)
                exitCode = ExitCode.StateOwnerUnavailable;
        }

        return (int)exitCode;
    }
}
