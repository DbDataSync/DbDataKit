using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Lineage;
using DbDataKit.Quality;
using DbDataKit.State;
using DbDataSync.Core.Config;
using DbDataSync.Core.Lineage;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// The checks stored with Sync's replications, evaluated (plan 23a FQ10). A replication's <c>checks:</c> (FQ04) are about the tables it writes: <see cref="SourceFor"/> is Sync's <see cref="IQualitySource"/>, one dataset for
/// each target table a check names, under the lineage identity the table has on its connection (<see cref="SyncLineage.DefinitionOf"/>, the one the lineage graph uses), and <see cref="EvaluateAsync"/> runs the shared service over it and
/// records the results in the <c>quality</c> module of Sync's state database.
/// <para>
/// <b>What "last load" means here.</b> A freshness check with no column (the usual one for a replication) measures the age of the target's data, and Sync already has the figure that says how old it is: the replication's lag
/// (<see cref="ReplicationLagReading"/>, the reading Monitoring's <c>Lagging</c> finding is made of), which is measured against the source's own position and not against the clock, so a quiet, caught-up replication is as fresh as its
/// source and never goes stale by waiting. The data's last-load moment is therefore <c>now - lag</c> when there is a reading, so the check and the finding cannot disagree; with no reading (a reader that places no position in time,
/// a replication that has not been polled) it is the end of the replication's newest successful primary run. A check that names a column measures that column's newest value, as everywhere.
/// </para>
/// <para>
/// The connection is Sync's own connection to the target (<see cref="IConnectionFactory"/>: the credential Sync has, opened with the driver): Sync keeps one login for a connection, not a read login beside a write login, so the
/// evaluator's own statement policy is the guard (one SELECT per check, refused otherwise), not a read-only session.
/// </para>
/// </summary>
public sealed class ReplicationQuality(ConfigRepository config, IConnectionFactory connections, ReaderLagService lag, TaskRunStore runs, DbDataSync.State.StateDatabase state, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The names of the replications whose <c>task.yaml</c> has checks.</summary>
    public IReadOnlyList<string> ReplicationsWithChecks()
    {
        var names = new List<string>();
        foreach (var name in config.ListReplications())
        {
            try { if (config.LoadReplicationTask(name).Checks.Count > 0) names.Add(name); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* a task that does not load has no checks to run; the scheduler says why */ }
        }

        return names;
    }

    /// <summary>The age basis of the replication's data, as described on the class: now minus the lag when there is a reading, else the end of the newest successful primary run, else null.</summary>
    public DateTimeOffset? LastLoadedAt(ReplicationTaskConfig task, IReadOnlyList<string> mappings)
    {
        var now = _time.GetUtcNow();
        if (ReplicationLagReading.For(lag, config, task.Name, _time) is { } reading) return now - reading.Behind;
        DateTimeOffset? oldest = null;
        foreach (var mapping in mappings)
        {
            var last = runs.GetMappingRunHistory(task.Name, RunKind.Primary, mapping, 20).Where(r => r.Status == RunStatus.Succeeded && r.EndedAtUtc is not null).Select(r => r.EndedAtUtc!.Value).DefaultIfEmpty().Max();
            if (last == default) return null;          // a mapping that never succeeded: the replication has no last load
            if (oldest is null || last < oldest) oldest = last;
        }

        return oldest;
    }

    /// <summary>The datasets of one replication: its target tables that a check names (a check of a table that is no target of it is kept under the name it gives, on the replication's target connection).</summary>
    public IQualitySource SourceFor(string replication, List<string>? notes = null)
    {
        var task = config.LoadReplicationTask(replication);
        var mappings = config.ListTableMappings(replication).ToList();
        var targets = new List<(TableRef Ref, ConnectionConfig Connection, IReadOnlyList<string> Key)>();
        foreach (var mappingName in mappings)
        {
            var mapping = config.LoadTableMapping(replication, mappingName);
            // the columns that identify a row of the target: the mapping's primary key, by the target's names of its columns
            IReadOnlyList<string> key = [.. mapping.PrimaryKey.Select(c => mapping.ColumnMappings.FirstOrDefault(m => string.Equals(m.SourceColumn, c, StringComparison.OrdinalIgnoreCase))?.TargetColumn ?? c)];
            foreach (var spec in mapping.Targets)
            {
                try
                {
                    var target = EndpointResolution.ResolveTarget(task, spec);
                    targets.Add((target, config.LoadConnection(target.ConnectionName), key));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { notes?.Add($"{replication}: a target table could not be resolved ({ex.GetType().Name})."); }
            }
        }

        var fallback = task.Endpoints.Target?.ConnectionName is { } name ? TryConnection(name) : null;
        var loadedAt = LastLoadedAt(task, mappings);
        var datasets = new List<QualityDataset>();

        // the name each check may use for a table, and what it resolves to
        var byName = new Dictionary<string, (TableRef Ref, ConnectionConfig Connection, string Identity, IReadOnlyList<string> Key)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, connection, key) in targets)
        {
            var identity = LineageIdentity.Dataset(SyncLineage.DefinitionOf(connection, target.Database), target.Schema, target.Table).ToString();
            var entry = (target, connection, identity, key);
            byName.TryAdd(identity, entry);
            byName.TryAdd($"{target.Schema}.{target.Table}", entry);
            byName.TryAdd(target.Table, entry);
        }

        foreach (var group in task.Checks.GroupBy(c => c.Attachment.Dataset, StringComparer.OrdinalIgnoreCase))
        {
            ConnectionConfig? connection; string identity; TableRef? table; IReadOnlyList<string> key = [];
            if (byName.TryGetValue(group.Key, out var known)) (connection, identity, table, key) = (known.Connection, known.Identity, known.Ref, known.Key);
            else { connection = fallback; table = null; identity = group.Key; }
            if (connection is null) { notes?.Add($"{replication}: the checks of `{group.Key}` were not evaluated: the replication has no target connection."); continue; }
            var engine = SyncLineage.DefinitionOf(connection, table?.Database).Engine;
            if (engine is not ("sqlserver" or "postgres"))
            {
                notes?.Add($"{replication}: the checks of `{group.Key}` were not evaluated: the target is {engine}, and checks run on SQL Server and PostgreSQL.");
                continue;
            }

            var dialect = DialectCatalog.Builtin.Get(engine);
            var database = table?.Database;
            var checks = group.Select(c => c with { Attachment = c.Attachment with { Dataset = identity } }).ToList();
            IReadOnlyList<string>? Resolve(string dataset) =>
                byName.TryGetValue(dataset, out var t) ? (string.IsNullOrEmpty(t.Ref.Schema) ? [t.Ref.Table] : [t.Ref.Schema, t.Ref.Table])
                : dataset.Contains("://", StringComparison.Ordinal) ? null : dialect.SplitQualifiedName(dataset);
            var connectionName = connection.Name;
            Func<CancellationToken, Task<DbConnection>> open = async ct =>
            {
                var (opened, _) = await connections.OpenAsync(connectionName, ct).ConfigureAwait(false);
                try
                {
                    // Sync's connection is opened on the connection's own database; an endpoint that names another one is switched to (the engines that cannot switch say so, which is an error result)
                    if (!string.IsNullOrEmpty(database) && !string.Equals(opened.Database, database, StringComparison.OrdinalIgnoreCase)) await opened.ChangeDatabaseAsync(database, ct).ConfigureAwait(false);
                    return opened;
                }
                catch { await opened.DisposeAsync().ConfigureAwait(false); throw; }
            };

            var aliases = new List<string> { group.Key };
            if (table is not null) aliases.Add($"{table.Schema}.{table.Table}");
            datasets.Add(new QualityDataset(identity, checks, dialect, open, Resolve, Aliases: aliases, KeyColumns: key, LastLoadedAt: loadedAt));
        }

        return new StaticQualitySource(datasets);

        ConnectionConfig? TryConnection(string connection)
        {
            try { return config.LoadConnection(connection); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }
    }

    /// <summary>Evaluates the checks of <paramref name="replication"/> and records the results; <paramref name="runId"/> is the run that set it off (null for an evaluation on the clock).</summary>
    public async Task<(QualityRunReport Report, IReadOnlyList<string> Notes)> EvaluateAsync(string replication, string? runId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var source = SourceFor(replication, notes);
        var report = await new QualityRunService(source, new QualityStore(state), time: _time).RunAsync(null, runId, ct).ConfigureAwait(false);
        return (report, notes);
    }
}

