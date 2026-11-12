using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Serving;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The result cache over the management API (plan 24a UC13, UC16):
/// <list type="bullet">
/// <item><c>GET /api/v1/cache</c> (viewer): this instance's counts (hits, misses, evictions, entries, bytes), the same by dataset, the cache setting of every data source and semantic model the Gateway serves (off unless the definition says otherwise, and why
/// an enabled one does nothing), and what the cache has heard of data changes (how many, the last, from which product, and whether the group shares them), so "events not arriving" can be seen.</item>
/// <item><c>POST /api/v1/cache/changed</c> (publisher or admin): the <c>data.changed/1</c> message of a Build, Sync, DbDataStore or Runner server that finished writing a dataset. The call is a signed management call, so a forged or replayed message is
/// refused by the channel; the body names a dataset and carries no row data. The answer says which cache datasets were cleared and how many entries went.</item>
/// <item><c>POST /api/v1/cache/invalidate</c> (admin): <c>{"dataset": "data-source/erp"}</c> clears one dataset, <c>{"all": true}</c> everything; audited.</item>
/// </list>
/// A dataset the Gateway cannot map to what it serves (the lineage graph does not know it) clears the whole cache: a cold cache is never wrong, a stale one can be.
/// </summary>
public sealed class GatewayCacheManagement(GatewayCache cache, Func<LineageGraph?> graph, string instanceId, IAuditLog? audit = null, ServingCacheEpochStore? epochs = null, TimeProvider? time = null, GatewayAccess? access = null)
{
    public const string Route = GatewayManagement.Prefix + "/cache";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static bool Handles(string route) => route == Route || route.StartsWith(Route + "/", StringComparison.Ordinal);

