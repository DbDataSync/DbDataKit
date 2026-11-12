using System.Text;
using DbDataKit.Authoring;
using DbDataKit.Catalog;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Authoring.Server;

/// <summary>The catalog cache of a draft as the kinds' check reports it (the format and its strict reader are the library <c>DbDataKit.Catalog.Cache</c>, which Sync and Build read too).</summary>
public static class CatalogCacheCheck
{
    /// <summary>
    /// The finding of the cache in a draft, as the kinds' check reports them: nothing when the draft has none (a cache is optional), the reader's findings (file, line) when it has one. Never throws for a damaged file.
    /// </summary>
    public static IReadOnlyList<ProjectDiagnostic> Check(string root)
    {
        var path = Path.Combine(root, CatalogCache.FileName);
        if (!File.Exists(path)) return [];
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException) { return [new("error", "DBD-CAT-READ", $"{CatalogCache.FileName} cannot be read: {ex.Message}", CatalogCache.FileName)]; }
        return [.. CatalogCache.Load(text, CatalogCache.FileName).Diagnostics.Select(d => new ProjectDiagnostic(d.Severity switch { ConfigSeverity.Error => "error", ConfigSeverity.Warning => "warning", _ => "info" }, d.Code, d.Message, CatalogCache.FileName, d.Line > 0 ? d.Line : null))];
    }
}

/// <summary>The check of a draft as the server runs it: the kind's own validators, then the catalog cache when the draft has one (every kind may carry it, so it is checked here and not in each kind).</summary>
public static class DraftCheck
{
    public static async Task<IReadOnlyList<ProjectDiagnostic>> RunAsync(IProjectKind kind, string root, CancellationToken cancellationToken)
    {
        var found = await kind.CheckAsync(root, cancellationToken).ConfigureAwait(false);
        var cache = CatalogCacheCheck.Check(root);
        return cache.Count == 0 ? found : [.. found, .. cache];
    }
}
