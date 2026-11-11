using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// What an administrator chose for retention (plan 29 section 3.3), in days and fractions of days (<c>0.25</c> is six hours): a figure for every table that has a default
/// (<see cref="Days"/>), one per module (<see cref="Modules"/>) and one per table (<see cref="Tables"/>, keyed <c>module.table</c>); the narrowest wins. A rule with no default age
/// (the audit) is untouched by <see cref="Days"/> and runs only when its module or table is named. <see cref="Forever"/> keeps a table's rows.
/// </summary>
public sealed class RetentionSettings
{
    public static RetentionSettings Defaults { get; } = new();

    public static double Forever => double.PositiveInfinity;

    public double? Days { get; init; }

    public IReadOnlyDictionary<string, double> Modules { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, double> Tables { get; init; } = new Dictionary<string, double>();

    /// <summary>How old an owned payload has to be before it is removed for having no owner: a writer stores the payload, then the row that refers to it. One hour by default.</summary>
    public TimeSpan OrphanGrace { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>What one run removed, by <c>module.table</c> (and <c>payloads.orphans</c>).</summary>
public sealed record RetentionResult(IReadOnlyDictionary<string, long> Removed);

/// <summary>
/// The one shared job that removes history (plan 29 section 3.3): each module's <see cref="RetentionRule"/>s, measured on the database's clock, and the payloads whose owner row is
/// gone. Tables that hold current state have no rule and are never touched. The job knows only the modules of the database it is given, in the scope of the service running it.
/// </summary>
public sealed class RetentionJob
{
    private readonly StateDatabase _database;
    private readonly RetentionSettings _settings;

    public RetentionJob(StateDatabase database, RetentionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _settings = settings ?? RetentionSettings.Defaults;
        var layout = database.Layout ?? throw new InvalidOperationException("Retention runs on a database built from modules.");

        if (_settings.Days is { } days) CheckDays(days, "retention.days");
        foreach (var (module, value) in _settings.Modules)
        {
            if (!layout.Has(module)) throw new InvalidOperationException($"retention.modules names the module '{module}', which this database does not use.");
            CheckDays(value, $"retention.modules.{module}");
        }

        foreach (var (key, value) in _settings.Tables)
        {
            var parts = key.Split('.');
            if (parts.Length != 2 || !layout.Has(parts[0]) || !layout.Namespace(parts[0]).Module.Retention.Any(r => r.Table == parts[1]))
                throw new InvalidOperationException($"retention.tables names '{key}', which is not a table with a retention rule (write module.table).");
            CheckDays(value, $"retention.tables.{key}");
        }
    }

    private static void CheckDays(double days, string setting)
    {
        if (double.IsNaN(days) || days <= 0)
            throw new InvalidOperationException($"{setting} is {days.ToString(CultureInfo.InvariantCulture)}; it is a number of days above zero (0.25 is six hours), or the word forever.");
    }

    /// <summary>The age a rule applies, or null for none (kept).</summary>
    private double? DaysFor(string module, RetentionRule rule)
    {
        if (_settings.Tables.TryGetValue($"{module}.{rule.Table}", out var table)) return table;
        if (_settings.Modules.TryGetValue(module, out var forModule)) return forModule;
        if (rule.DefaultDays is null) return null;
        return _settings.Days ?? rule.DefaultDays;
    }

    public RetentionResult Run()
    {
        var layout = _database.Layout!;
        var now = _database.Time.GetUtcNow();
        var removed = new Dictionary<string, long>();

        foreach (var ns in layout.Namespaces)
        {
            foreach (var rule in ns.Module.Retention)
            {
                if (DaysFor(ns.Module.Name, rule) is not { } days || double.IsPositiveInfinity(days)) continue;
                removed[$"{ns.Module.Name}.{rule.Table}"] = RemoveOlderThan(ns.Module.Name, rule, now - TimeSpan.FromDays(days));
            }
        }

        if (layout.Has(PayloadStore.ModuleName))
        {
            long orphans = 0;
            foreach (var ns in layout.Namespaces)
            {
                foreach (var owner in ns.Module.PayloadOwners)
                    orphans += RemoveOrphans(ns.Module.Name, owner, now - _settings.OrphanGrace);
            }

            if (orphans > 0) removed[$"{PayloadStore.ModuleName}.orphans"] = orphans;
        }

        return new RetentionResult(removed);
    }

    /// <summary>Runs every <paramref name="every"/> until cancelled; a failed run is reported through <paramref name="onError"/> and the next one still happens.</summary>
    public async Task RunEveryAsync(TimeSpan every, CancellationToken cancellationToken, Action<Exception>? onError = null)
    {
        using var timer = new PeriodicTimer(every);
        do
        {
            try { Run(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { onError?.Invoke(ex); }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private long RemoveOlderThan(string module, RetentionRule rule, DateTimeOffset cutoff)
    {
        var where = $"{rule.TimeColumn} < $cutoff{(rule.Where is null ? "" : $" AND ({rule.Where})")}";
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var child in rule.Children ?? [])
        {
            using var children = _database.Command(module, connection, transaction,
                $"DELETE FROM {{table:{child.Table}}} WHERE {child.Column} IN (SELECT {rule.KeyColumn} FROM {{table:{rule.Table}}} WHERE {where});");
            children.Bind(_database, "cutoff", cutoff);
            children.ExecuteNonQuery();
        }

        using var parents = _database.Command(module, connection, transaction, $"DELETE FROM {{table:{rule.Table}}} WHERE {where};");
        parents.Bind(_database, "cutoff", cutoff);
        var count = parents.ExecuteNonQuery();
        transaction.Commit();
        return count;
    }

    private long RemoveOrphans(string module, PayloadOwner owner, DateTimeOffset createdBefore)
    {
        var ownerTable = _database.Layout!.Table(module, owner.Table);
        var key = owner.KeyIsUuid ? _database.Dialect.UuidAsText(owner.KeyColumn) : owner.KeyColumn;
        var orphaned = $"owner_module = $module AND created_at < $before AND owner_row IS NOT NULL AND owner_row NOT IN (SELECT {key} FROM {ownerTable})";

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var chunks = _database.Command(PayloadStore.ModuleName, connection, transaction, $"DELETE FROM {{table:chunk}} WHERE payload_id IN (SELECT id FROM {{table:payload}} WHERE {orphaned});"))
        {
            chunks.Bind(_database, "module", module).Bind(_database, "before", createdBefore);
            chunks.ExecuteNonQuery();
        }

        using var payloads = _database.Command(PayloadStore.ModuleName, connection, transaction, $"DELETE FROM {{table:payload}} WHERE {orphaned};");
        payloads.Bind(_database, "module", module).Bind(_database, "before", createdBefore);
        var count = payloads.ExecuteNonQuery();
        transaction.Commit();
        return count;
    }
}
