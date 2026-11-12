using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Config.Yaml;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using Microsoft.Extensions.Hosting;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The result cache of this instance (plan 24a UC12 to UC14, UC16): the in-memory <see cref="IServingCache"/>, the policy that reads the published definitions' <c>cache</c> sections, and the invalidator that clears it when data changes. The cache is
/// off for every definition that does not say <c>enabled: true</c>; nothing else turns it on.
/// </summary>
public sealed class GatewayCache
{
    public GatewayCache(DataSourceCatalog sources, Func<GatewayOptions> options, Func<SemanticModels> models, IServingCacheEpochs? epochs = null, TimeProvider? time = null, MemoryServingCacheOptions? sizes = null)
    {
        Store = new MemoryServingCache(sizes, time);
        Policy = new GatewayCachePolicy(sources, options, models);
        Invalidator = new ServingCacheInvalidator(Store, dataset => Resolve?.Invoke(dataset), epochs, time);
    }

    public MemoryServingCache Store { get; }

    public GatewayCachePolicy Policy { get; }

    public ServingCacheInvalidator Invalidator { get; }

    /// <summary>Maps the dataset a producer names to the cache datasets that read it (the lineage graph does, see <c>GatewayCacheManagement</c>); until the host sets it, or when it returns null, a change clears the whole cache.</summary>
    public Func<string, IReadOnlyList<string>?>? Resolve { get; set; }
}

/// <summary>
/// Reads a definition's <c>cache</c> section into a plan for one request (24a UC03 section into UC12 plan). A request is cached only when all of these hold: the definition says <c>enabled: true</c>; the request is a read the cache can key
/// (<see cref="ServingCacheable"/>); it is no preview; every data source it reads answers with the Gateway's own login (a pass-through source answers with the caller's database login and is never shared, a writable one can change under a cache
/// that only hears of changes by message) and the <em>caller may use each of them</em>, so an entry another caller made can never be a way round a grant (the inner service would have refused this caller, and the cache asks the same question first).
/// </summary>
public sealed class GatewayCachePolicy(DataSourceCatalog sources, Func<GatewayOptions> options, Func<SemanticModels> models) : IServingCachePolicy, IServingCacheSettings
{
    public const string DataSourcePrefix = "data-source/";
    public const string ModelPrefix = "semantic-model/";

    public ServingCachePlan? PlanFor(ServingContext context, ServingRequest request)
    {
        if (context.Preview is not null || !ServingCacheable.IsRead(request) || context.Caller is not GatewayCaller caller || string.IsNullOrWhiteSpace(request.Source)) return null;
        var principal = caller.Principal;
        if (SemanticModels.IsModelSource(request.Source))
        {
            if (models().Find(request.Source) is not { } model) return null;
            var setting = Describe(model);
            if (!setting.Enabled || setting.Note is not null) return null;
            foreach (var used in UsedBy(model))
                if (!Usable(principal, used)) return null;
            // the caller's column rules are part of the key (plan 22a GP13): a caller who sees a column masked never shares an entry with one who sees it as it is
            return new ServingCachePlan(setting.Ttl, setting.Scope, setting.Version, setting.Datasets, SemanticColumnAccess.Fingerprint(model, caller));
        }

        var name = request.Source.Trim();
        if (!options().DataSources.TryGetValue(name, out var source)) return null;
        var own = Describe(name, source);
        if (!own.Enabled || own.Note is not null || !sources.MayUse(principal, name)) return null;
        return new ServingCachePlan(own.Ttl, own.Scope, own.Version, own.Datasets);
    }

    private bool Usable(Auth.GatewayPrincipal principal, string dataSource) =>
        options().DataSources.TryGetValue(dataSource, out var source) && !source.PassThrough && !source.AllowWrites && sources.MayUse(principal, dataSource);