    /// <summary>The cache datasets that read a dataset a producer names, by the lineage graph; null when the graph does not know the dataset (the caller then clears everything).</summary>
    public IReadOnlyList<string>? Resolve(string dataset)
    {
        LineageGraph? built;
        // the models whose access rules read the table: a rules table is read by no dimension or measure, so the graph does not reach them from it (plan 22a GP11)
        var rules = access?.DependentsOf(dataset) ?? [];
        try { built = graph(); }
        catch (LineageGraphException) { return null; }
        if (built?.Node(dataset) is not { } start) return rules.Count > 0 ? rules : null;
        var reached = built.Impact(dataset).Reached.Select(r => r.Node).Prepend(start).Where(n => n.Kind == NodeKind.Serving);
        return [.. reached.Select(n => CacheNameOf(n.Id)).Where(n => n is not null).Select(n => n!).Concat(rules).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary><c>suite://&lt;instance&gt;/data-source/&lt;name&gt;</c> is the cache dataset <c>data-source/&lt;name&gt;</c> (and the same for a semantic model); any other node is not one the cache keys entries by.</summary>
    public static string? CacheNameOf(string nodeId)
    {
        const string prefix = "suite://";
        if (!nodeId.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = nodeId[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var name = rest[(slash + 1)..];
        return name.StartsWith(GatewayCachePolicy.DataSourcePrefix, StringComparison.Ordinal) || name.StartsWith(GatewayCachePolicy.ModelPrefix, StringComparison.Ordinal) ? name : null;
    }

    public ManagementResult Handle(string method, string route, string? body, ManagementCaller caller)
    {
        switch ((method, route))
        {
            case ("GET", Route):
                return caller.Holds(ServerRole.Viewer) ? ManagementResult.Ok(Describe()) : Denied("viewer");
            case ("POST", Route + "/changed"):
                return caller.Holds(ServerRole.Publisher) || caller.Holds(ServerRole.Admin) ? Changed(body, caller) : Denied("publisher");
            case ("POST", Route + "/invalidate"):
                return caller.Holds(ServerRole.Admin) ? Invalidate(body, caller) : Denied("admin");
            case (_, Route or Route + "/changed" or Route + "/invalidate"):
                return ManagementResult.Problem(405, "This call is not available with that method.");
            default:
                return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    private static ManagementResult Denied(string role) => ManagementResult.Problem(403, $"This call needs the {role} role.");

    private ManagementResult Changed(string? body, ManagementCaller caller)
    {
        if (!DataChangedMessage.TryRead(body, _time.GetUtcNow(), out var notice, out var problem)) return ManagementResult.Problem(400, problem);
        access?.Changed(notice);   // the rules table's age starts again before the entries that used it go
        var outcome = cache.Invalidator.Apply(notice);
        audit?.Append(caller.Audit("cache changed", notice.Dataset, outcome.Ignored ? "not served here, nothing cleared" : outcome.Everything ? $"everything cleared ({outcome.Removed} entries)" : $"{outcome.Removed} entries cleared", _time.GetUtcNow()));
        return ManagementResult.Ok(new JsonObject
        {
            ["message"] = DataChangedMessage.Format,
            ["dataset"] = notice.Dataset,
            ["cleared"] = outcome.Removed,
            ["everything"] = outcome.Everything,
            ["served"] = !outcome.Ignored,
            ["datasets"] = outcome.Datasets is null ? null : new JsonArray([.. outcome.Datasets.Select(d => (JsonNode)JsonValue.Create(d)!)]),
        });
    }

    private ManagementResult Invalidate(string? body, ManagementCaller caller)
    {
        JsonObject? json;
        try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { json = null; }
        var all = json?["all"]?.GetValue<bool>() == true;
        var dataset = json?["dataset"] is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : null;
        if (all == (dataset is { Length: > 0 })) return ManagementResult.Problem(400, "Say which dataset to clear (`dataset`), or `all: true`; not both, not neither.");
        var who = caller.Token?.SubjectName ?? "local";
        var removed = cache.Invalidator.ClearByHand(all ? null : dataset, who);
        audit?.Append(caller.Audit("cache invalidate", all ? "*" : dataset, $"{removed} entries cleared", _time.GetUtcNow()));
        return ManagementResult.Ok(new JsonObject { ["cleared"] = removed, ["dataset"] = all ? null : dataset, ["all"] = all });
    }

    private JsonObject Describe()
    {
        var stats = cache.Store.Stats;
        var activity = cache.Invalidator.Activity;
        var lookups = stats.Hits + stats.Misses;
        string? Time(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);
        var settings = cache.Policy.Settings();
        var perDataset = cache.Store.DatasetStats.ToDictionary(s => s.Dataset, StringComparer.OrdinalIgnoreCase);
        var recent = new JsonArray();
        try
        {
            foreach (var row in epochs?.Recent(20) ?? [])
                recent.Add(new JsonObject { ["dataset"] = row.Dataset, ["at"] = Time(row.At), ["product"] = row.Product, ["reason"] = row.Reason });
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or IOException) { /* the shared record is down: the list is empty, the instance still serves */ }
        return new JsonObject
        {
            ["instance"] = instanceId,
            ["enabled"] = settings.Any(s => s.Enabled && s.Note is null),
            ["max_bytes"] = cache.Store.Options.MaxBytes,
            ["max_entry_bytes"] = cache.Store.Options.MaxEntryBytes,
            ["stats"] = new JsonObject
            {
                ["hits"] = stats.Hits, ["misses"] = stats.Misses, ["entries"] = stats.Entries, ["bytes"] = stats.Bytes, ["evictions"] = stats.Evictions, ["cleared"] = stats.Cleared,
                ["hit_rate"] = lookups == 0 ? null : Math.Round((double)stats.Hits / lookups, 4),
            },
            ["datasets"] = new JsonArray([.. cache.Store.DatasetStats.Select(d => (JsonNode)new JsonObject
            {
                ["name"] = d.Dataset, ["entries"] = d.Entries, ["bytes"] = d.Bytes, ["hits"] = d.Hits, ["misses"] = d.Misses,
                ["hit_rate"] = d.Hits + d.Misses == 0 ? null : Math.Round((double)d.Hits / (d.Hits + d.Misses), 4),
            })]),
            ["settings"] = new JsonArray([.. settings.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["kind"] = s.Kind, ["enabled"] = s.Enabled, ["active"] = s.Enabled && s.Note is null, ["ttl_seconds"] = (long)s.Ttl.TotalSeconds, ["scope"] = s.Scope.ToString().ToLowerInvariant(),
                ["version"] = s.Version, ["datasets"] = new JsonArray([.. s.Datasets.Select(d => (JsonNode)JsonValue.Create(d)!)]), ["note"] = s.Note,
                ["entries"] = s.Datasets.Count > 0 && perDataset.TryGetValue(s.Datasets[0], out var own) ? own.Entries : 0,
            })]),
            ["events"] = new JsonObject
            {
                ["received"] = activity.Received, ["ignored"] = activity.Ignored, ["cleared"] = activity.Cleared, ["last_at"] = Time(activity.LastAt), ["last_product"] = activity.LastProduct, ["last_dataset"] = activity.LastDataset,
                ["manual"] = activity.Manual, ["last_manual_at"] = Time(activity.LastManualAt), ["shared"] = cache.Invalidator.Shared, ["recent"] = recent,
            },
        };
    }
}
