using System.Text.Json;

namespace DbDataKit.Serving;

/// <summary>One dataset's cache setting as a host reads it from the published definition's <c>cache</c> section, for the console (24a UC16) and for the policy.</summary>
/// <param name="Name">The definition: a data source name or <c>semantic:&lt;model&gt;</c>, as a request names its source.</param>
/// <param name="Kind">What it is: <c>data-source</c> or <c>semantic-model</c>.</param>
/// <param name="Enabled">The definition says <c>enabled: true</c>.</param>
/// <param name="Ttl">The time-to-live, at most 24 hours.</param>
/// <param name="Scope">Who shares an entry.</param>
/// <param name="Version">The version of the definition the entries are keyed by.</param>
/// <param name="Datasets">The names the cache keys these entries by (what a <c>data.changed</c> maps to).</param>
/// <param name="Note">Why an enabled setting does nothing (a pass-through data source answers with the caller's own database login, so it is never shared), or null.</param>
public sealed record ServingCacheSetting(string Name, string Kind, bool Enabled, TimeSpan Ttl, ServingCacheScope Scope, string? Version, IReadOnlyList<string> Datasets, string? Note = null);

/// <summary>A host that can list the cache settings of what it serves.</summary>
public interface IServingCacheSettings
{
    IReadOnlyList<ServingCacheSetting> Settings();
}

/// <summary>Which requests can be cached at all, whatever a definition says (24a UC12).</summary>
public static class ServingCacheable
{
    /// <summary>
    /// A statement over data. A semantic request is cached only when it is a query (<c>kind: query</c>): a description of the catalog or a member listing is answered per caller from what they may see, and an <c>EXPLAIN</c>, a call of a
    /// saved query or any statement that does not begin with SELECT or WITH is run every time. MDX is not cached.
    /// </summary>
    public static bool IsRead(ServingRequest request) => request switch
    {
        ServingRequest.Sql sql => !IsSemanticDocument(sql) || QueryDocument(sql.Text),
        ServingRequest.Semantic semantic => QueryDocument(semantic.Json),
        ServingRequest.SemanticSql semanticSql => StartsLikeSelect(semanticSql.Text),
        _ => false,
    };

    /// <summary>A request on the <c>semantic:</c> source whose text is a JSON document and not SQL.</summary>
    private static bool IsSemanticDocument(ServingRequest.Sql sql) => sql.Source.StartsWith("semantic:", StringComparison.OrdinalIgnoreCase) && sql.Text.TrimStart().StartsWith('{');

    private static bool QueryDocument(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "query";
        }
        catch (JsonException) { return false; }
    }

    private static bool StartsLikeSelect(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || t.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The core services with the result cache in front of the query service (24a UC12); everything else is the inner services'.</summary>
public static class ServingCacheServices
{
    public static IServingServices WithCache(this IServingServices inner, IServingCache cache, IServingCachePolicy policy) => new CachedServices(inner, new CachingServingQueries(inner.Queries, cache, policy));

    private sealed class CachedServices(IServingServices inner, IServingQueries queries) : IServingServices
    {
        public IServingIdentity Identity => inner.Identity;

        public IServingSessions Sessions => inner.Sessions;

        public IServingQueries Queries => queries;

        public IServingCatalog Catalog => inner.Catalog;

        public IServingEntities? Entities => inner.Entities;

        public IServingUpdates? Updates => inner.Updates;
    }
}