/// <summary>
/// Runs <see cref="ReplicationQuality"/> for the replications that have checks: when a run of the replication has ended (after-run, with that run's id), and on a clock (<see cref="IdleInterval"/>) for the ticks in which nothing
/// ran, because the polling gate skips a replication whose source has not moved and a freshness check still has to answer, from the lag reading, that the data is as fresh as the source. One evaluation at a time.
/// </summary>
public sealed class ReplicationChecksService(ReplicationQuality quality, TaskRunStore runs, ILogger<ReplicationChecksService> logger, TimeProvider? time = null) : BackgroundService
{
    /// <summary>How often a replication's checks are evaluated when no run of it has ended: a minute.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _lastEvaluated = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _seen = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await TickAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>One tick: evaluates every replication with checks that is due. Public for the tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        IReadOnlyList<string> names;
        try { names = quality.ReplicationsWithChecks(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The replications with checks could not be listed."); return; }
        if (names.Count == 0) return;

        var ended = runs.GetRecentlyEndedRuns(now - RecentWindow).Where(r => r.RunKind == RunKind.Primary && !_seen.Contains(r.RunId)).OrderBy(r => r.EndedAtUtc).ToList();
        foreach (var name in names)
        {
            var own = ended.LastOrDefault(r => string.Equals(r.TaskName, name, StringComparison.Ordinal));
            foreach (var run in ended.Where(r => string.Equals(r.TaskName, name, StringComparison.Ordinal))) _seen.Add(run.RunId);
            if (own is null)
            {
                // a replication seen for the first time is not evaluated on the clock at once (a restart does not send every replication's checks to its target together); its next time is an interval away
                if (!_lastEvaluated.TryGetValue(name, out var last)) { _lastEvaluated[name] = now; if (IdleInterval > TimeSpan.Zero) continue; }
                else if (now - last < IdleInterval) continue;
            }

            _lastEvaluated[name] = now;
            try
            {
                var (report, _) = await quality.EvaluateAsync(name, own?.RunId.ToString(), ct).ConfigureAwait(false);
                if (!report.AllPassed) logger.LogInformation("Checks of '{Replication}': {Failed} failed, {Errors} could not run.", name, report.Failed, report.Errors);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "The checks of '{Replication}' could not be evaluated.", name); }
        }

        if (_seen.Count > 10_000) _seen.Clear();
    }
}
