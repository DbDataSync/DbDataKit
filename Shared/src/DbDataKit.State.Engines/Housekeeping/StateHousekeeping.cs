using System.Globalization;
using DbDataKit.Monitoring;
using DbDataKit.State.Engines.Snapshots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataKit.State.Engines.Housekeeping;

/// <summary>
/// A server's upkeep of its own state, in its own process (plan 35 I1, decision 1): the retention rules of its modules (<see cref="RetentionJob"/>) every
/// <see cref="StateHousekeepingSettings.RetentionEvery"/>, and, when a folder is set, a verified snapshot of its state (<see cref="StateBackupJob"/>) every <see cref="StateHousekeepingSettings.BackupEvery"/>.
/// Every run is recorded in the state's <c>runs</c> module under the server's service name, with the job <see cref="RetentionJobId"/> or <see cref="BackupJobId"/>, its outcome, the rows it removed or wrote and one log line;
/// a failed run is recorded as failed and the next one still happens. Work on customer data never runs here: that is the Runner's (plan 35, "Housekeeping").
/// <para>
/// When a job is next due is read from its last recorded run (its start plus the interval), so a restart does not run it again early, and the instances of a group that share one state database
/// and one service name share its schedule. A job that has never run is first due a short while after the start (<see cref="FirstRunDelay"/>, or the interval when that is shorter), so a server
/// that starts does not write to its state at once.
/// </para>
/// Composed by every server (the console, the Workspace, the Gateway, the Runner) as a hosted service; the jobs can also be run once (<see cref="RunRetention"/>, <see cref="RunBackupAsync"/>).
/// </summary>
public sealed class StateHousekeeping : IHostedService, IAsyncDisposable
{
    public const string RetentionJobId = "state-retention";
    public const string BackupJobId = "state-backup";

    /// <summary>How long after the start a job that has never run is first due, at most.</summary>
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(5);

    private readonly StateDatabase _state;
    private readonly IReadOnlyList<StateModule> _modules;
    private readonly StateScope _scope;
    private readonly RetentionSettings _retention;
    private readonly RunStore _runs;
    private readonly ILogger _log;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private DateTimeOffset _started;

    /// <param name="state">The server's own state; it has to have the <c>runs</c> module, where the runs are recorded.</param>
    /// <param name="modules">The modules a snapshot holds: the server's.</param>
    /// <param name="scope">The scope the snapshot is taken in: the server's.</param>
    /// <param name="service">The service name recorded on each run (the server's).</param>
    /// <param name="settings">The server's state settings: their <see cref="StateSettings.Retention"/> and <see cref="StateSettings.Housekeeping"/>.</param>
    /// <param name="directory">The server's directory: a relative backup folder is read from it.</param>
    public StateHousekeeping(StateDatabase state, IReadOnlyList<StateModule> modules, StateScope scope, string service, StateSettings? settings, string directory, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        if (state.Layout is not { } layout || !layout.Has(RunsModule.Name))
            throw new InvalidOperationException($"The housekeeping of {service} records its runs in the '{RunsModule.Name}' module, which its state does not use: add RunsModule.Module to the server's modules.");
        _state = state;
        _modules = modules;
        _scope = scope;
        _retention = settings?.Retention ?? RetentionSettings.Defaults;
        Settings = settings?.Housekeeping ?? StateHousekeepingSettings.Defaults;
        BackupDirectory = Settings.BackupDirectoryIn(directory);
        Service = service;
        _runs = new RunStore(state, service);
        _log = log ?? NullLogger.Instance;
        _ = new RetentionJob(state, _retention);   // a retention setting that names a module or a table the state does not have stops the server at its start, not an hour later
    }

    public StateHousekeepingSettings Settings { get; }

    /// <summary>The full path of the backup folder; null when no snapshot is written.</summary>
    public string? BackupDirectory { get; }

    public string Service { get; }

    /// <summary>Runs the retention rules once and records the run.</summary>
    public RetentionResult RunRetention()
    {
        var runId = _runs.Start(RetentionJobId);
        try
        {
            var result = new RetentionJob(_state, _retention).Run();
            var removed = result.Removed.Values.Sum();
            var detail = string.Join(", ", result.Removed.Where(p => p.Value > 0).Select(p => $"{p.Key}: {p.Value.ToString(CultureInfo.InvariantCulture)}"));
            _runs.Log(runId, "info", removed == 0 ? "Nothing was old enough to remove." : $"Removed {removed.ToString(CultureInfo.InvariantCulture)} rows ({detail}).");
            _runs.Finish(runId, RunOutcome.Succeeded, removed);
            return result;
        }
        catch (Exception ex)
        {
            Fail(runId, RetentionJobId, ex);
            throw;
        }
    }

