using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// One model the host serves: a catalog, for each of its sources the serving source (a data source of the host) that holds its tables, and for a source on Analysis Services the connection string (or a reference to it) the host connects with.
/// </summary>
public sealed record SemanticModel(Catalog Catalog, IReadOnlyDictionary<string, string> Sources, IReadOnlyDictionary<string, string>? Ssas = null)
{
    public string Name => Catalog.Name;

    /// <summary>The published version this model is (plan 14a SL23), so a run record names it and a replay can load it again; null for a model the host was configured with.</summary>
    public int? Version { get; init; }

    /// <summary>
    /// The serving source a catalog source's queries run against; a source the map does not name is the serving source of the same name. The engine names a source with several fact tables
    /// <c>source.fact</c> for all but the first, and those queries run where the source does.
    /// </summary>
    public string ServingSource(string catalogSource) => Sources.TryGetValue(BaseSource(catalogSource), out var serving) ? serving : BaseSource(catalogSource);

    /// <summary>The source of the catalog an engine source id belongs to (<c>sqlserver_sales.fact_returns</c> is of <c>sqlserver_sales</c>).</summary>
    public static string BaseSource(string sourceId) => sourceId.Contains('.') ? sourceId[..sourceId.IndexOf('.')] : sourceId;
}

/// <summary>The models a host serves, by the name of their catalogs. Models can be added, replaced and removed while the host runs: a request reads the model it found, so a replacement never changes a request already running.</summary>
public sealed class SemanticModels
{
    /// <summary>What a request's source starts with when it names a model: <c>semantic:sales</c>.</summary>
    public const string SourcePrefix = "semantic:";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemanticModel> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine level the host runs at (plan 14's adoption levels); what a level cannot execute comes back as remaining steps.</summary>
    public int Level { get; init; } = 5;

    /// <summary>
    /// Makes the executor for a source of kind <c>ssas</c> (the model, the catalog source, and the executor for everything that is not DAX); null when the host does not run Analysis Services.
    /// The host owns this because the client library runs on Windows only, and because the connection (the service account's, not the caller's) is the host's to configure.
    /// </summary>
    public Func<SemanticModel, string, ISourceExecutor, ISourceExecutor>? SsasExecutor { get; init; }

    /// <summary>The key of the keyed hash that masks a column with <c>mask: hash</c> (plan 22a GP13): the install's own secret, read from the host's secrets store. Null, or an empty key, uses a key of this process only.</summary>
    public Func<byte[]>? MaskKey { get; set; }

    /// <summary>Resolves the row rules of the caller for a model that has them (plan 22a GP11); null when the host cannot, and such a model is then refused.</summary>
    public ISemanticAccess? Access { get; set; }

    /// <summary>Where people keep saved queries of their own (J27); null when the host keeps none.</summary>
    public IUserSavedQueries? UserQueries { get; set; }

    /// <summary>Where a record of every statement is kept (plan 14a SL23, D21); null when the host keeps none.</summary>
    public ISemanticRuns? Runs { get; set; }

    public IReadOnlyCollection<SemanticModel> All => _models.Values.ToList();

    public SemanticModels Add(Catalog catalog, IReadOnlyDictionary<string, string>? sources = null, IReadOnlyDictionary<string, string>? ssas = null)
    {
        var model = new SemanticModel(catalog, sources ?? new Dictionary<string, string>(), ssas);
        if (!_models.TryAdd(model.Name, model)) throw new InvalidOperationException($"Two semantic models are named '{model.Name}'.");
        return this;
    }

    /// <summary>Serves this model from now on in place of the one of the same name, if there is one.</summary>
    public void Set(SemanticModel model) => _models[model.Name] = model;

    public bool Remove(string name) => _models.TryRemove(name, out _);

    /// <summary>The model a source names (<c>semantic:sales</c>, or just <c>sales</c> for a request kind that is always semantic); null when it names none.</summary>
    public SemanticModel? Find(string source) =>
        _models.TryGetValue(source.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase) ? source[SourcePrefix.Length..] : source, out var model) ? model : null;

    public static bool IsModelSource(string source) => source.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a host gives the semantic module to apply the access rules of a model (plan 22a GP10, GP11): the plan of the caller's row rules, from the resolver.</summary>
public interface ISemanticAccess
{
    /// <summary>The caller's row rules on the model's catalog; an empty plan filters nothing. Never throws for a rule that cannot be resolved: the plan then refuses the table (D2).</summary>
    DbDataKit.Semantic.RowAccessPlan PlanFor(ServingContext context, SemanticModel model);
}
