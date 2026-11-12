using System.Collections.Concurrent;
using DbDataKit.State.Tracking;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;
using DbDataSync.Tracking;

namespace DbDataSync.Api.Services;

/// <summary>What the last run of the follower of one replication did (plan 28c E17).</summary>
/// <param name="Identity">The identity the units are copied under (the target as it really is).</param>
public sealed record SyncCopyStatus(string Identity, DateTimeOffset At, int Copied, bool CaughtUp, string? Error, long? OpenDivergenceId, string? PositionProblem);

/// <summary>
/// Keeps the state's copy of the units of every replication that keeps its state on the target (plan 28c E17, mode <c>target</c> or <c>both</c>): a minute apart it copies what the target recorded
/// in <c>dbdatasync.unit</c> since the follower's checkpoint (<see cref="SyncUnitFollower"/>) and checks that the position the state holds for each mapping is one the target recorded, which persists a
/// divergence for a target restored from a backup or a state that was reset. It runs in the API process because that process is the state store's one writer (phase 39). A target that is down is the
/// <c>Error</c> of the status, never an exception, and never slows a run: the passes do not wait for the follower.
/// <para>Limit (documented): the replication's own target connection is followed; a mapping that names another target keeps its units there and is not copied.</para>
/// </summary>
public sealed class SyncTrackingService(ConfigRepository config, IConnectionFactory connections, StateDatabase state, ChangeWatermarkStore watermarks, ILogger<SyncTrackingService> logger, TimeProvider? time = null) : BackgroundService
{
    /// <summary>How often every replication is followed (default taken: a minute).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The time one catch-up of one replication may take.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SyncCopyStatus> _last = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await TickAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>The last result for a replication, or null when it has not been followed since the server started.</summary>
    public SyncCopyStatus? Last(string replication) => _last.TryGetValue(replication, out var status) ? status : null;

    /// <summary>One round: every replication that asks for the target's state is followed. Public for the tests.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names;
        try { names = config.ListReplications(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The replications could not be listed to follow their units."); return; }
        foreach (var name in names)
        {
            try { await FollowAsync(name, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Following the units of '{Replication}' failed.", name); }
        }
    }

    /// <summary>Follows one replication now (the console's "catch up" button too). Null when its setting keeps nothing on the target, or its target is not one a writer exists for.</summary>
    public async Task<SyncCopyStatus?> FollowAsync(string replication, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        try { task = config.LoadReplicationTask(replication); }
        catch (Exception ex) when (ex is FileNotFoundException or ConfigFileException or ConfigValidationException) { return null; }
        if (task.Endpoints.Target?.ConnectionName is not { Length: > 0 } connectionName) return null;
        string driver;
        try { driver = config.LoadConnection(connectionName).DriverType; }
        catch (FileNotFoundException) { return null; }
        var resolved = DbDataSync.Core.Config.TrackingResolution.Resolve(task.Tracking, driver);
        if (resolved.EffectiveMode == "state") { _last.TryRemove(replication, out _); return null; }

        var engine = driver.Equals(DriverIds.MsSql, StringComparison.OrdinalIgnoreCase) ? UnitStateEngine.SqlServer : UnitStateEngine.Postgres;
        var units = new TargetUnitState(engine);
        Task<System.Data.Common.DbConnection> Open(CancellationToken ct) => OpenAsync(connectionName, ct);

        string identity;
        try
        {
            await using var probe = await Open(cancellationToken).ConfigureAwait(false);
            identity = SyncUnitStream.Identity(engine, probe.DataSource, probe.Database, connectionName, replication);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the target is down: the identity is not known, so the last status keeps what it had and says the target did not answer
            var previous = _last.TryGetValue(replication, out var s) ? s : null;
            var down = new SyncCopyStatus(previous?.Identity ?? "", _time.GetUtcNow(), 0, false, $"The target '{connectionName}' did not answer: {ex.Message}", previous?.OpenDivergenceId, previous?.PositionProblem);
            _last[replication] = down;
            return down;
        }

        var follower = new SyncUnitFollower(state, units, Open, identity, replication, time: _time);
        var run = await follower.CatchUpAsync(Budget, cancellationToken).ConfigureAwait(false);
        string? problem = null;
        if (run.Error is null)
        {
            try
            {
                var findings = await follower.CheckPositionsAsync((mapping, key) => watermarks.GetWatermark(replication, mapping, key), cancellationToken).ConfigureAwait(false);
                problem = findings.FirstOrDefault(f => f.Reason is not null)?.Reason;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problem = null; logger.LogWarning(ex, "The position check of '{Replication}' failed.", replication); }
        }

        var open = follower.Follower.Divergence;
        var status = new SyncCopyStatus(identity, _time.GetUtcNow(), run.Copied, run.CaughtUp, run.Error, open?.Id, problem);
        _last[replication] = status;
        return status;
    }

    private async Task<System.Data.Common.DbConnection> OpenAsync(string connectionName, CancellationToken cancellationToken) =>
        (await connections.OpenAsync(connectionName, CredentialRole.Run, cancellationToken).ConfigureAwait(false)).Connection;
}
