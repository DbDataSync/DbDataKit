using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DbData.Metadata;

/// <summary>In-process <see cref="ICatalogCache"/>. Entries from an older epoch are dropped lazily when the epoch moves on.</summary>
public sealed class MemoryCatalogCache : ICatalogCache
{
    private readonly ConcurrentDictionary<string, long> epochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<CatalogCacheKey, object> entries = new();

    public int Count => entries.Count;

    public long GetEpoch(string fingerprint) => epochs.GetValueOrDefault(fingerprint, 0);

    public long Invalidate(string fingerprint)
    {
        long next = epochs.AddOrUpdate(fingerprint, 1, static (_, old) => old + 1);
        // Older entries can never be hit again: free them now.
        foreach (var key in entries.Keys)
            if (key.Fingerprint == fingerprint && key.Epoch < next) entries.TryRemove(key, out _);
        return next;
    }

    public bool TryGet<T>(CatalogCacheKey key, out T? value) where T : class
    {
        if (key.Epoch == GetEpoch(key.Fingerprint) && entries.TryGetValue(key, out var found) && found is T typed) { value = typed; return true; }
        value = null;
        return false;
    }

    public void Set<T>(CatalogCacheKey key, T value) where T : class
    {
        // A result computed under an epoch that has since been invalidated is stale on arrival.
        if (key.Epoch == GetEpoch(key.Fingerprint)) entries[key] = value;
    }
}

/// <summary>
/// Caches shapes (and server facts) of another reader under a connection fingerprint and the cache's epoch. Listings and query descriptions are
/// not cached: listings are cheap to ask and always wanted fresh, and query shapes are keyed by arbitrary text. Call <see cref="Invalidate"/> after DDL
/// (or when the metadata epoch the product tracks changes); readers created over the same fingerprint share entries.
/// </summary>
public sealed class CachingCatalogReader(ICatalogReader inner, ICatalogCache cache, string fingerprint) : ICatalogReader
{
    public CatalogCapabilities Capabilities => inner.Capabilities;

    /// <summary>Starts a new epoch: every cached shape for this fingerprint is stale.</summary>
    public long Invalidate() => cache.Invalidate(fingerprint);

    private CatalogCacheKey Key(string item) => new(fingerprint, cache.GetEpoch(fingerprint), item);

    public async Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default)
    {
        var key = Key("facts");
        if (cache.TryGet<ServerFacts>(key, out var hit)) return hit!;
        var facts = await inner.GetServerFactsAsync(cancellationToken).ConfigureAwait(false);
        cache.Set(key, facts);
        return facts;
    }

    public IAsyncEnumerable<string> ListCatalogsAsync(CancellationToken cancellationToken = default) => inner.ListCatalogsAsync(cancellationToken);

    public IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, CancellationToken cancellationToken = default) => inner.ListTablesAsync(filter, cancellationToken);

    public async Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        var key = Key("shape:" + table.Key);
        if (cache.TryGet<TableShape>(key, out var hit)) return hit!;
        var shape = await inner.GetShapeAsync(table, cancellationToken).ConfigureAwait(false);
        cache.Set(key, shape);
        return shape;
    }

    /// <summary>A crawl reads everything anyway: it refreshes the cache with every shape it returns.</summary>
    public async IAsyncEnumerable<TableShape> CrawlAsync(TableFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long epoch = cache.GetEpoch(fingerprint);
        await foreach (var shape in inner.CrawlAsync(filter, cancellationToken).ConfigureAwait(false))
        {
            cache.Set(new CatalogCacheKey(fingerprint, epoch, "shape:" + shape.Table.Key), shape);
            yield return shape;
        }
    }

    public Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default) => inner.DescribeQueryAsync(sql, cancellationToken);
}
