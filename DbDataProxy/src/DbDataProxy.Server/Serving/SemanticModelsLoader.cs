using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.Semantic.Ssas;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;

namespace DbDataProxy.Server.Serving;

/// <summary>Reads the catalogs the <c>Semantic</c> section names, once, when the server starts: a catalog that does not load stops the start and names its file.</summary>
public static class SemanticModelsLoader
{
    public static SemanticModels Load(ProxyOptions options, string dataDirectory, SecretsRuntime? secrets = null)
    {
        var connections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var models = new SemanticModels
        {
            Level = options.Semantic.Level,
            // The connection string is read when a query needs it, so a rotated secret is used without a restart
            SsasExecutor = (model, source, fallback) =>
            {
                if (!connections.TryGetValue(model, out var byName) || !byName.TryGetValue(source, out var connection))
                    throw new InvalidOperationException($"Model '{model}' has no Analysis Services connection for source '{source}'.");
                return new SsasExecutor(secrets?.ResolveConnectionString($"Semantic.Ssas.{source}", connection, "ssas") ?? connection, fallback);
            },
        };
        foreach (var entry in options.Semantic.Models)
        {
            var path = Path.GetFullPath(entry.Catalog, dataDirectory);
            try
            {
                var catalog = Catalog.Load(path);
                models.Add(catalog, entry.Sources);
                connections[catalog.Name] = entry.Ssas;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Semantic model '{path}' cannot be loaded: {ex.Message}", ex);
            }
        }
        return models;
    }
}
