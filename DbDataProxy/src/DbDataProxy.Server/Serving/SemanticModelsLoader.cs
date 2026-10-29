using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.Semantic.Ssas;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;

namespace DbDataProxy.Server.Serving;

/// <summary>Reads the catalogs the <c>Semantic</c> section names, when the server starts: a catalog that does not load stops the start and names its file. The published models come after them, and one that does not load is reported and left out.</summary>
public static class SemanticModelsLoader
{
    public static SemanticModels Load(ProxyOptions options, string dataDirectory, SecretsRuntime? secrets = null, Action<string>? warn = null)
    {
        var models = new SemanticModels
        {
            Level = options.Semantic.Level,
            // The connection string is read when a query needs it, so a rotated secret is used without a restart
            SsasExecutor = (model, source, fallback) =>
            {
                if (model.Ssas is null || !model.Ssas.TryGetValue(source, out var connection))
                    throw new InvalidOperationException($"Model '{model.Name}' has no Analysis Services connection for source '{source}'.");
                return new SsasExecutor(secrets?.ResolveConnectionString($"Semantic.Ssas.{source}", connection, "ssas") ?? connection, fallback);
            },
        };
        foreach (var entry in options.Semantic.Models)
        {
            var path = Path.GetFullPath(entry.Catalog, dataDirectory);
            try { models.Add(Catalog.Load(path), entry.Sources, entry.Ssas); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Semantic model '{path}' cannot be loaded: {ex.Message}", ex);
            }
        }
        // the models published through the management API, after the configured ones (a configured name wins)
        new PublishedSemanticModels(dataDirectory).LoadInto(models, problem => (warn ?? Console.Error.WriteLine)(problem));
        return models;
    }

    /// <summary>
    /// Applies a changed configuration to the models in service: each configured catalog is read again and, if it loads, replaces the one serving; one that does not load is reported and the old one keeps serving.
    /// A model no longer configured stops being served unless a published model has its name. Returns the configured names, to hand back as <paramref name="previous"/> on the next change.
    /// </summary>
    public static IReadOnlySet<string> Reload(SemanticModels models, ProxyOptions options, string dataDirectory, IReadOnlySet<string> previous, Action<string> warn)
    {
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allLoaded = true;
        foreach (var entry in options.Semantic.Models)
        {
            var path = Path.GetFullPath(entry.Catalog, dataDirectory);
            try
            {
                var model = new SemanticModel(Catalog.Load(path), entry.Sources, entry.Ssas);
                models.Set(model);
                configured.Add(model.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allLoaded = false;
                warn($"Semantic model '{path}' cannot be loaded, so the model being served stays: {ex.Message}");
            }
        }
        // a catalog that failed has no name to match, so nothing is taken out of service on a reload with a failure in it
        if (!allLoaded) configured.UnionWith(previous);
        else foreach (var gone in previous.Where(n => !configured.Contains(n))) models.Remove(gone);
        new PublishedSemanticModels(dataDirectory).LoadInto(models, _ => { });
        return configured;
    }
}
