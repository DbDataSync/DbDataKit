using System.Globalization;
using Apache.Arrow;
using DbDataKit.Access;
using DbDataKit.Lineage;
using DbDataKit.Serving;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using DbDataGateway.Data;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Management;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The Gateway's side of the access rules (plan 22a GP10, GP11; QG2): the resolver's ports (the names a rules table holds, read with the Gateway's own login once per generation of the table; the group's record of when a table last changed; the alert),
/// the plan of a caller's row rules for a semantic model (<see cref="ISemanticAccess"/>), the decision the result cache keys on, and the hook that a <c>data.changed</c> message for a rules table goes through. Nobody is exempt (QR3): the plan is built for every caller.
/// A data source that answers with the caller's own database login (pass-through) cannot hold rules: the Gateway has no login of its own to read the rules table with, so a model with rules on one is refused.
/// </summary>
public sealed class GatewayAccess(DataSourceCatalog sources, Func<GatewayOptions> options, Func<SemanticModels> models, IRulesTableLedger ledger, Func<string, string, string?> lineageIdentity, IAuditLog? audit = null,
    ILogger? log = null, TimeProvider? time = null, Func<IReadOnlyCollection<ServedEntityModel>>? entityModels = null) : ISemanticAccess
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly DataSourceCatalog _sources = sources;
    private readonly Func<GatewayOptions> _options = options;

    /// <summary>The resolver (one per instance: it keeps the names of each rules table per generation).</summary>
    public AccessResolver Resolver { get; } = new(ledger, new Alerts(audit, log, time), time);

    /// <summary>The caller as the rules see them: the id and the name, and the groups of the identity home.</summary>
    public static AccessCaller CallerOf(ServingCaller caller) => new(caller.Id, caller.Name, caller.Roles);

    /// <summary>Whether a model has row rules (a model with none is read as it always was).</summary>
    public static bool HasRowRules(SemanticModel model) => model.Catalog.Access is { Row.Count: > 0 };

    /// <summary>What applies to the caller on the model: the rules, the state of each rules table, the fingerprint.</summary>
    public AccessDecision DecisionFor(ServingCaller caller, SemanticModel model) =>
        model.Catalog.Access is { IsEmpty: false } section ? Resolver.Resolve(CallerOf(caller), new AccessScope(GatewayCachePolicy.ModelPrefix + model.Name, section, table => TableKey(model, table), new Subjects(this, model))) : AccessDecision.None;

    public RowAccessPlan PlanFor(ServingContext context, SemanticModel model) => RowAccessPlan.Build(model.Catalog, DecisionFor(context.Caller, model));

    /// <summary>The identity of a rules table in the state: the serving source and the physical table, as the model's catalog says them.</summary>
    internal static string TableKey(SemanticModel model, string tableName)
    {
        var table = TableOf(model, tableName);
        var physical = table?["source"]?.GetValue<string>() ?? tableName;
        var serving = model.ServingSource(table?["x_source"]?.GetValue<string>() ?? "");
        return serving + "/" + RowAccessPlan.Normalize(physical);
    }

    private static System.Text.Json.Nodes.JsonObject? TableOf(SemanticModel model, string name) =>
        (model.Catalog.Source["tables"] as System.Text.Json.Nodes.JsonArray)?.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(t => t["name"]?.GetValue<string>() == name);

    /// <summary>
    /// A <c>data.changed</c> message arrived: when it names the table a served model's rule reads, the table's refresh time moves (so its age starts again and every fingerprint of the model changes). Returns the cache datasets of the models that use it,
    /// to be cleared (the same message may name a table the lineage graph does not know: a rules table is read by no dimension or measure).
    /// </summary>
    public IReadOnlyList<string> Changed(DataChangedNotice notice)
    {
        var touched = new List<string>();
        foreach (var model in models().All)
        {
            if (model.Catalog.Access is not { Row.Count: > 0 } section) continue;
            var hit = false;
            foreach (var table in section.RulesTables().Keys)
            {
                var entry = TableOf(model, table);
                if (entry is null) continue;
                var serving = model.ServingSource(entry["x_source"]?.GetValue<string>() ?? "");
                var identity = lineageIdentity(serving, entry["source"]?.GetValue<string>() ?? table);
                if (identity is null || !string.Equals(identity, notice.Dataset, StringComparison.OrdinalIgnoreCase)) continue;
                var now = _time.GetUtcNow();
                ledger.Changed(TableKey(model, table), notice.At > now ? now : notice.At, notice.Product, "data.changed " + notice.Dataset);
                hit = true;
            }

            if (hit) touched.Add(GatewayCachePolicy.ModelPrefix + model.Name);
        }

        // the rules table of a Store model's entity (plan 22a GP11): its age starts again too, and the names it holds are read again; an entity read is never cached, so there is nothing to clear
        foreach (var served in entityModels?.Invoke() ?? [])
            foreach (var table in served.Model.Access?.RulesTables().Keys ?? (IEnumerable<string>)[])
                if (lineageIdentity(served.DataSource, table) is { } identity && string.Equals(identity, notice.Dataset, StringComparison.OrdinalIgnoreCase))
                    ledger.Changed(StoreEntityAccess.TableKey(served.DataSource, table), notice.At > _time.GetUtcNow() ? _time.GetUtcNow() : notice.At, notice.Product, "data.changed " + notice.Dataset);
        return touched;
    }

    /// <summary>The cache datasets of the models that have a rules table the message names, without moving anything (for the mapping of a message to what it clears).</summary>
    public IReadOnlyList<string> DependentsOf(string dataset)
    {
        var found = new List<string>();
        foreach (var model in models().All)
        {
            if (model.Catalog.Access is not { Row.Count: > 0 } section) continue;
            foreach (var table in section.RulesTables().Keys)
                if (TableOf(model, table) is { } entry && lineageIdentity(model.ServingSource(entry["x_source"]?.GetValue<string>() ?? ""), entry["source"]?.GetValue<string>() ?? table) is { } identity
                    && string.Equals(identity, dataset, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(GatewayCachePolicy.ModelPrefix + model.Name);
                    break;
                }
        }

        return found;
    }

    private sealed class Subjects(GatewayAccess owner, SemanticModel model) : IRulesSubjects
    {
        public IReadOnlyCollection<string> Names(string table, string column)
        {
            var rules = TableOf(model, table) ?? throw new InvalidOperationException($"The model has no table '{table}'.");
            var serving = model.ServingSource(rules["x_source"]?.GetValue<string>() ?? "");
            if (!owner._options().DataSources.TryGetValue(serving, out var source)) throw new InvalidOperationException($"The data source '{serving}' is not served here.");
            if (source.PassThrough) throw new InvalidOperationException($"The data source '{serving}' answers with each caller's own database login, so the Gateway cannot read a rules table on it");
            var physicalColumn = (rules["columns"] as System.Text.Json.Nodes.JsonArray)?.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(c => c["name"]?.GetValue<string>() == column) is { } found
                ? found["expression"]?["dialects"]?.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(d => d["dialect"]?.GetValue<string>() == "ANSI_SQL")?["expression"]?.GetValue<string>() ?? column
                : column;
            var resolved = owner._sources.Resolve(new GatewayPrincipal("access-rules", "service", [serving]), serving);
            var text = $"SELECT DISTINCT {physicalColumn} FROM {rules["source"]?.GetValue<string>() ?? table}";
            return Task.Run(async () =>
            {
                await using var result = await resolved.Executor.ExecuteQueryAsync(new QueryRequest(text, resolved.Options.BatchSize, resolved.Options.CommandTimeoutSeconds, null), CancellationToken.None).ConfigureAwait(false);
                var names = new HashSet<string>(StringComparer.Ordinal);
                await foreach (var batch in result.ReadBatchesAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var array = batch.Column(0);
                    for (var i = 0; i < batch.Length; i++)
                        if (!array.IsNull(i)) names.Add(array switch { StringArray s => s.GetString(i), LargeStringArray l => l.GetString(i), _ => throw new InvalidOperationException("The subject column of a rules table holds text.") });
                }

                return (IReadOnlyCollection<string>)names;
            }).GetAwaiter().GetResult();
        }
    }

    /// <summary>The alert of a rules table that passed half its age: an audit entry and a log line until the notification delivery is wired (plan 22a GP16).</summary>
    private sealed class Alerts(IAuditLog? audit, ILogger? log, TimeProvider? time) : IAccessAlerts
    {
        public void RulesTableStale(RulesTableStatus status)
        {
            var message = $"The rules table '{status.Table}' ({status.Key}) was last refreshed {status.Age.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes ago, past half of its maximum age of {status.MaxAge.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes: the last good rules are served and, past the maximum, the datasets they filter are refused.";
            log?.LogWarning("{Message}", message);
            try { audit?.Append(new AuditEntry((time ?? TimeProvider.System).GetUtcNow(), "local", "access rules", "access-rules", "access rules", false, "access rules stale", status.Key, message, "")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the log line stands */ }
        }
    }
}
