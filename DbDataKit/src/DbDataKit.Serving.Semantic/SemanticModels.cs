using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Serving.Semantic;

/// <summary>One model the host serves: a catalog, and for each of its sources the serving source (a data source of the host) that holds its tables.</summary>
public sealed record SemanticModel(Catalog Catalog, IReadOnlyDictionary<string, string> Sources)
{
    public string Name => Catalog.Name;

    /// <summary>The serving source a catalog source's queries run against; a source the map does not name is the serving source of the same name.</summary>
    public string ServingSource(string catalogSource) => Sources.TryGetValue(catalogSource, out var serving) ? serving : catalogSource;
}

/// <summary>The models a host serves, by the name of their catalogs.</summary>
public sealed class SemanticModels
{
    /// <summary>What a request's source starts with when it names a model: <c>semantic:sales</c>.</summary>
    public const string SourcePrefix = "semantic:";

    private readonly Dictionary<string, SemanticModel> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The engine level the host runs at (plan 14's adoption levels); what a level cannot execute comes back as remaining steps.</summary>
    public int Level { get; init; } = 5;

    /// <summary>
    /// Makes the executor for a source of kind <c>ssas</c> (model name, catalog source, and the executor for everything that is not DAX); null when the host does not run Analysis Services.
    /// The host owns this because the client library runs on Windows only, and because the connection (the service account's, not the caller's) is the host's to configure.
    /// </summary>
    public Func<string, string, ISourceExecutor, ISourceExecutor>? SsasExecutor { get; init; }

    public IReadOnlyCollection<SemanticModel> All => _models.Values;

    public SemanticModels Add(Catalog catalog, IReadOnlyDictionary<string, string>? sources = null)
    {
        var model = new SemanticModel(catalog, sources ?? new Dictionary<string, string>());
        if (!_models.TryAdd(model.Name, model)) throw new InvalidOperationException($"Two semantic models are named '{model.Name}'.");
        return this;
    }

    /// <summary>The model a source names (<c>semantic:sales</c>, or just <c>sales</c> for a request kind that is always semantic); null when it names none.</summary>
    public SemanticModel? Find(string source) =>
        _models.TryGetValue(source.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase) ? source[SourcePrefix.Length..] : source, out var model) ? model : null;

    public static bool IsModelSource(string source) => source.StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase);
}