    /// <summary>Writes, verifies and prunes one snapshot of the state and records the run; refused when no folder is set.</summary>
    public async Task<StateBackupResult> RunBackupAsync(CancellationToken ct = default)
    {
        if (BackupDirectory is not { } folder)
            throw new InvalidOperationException($"No backup folder is set for {Service}: name one as state.housekeeping.backup_directory in its settings file.");
        var runId = _runs.Start(BackupJobId);
        try
        {
            var result = await new StateBackupJob(_state, _modules, _scope, new StateBackupSettings(folder, Settings.BackupKeep), _state.Time).RunAsync(ct).ConfigureAwait(false);
            _runs.Log(runId, "info", $"Wrote '{result.Directory}': {result.Rows.ToString(CultureInfo.InvariantCulture)} rows in {result.Tables.ToString(CultureInfo.InvariantCulture)} tables{(result.Verified ? ", verified against its manifest" : "")}" +
                                     (result.Pruned.Count > 0 ? $"; removed {result.Pruned.Count.ToString(CultureInfo.InvariantCulture)} older." : "."));
            _runs.Finish(runId, RunOutcome.Succeeded, result.Rows);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _runs.Finish(runId, RunOutcome.Stopped);
            throw;
        }
        catch (Exception ex)
        {
            Fail(runId, BackupJobId, ex);
            throw;
        }
    }

    /// <summary>When <paramref name="job"/> is next due: its last run's start plus <paramref name="every"/>, or a short while after the start when it has never run.</summary>
    public DateTimeOffset NextDue(string job, TimeSpan every)
    {
        var last = _runs.LastRun(job);
        if (last is null) return _started + (every < FirstRunDelay ? every : FirstRunDelay);
        return last.Run.Started + every;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = _state.Time.GetUtcNow();
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stop is not { } stop || _loop is not { } loop) return;
        await stop.CancelAsync().ConfigureAwait(false);
        try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* stopped */ }
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stop?.Dispose();
        _stop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMinutes(1);
            try
            {
                var now = _state.Time.GetUtcNow();
                var retentionDue = NextDue(RetentionJobId, Settings.RetentionEvery);
                if (retentionDue <= now) Try(RetentionJobId, RunRetention);
                DateTimeOffset? backupDue = null;
                if (BackupDirectory is not null)
                {
                    backupDue = NextDue(BackupJobId, Settings.BackupEvery);
                    if (backupDue <= now)
                    {
                        try { await RunBackupAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                        catch (Exception ex) { _log.LogWarning("The snapshot of {Service}'s state failed: {Message}", Service, ex.Message); }
                    }
                }

                now = _state.Time.GetUtcNow();
                var next = NextDue(RetentionJobId, Settings.RetentionEvery);
                if (BackupDirectory is not null) next = Min(next, NextDue(BackupJobId, Settings.BackupEvery));
                wait = next - now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the state could not be read (a database that is down): try again in a minute
                _log.LogWarning("The housekeeping of {Service} could not read its runs: {Message}", Service, ex.Message);
            }

            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            if (wait > TimeSpan.FromHours(1)) wait = TimeSpan.FromHours(1);   // another instance of a group may have run a job: look again at least hourly
            try { await Task.Delay(wait, _state.Time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Try(string job, Func<RetentionResult> run)
    {
        try { run(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("The {Job} of {Service}'s state failed: {Message}", job, Service, ex.Message); }
    }

    private void Fail(Guid runId, string job, Exception ex)
    {
        try
        {
            _runs.Log(runId, "error", $"{ex.GetType().Name}: {ex.Message}");
            _runs.Finish(runId, RunOutcome.Failed, failure: new RunFailure(ex.GetType().Name, ex.Message));
        }
        catch (Exception recording) when (recording is not OperationCanceledException)
        {
            _log.LogWarning("The failed {Job} of {Service} could not be recorded: {Message}", job, Service, recording.Message);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
