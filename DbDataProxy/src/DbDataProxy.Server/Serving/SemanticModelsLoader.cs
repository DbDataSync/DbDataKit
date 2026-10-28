using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Serving;

/// <summary>Reads the catalogs the <c>Semantic</c> section names, once, when the server starts: a catalog that does not load stops the start and names its file.</summary>
public static class SemanticModelsLoader
{
    public static SemanticModels Load(ProxyOptions options, string dataDirectory)
    {
        var models = new SemanticModels { Level = options.Semantic.Level };
        foreach (var entry in options.Semantic.Models)
        {
            var path = Path.GetFullPath(entry.Catalog, dataDirectory);
            try { models.Add(Catalog.Load(path), entry.Sources); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Semantic model '{path}' cannot be loaded: {ex.Message}", ex);
            }
        }
        return models;
    }
}