    /// <summary>The serving data sources a model reads, by name (a source that is not one of this Gateway's is named all the same, and the caller may not use it).</summary>
    private static IEnumerable<string> UsedBy(SemanticModel model) =>
        (model.Catalog.Source["tables"] is System.Text.Json.Nodes.JsonArray tables ? tables.OfType<System.Text.Json.Nodes.JsonObject>() : [])
            .Select(table => table["x_source"]?.GetValue<string>()).OfType<string>().Select(model.ServingSource).Distinct(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ServingCacheSetting> Settings()
    {
        var current = options();
        var list = new List<ServingCacheSetting>();
        foreach (var (name, source) in current.DataSources.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase)) list.Add(Describe(name, source));
        foreach (var model in models().All.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) list.Add(Describe(model));
        return list;
    }

    private ServingCacheSetting Describe(string name, DataSourceOptions source)
    {
        var cache = source.Cache;
        var ttl = CacheSection.DefaultTtl;
        var scope = ServingCacheScope.Caller;
        if (cache is not null)
        {
            if (CacheSection.TryParseTtl(cache.Ttl, out var parsed, out _)) ttl = parsed;
            scope = ScopeOf(cache.Scope);
        }

        string? note = null;
        if (cache is { Enabled: true })
        {
            if (source.PassThrough) note = "A pass-through data source answers with the caller's own database login, so its results are never shared and never cached.";
            else if (source.AllowWrites) note = "A data source that accepts writes is not cached: a write changes it without a message.";
        }

        var version = Fingerprint(source);
        return new ServingCacheSetting(name, "data-source", cache is { Enabled: true }, ttl, scope, version, [DataSourcePrefix + name], note);
    }

    private ServingCacheSetting Describe(SemanticModel model)
    {
        var cache = model.Catalog.Cache;
        var datasets = new List<string> { ModelPrefix + model.Name };
        datasets.AddRange(UsedBy(model).Select(used => DataSourcePrefix + used));
        string? note = null;
        if (cache is { Enabled: true } && model.Ssas is { Count: > 0 }) note = "A model that reads an Analysis Services source is not cached.";
        else if (cache is { Enabled: true } && UsedBy(model).Any(used => options().DataSources.TryGetValue(used, out var source) && (source.PassThrough || source.AllowWrites)))
            note = "The model reads a data source that is pass-through or accepts writes; such a data source is never cached, so neither is the model.";
        return new ServingCacheSetting(SemanticModels.SourcePrefix + model.Name, "semantic-model", cache is { Enabled: true }, cache?.Ttl ?? CacheSection.DefaultTtl, cache is null ? ServingCacheScope.Caller : ScopeOf(CacheSection.Name(cache.Scope)),
            model.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), datasets, note);
    }

    private static ServingCacheScope ScopeOf(string name) => name switch { "rules" => ServingCacheScope.Rules, "shared" => ServingCacheScope.Shared, _ => ServingCacheScope.Caller };

    /// <summary>What the key holds of a data source's own settings, so a changed connection, login or serving policy is a new key (a hand-made file has no version number): a hash, never shown.</summary>
    private static string Fingerprint(DataSourceOptions source) =>
        Fingerprints.GetValue(source, s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s))))[..12]);

    // the options object of a source is replaced when the configuration is read again, so the hash is made once per object
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataSourceOptions, string> Fingerprints = new();
}

/// <summary>Reads the group's shared epochs every few seconds and clears what the other instances cleared (24a UC14). A second between calls of a busy instance is the most a stale entry can outlive a change that reached another instance.</summary>
public sealed class ServingCacheSyncService(GatewayCache cache, TimeSpan? every = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cache.Invalidator.Shared) return;
        using var timer = new PeriodicTimer(every ?? TimeSpan.FromSeconds(2));
        try
        {
            cache.Invalidator.Poll();   // learns the epochs of now
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) cache.Invalidator.Poll();
        }
        catch (OperationCanceledException) { /* stopping */ }
    }
}
