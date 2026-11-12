using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace DbDataKit.Serving;

/// <summary>Who may share a cached result (plan 24 section 3, plan 24a UC03 and UC12); the same three words as the <c>scope</c> of a definition's <c>cache</c> section.</summary>
public enum ServingCacheScope
{
    /// <summary>Each caller's results are kept apart.</summary>
    Caller,

    /// <summary>
    /// Callers the access rules treat alike share an entry: the key holds the fingerprint of the caller's resolved rules (<see cref="ServingCachePlan.RulesFingerprint"/>, plan 22a GP10, QG2): the rules' definition, the refresh generation of each
    /// rules table and the caller's names that appear in it. A plan with no fingerprint (a definition with no rules) falls back to all of the caller's directory attributes (<see cref="ServingCaller.Attributes"/>), a superset of any rule's.
    /// </summary>
    Rules,

    /// <summary>Every caller shares. For a dataset no rule narrows; the owner chose it in the definition.</summary>
    Shared,
}

/// <summary>What the cache does for one request: how long an entry lives, who shares it, which published version it answers from and which datasets it reads (what a data change clears).</summary>
/// <param name="Ttl">The time-to-live, the backstop for a source written outside the suite (24a QU7).</param>
/// <param name="Scope">Who may share an entry.</param>
/// <param name="DefinitionVersion">The published version the request is pinned to; part of the key, so a new version is a new key.</param>
/// <param name="Datasets">The datasets the answer reads, by the identity a <c>data.changed</c> message names; empty when it cannot say (only the time-to-live and a publish clear it).</param>
/// <param name="RulesFingerprint">The fingerprint of the caller's resolved access rules (plan 22a GP10) when the definition has rules: callers with the same effective rules have the same one, so scope <c>rules</c> shares an entry between them and no one else; null when the definition has none.</param>
public sealed record ServingCachePlan(TimeSpan Ttl, ServingCacheScope Scope, string? DefinitionVersion, IReadOnlyList<string>? Datasets = null, string? RulesFingerprint = null);

/// <summary>
/// Which requests are cached. The cache is off unless this says otherwise: a request the policy gives no plan for is never stored and never answered from the cache (plan 24 section 3.1, 24a UC12). The policy is
/// built from the published definitions' <c>cache</c> sections, which is why a cache setting is published and promoted with the definition.
/// </summary>
public interface IServingCachePolicy
{
    /// <summary>The plan for this request, or null when it is not to be cached.</summary>
    ServingCachePlan? PlanFor(ServingContext context, ServingRequest request);
}

/// <summary>A policy that caches nothing.</summary>
public sealed class NoServingCache : IServingCachePolicy
{
    public static NoServingCache Instance { get; } = new();

    public ServingCachePlan? PlanFor(ServingContext context, ServingRequest request) => null;
}

/// <summary>What the cache counts, for the console (24a UC16).</summary>
public sealed record ServingCacheStats(long Hits, long Misses, int Entries, long Bytes, long Evictions, long Cleared);

/// <summary>What the cache counts for one dataset (as <see cref="ServingCachePlan.Datasets"/> names it), for the console (24a UC16): the entries that read it, their bytes, and the hits and misses of requests that read it.</summary>
public sealed record ServingCacheDatasetStats(string Dataset, int Entries, long Bytes, long Hits, long Misses);

/// <summary>A stored answer: the Arrow IPC stream of its batches and where it came from.</summary>
public sealed record ServingCachedAnswer(byte[] Ipc, ServingProvenance Provenance);

/// <summary>
/// The result cache of the serving layer (plan 24a D7: a service of the core, never a module's own). A cold or lost cache changes no result: every entry is a copy of what the source answered, and the answer replays byte for
/// byte the batches it was made from.
/// </summary>
public interface IServingCache
{
    /// <summary>The entry for the key when there is one and it has not expired.</summary>
    bool TryGet(string key, out ServingCachedAnswer answer);

    /// <summary>The number a store must hand back to <see cref="Set"/>, taken before the query runs: a clear in between makes the store refuse, so an answer read before a data change is never kept after it.</summary>
    long Epoch { get; }

    /// <summary>Keeps the answer when nothing was cleared since <paramref name="epoch"/> and it is small enough; returns whether it was kept.</summary>
    bool Set(string key, ServingCachedAnswer answer, TimeSpan ttl, IReadOnlyList<string>? datasets, long epoch);

    /// <summary>Removes the entries that read the dataset (and those that cannot say what they read); returns how many.</summary>
    int Invalidate(string dataset);

    /// <summary>Removes every entry; returns how many.</summary>
    int Clear();

    ServingCacheStats Stats { get; }

    /// <summary>As <see cref="TryGet(string, out ServingCachedAnswer)"/>, naming the datasets the request reads so a miss is counted for them too.</summary>
    bool TryGet(string key, IReadOnlyList<string>? datasets, out ServingCachedAnswer answer) => TryGet(key, out answer);

    /// <summary>Entries, bytes, hits and misses by dataset; empty for a cache that does not count them.</summary>
    IReadOnlyList<ServingCacheDatasetStats> DatasetStats => [];
}

/// <summary>Sizes of the in-memory cache (24a UC12: in memory per instance, with a size bound).</summary>
/// <param name="MaxBytes">The most the entries may hold in all; the least recently used go first.</param>
/// <param name="MaxEntryBytes">The most one entry may hold; a bigger answer is passed through and not kept.</param>
public sealed record MemoryServingCacheOptions(long MaxBytes = 64L * 1024 * 1024, long MaxEntryBytes = 8L * 1024 * 1024);

/// <summary>The cache of one instance: a least-recently-used map with a byte bound and a time-to-live per entry.</summary>
public sealed class MemoryServingCache(MemoryServingCacheOptions? options = null, TimeProvider? time = null) : IServingCache
{
    private sealed class Entry(string key, ServingCachedAnswer answer, DateTimeOffset expires, IReadOnlyList<string>? datasets)
    {
        public string Key { get; } = key;
        public ServingCachedAnswer Answer { get; } = answer;
        public DateTimeOffset Expires { get; } = expires;
        public IReadOnlyList<string>? Datasets { get; } = datasets;
        public long Size => Answer.Ipc.LongLength + Key.Length * 2L + 256;
    }

    private readonly MemoryServingCacheOptions _options = options ?? new();

    /// <summary>The bounds this cache runs on.</summary>
    public MemoryServingCacheOptions Options => _options;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recent = new();
    private long _bytes;
    private long _epoch;
    private long _hits, _misses, _evictions, _cleared;
    private readonly Dictionary<string, (long Hits, long Misses)> _byDataset = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name an entry that cannot say what it reads is counted under.</summary>
    public const string Unscoped = "(unscoped)";

    public long Epoch => Interlocked.Read(ref _epoch);

    public bool TryGet(string key, out ServingCachedAnswer answer) => TryGet(key, null, out answer);

    public bool TryGet(string key, IReadOnlyList<string>? datasets, out ServingCachedAnswer answer)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var node))
            {
                if (node.Value.Expires > _time.GetUtcNow())
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    _hits++;
                    Count(node.Value.Datasets ?? datasets, hit: true);
                    answer = node.Value.Answer;
                    return true;
                }

                Remove(node);
            }

            _misses++;
            Count(datasets, hit: false);
            answer = null!;
            return false;
        }
    }

    public bool Set(string key, ServingCachedAnswer answer, TimeSpan ttl, IReadOnlyList<string>? datasets, long epoch)
    {
        var entry = new Entry(key, answer, _time.GetUtcNow() + ttl, datasets is { Count: > 0 } ? [.. datasets] : null);
        if (entry.Size > _options.MaxEntryBytes || entry.Size > _options.MaxBytes || ttl <= TimeSpan.Zero) return false;
        lock (_gate)
        {
            if (epoch != _epoch) return false;
            if (_byKey.TryGetValue(key, out var old)) Remove(old);
            _byKey[key] = _recent.AddFirst(entry);
            _bytes += entry.Size;
            while (_bytes > _options.MaxBytes && _recent.Last is { } last && last.Value.Key != key)
            {
                Remove(last);
                _evictions++;
            }

            return true;
        }
    }

    public int Invalidate(string dataset)
    {
        lock (_gate)
        {
            _epoch++;
            var gone = _recent.Where(e => e.Datasets is null || e.Datasets.Contains(dataset, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var entry in gone) Remove(_byKey[entry.Key]);
            _cleared += gone.Count;
            return gone.Count;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            _epoch++;
            var count = _byKey.Count;
            _byKey.Clear();
            _recent.Clear();
            _bytes = 0;
            _cleared += count;
            return count;
        }
    }

    public ServingCacheStats Stats
    {
        get
        {
            lock (_gate) return new ServingCacheStats(_hits, _misses, _byKey.Count, _bytes, _evictions, _cleared);
        }
    }

    private void Count(IReadOnlyList<string>? datasets, bool hit)
    {
        foreach (var dataset in datasets is { Count: > 0 } ? datasets : [Unscoped])
        {
            _byDataset.TryGetValue(dataset, out var counts);
            _byDataset[dataset] = hit ? (counts.Hits + 1, counts.Misses) : (counts.Hits, counts.Misses + 1);
        }
    }

    public IReadOnlyList<ServingCacheDatasetStats> DatasetStats
    {
        get
        {
            lock (_gate)
            {
                var entries = new Dictionary<string, (int Entries, long Bytes)>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _recent)
                    foreach (var dataset in entry.Datasets ?? [Unscoped])
                    {
                        entries.TryGetValue(dataset, out var known);
                        entries[dataset] = (known.Entries + 1, known.Bytes + entry.Size);
                    }

                return [.. entries.Keys.Union(_byDataset.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(name =>
                {
                    entries.TryGetValue(name, out var e);
                    _byDataset.TryGetValue(name, out var c);
                    return new ServingCacheDatasetStats(name, e.Entries, e.Bytes, c.Hits, c.Misses);
                })];
            }
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _recent.Remove(node);
        _byKey.Remove(node.Value.Key);
        _bytes -= node.Value.Size;
    }
}

/// <summary>
/// The cache key (24a UC12): one place builds it, so no module leaves a part out. It is a SHA-256 over length-prefixed fields: the query as asked (kind, source, text or document, positional parameters, the limits the
/// request asked for), the channel and the preview draft, the published version, and, by scope, who the entry is for (the caller's id, the caller's directory attributes, or no one). Two callers with the same effective
/// access share an entry in scope <c>rules</c>; with different attribute values they do not; two channels never do; a preview never shares with its channel; a new version is a new key.
/// </summary>
public static class ServingCacheKey
{
    public static string Build(ServingContext context, ServingRequest request, ServingCachePlan plan)
    {
        var fields = new List<string> { "serving-cache/1", request.GetType().Name, request.Source };
        switch (request)
        {
            case ServingRequest.Sql sql:
                fields.Add(sql.Text);
                AddParameters(fields, sql.Parameters);
                break;
            case ServingRequest.Semantic semantic:
                fields.Add(semantic.Json);
                break;
            case ServingRequest.SemanticSql semanticSql:
                fields.Add(semanticSql.Text);
                AddParameters(fields, semanticSql.Parameters);
                break;
            case ServingRequest.Mdx mdx:
                fields.Add(mdx.Text);
                break;
            default:
                throw new ArgumentException($"The cache does not know the request kind {request.GetType().Name}; it cannot build a key for it.", nameof(request));
        }

        var asked = request.Asked;
        fields.Add(Invariant(asked?.MaxRows));
        fields.Add(asked?.Timeout is { } timeout ? Invariant((long)timeout.TotalMilliseconds) : "-");
        fields.Add(Invariant(asked?.MaxBatchRows));
        fields.Add(context.Channel);
        fields.Add(context.Preview is { } preview ? "preview:" + preview : "-");
        fields.Add(plan.DefinitionVersion ?? "-");
        fields.Add(plan.Scope.ToString());
        // a definition with rules: whoever shares, the fingerprint of the rules is part of the key, so a refreshed rules table or another caller's rules is another key
        if (plan.RulesFingerprint is { } fingerprint) fields.Add("rules:" + fingerprint);
        switch (plan.Scope)
        {
            case ServingCacheScope.Caller:
                fields.Add(context.Caller.Id);
                break;
            case ServingCacheScope.Rules when plan.RulesFingerprint is not null:
                break;
            case ServingCacheScope.Rules:
                foreach (var (name, values) in context.Caller.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
                {
                    fields.Add("attr:" + name);
                    foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) fields.Add(value);
                }

                break;
        }

        var text = new StringBuilder();
        foreach (var field in fields) text.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void AddParameters(List<string> fields, IReadOnlyList<ServingParameter>? parameters)
    {
        fields.Add(Invariant(parameters?.Count ?? 0));
        foreach (var p in parameters ?? []) fields.Add(p.Type + "=" + (p.Text ?? "\0null"));
    }

    private static string Invariant(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-";
}

/// <summary>
/// Wraps the query service so a request the policy names is answered from the cache when it can be and stored when it cannot (24a UC12). A request with no plan goes straight to the inner service, so the cache is off
/// for every dataset that has not turned it on. The inner service still applies the access rules to a miss; a hit is only ever found under a key that holds the caller's access, by scope. An answer that fails while it
/// streams, passes a limit or is too big is not stored.
/// </summary>
public sealed class CachingServingQueries(IServingQueries inner, IServingCache cache, IServingCachePolicy policy) : IServingQueries
{
    public async Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
    {
        if (policy.PlanFor(context, request) is not { } plan) return await inner.RunAsync(context, request, cancellationToken).ConfigureAwait(false);
        var key = ServingCacheKey.Build(context, request, plan);
        if (cache.TryGet(key, plan.Datasets, out var hit)) return new HitAnswer(hit);
        var epoch = cache.Epoch;
        var answer = await inner.RunAsync(context, request, cancellationToken).ConfigureAwait(false);
        return new TeeAnswer(answer, cache, key, plan, epoch);
    }

    public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) =>
        inner.PrepareAsync(context, statement, cancellationToken);

    private sealed class HitAnswer(ServingCachedAnswer stored) : IServingAnswer
    {
        private readonly MemoryStream _stream = new(stored.Ipc, writable: false);
        private ArrowStreamReader? _reader;

        public ServingProvenance Provenance { get; } = stored.Provenance with { Cache = "hit" };

        public Schema Schema => (_reader ??= new ArrowStreamReader(_stream)).Schema;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = Schema;
            while (await _reader!.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false) is { } batch) yield return batch;
        }

        public ValueTask DisposeAsync()
        {
            _reader?.Dispose();
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TeeAnswer(IServingAnswer source, IServingCache cache, string key, ServingCachePlan plan, long epoch) : IServingAnswer
    {
        private bool _read;

        public ServingProvenance Provenance => source.Provenance;

        public Schema Schema => source.Schema;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // only the first reading is copied; a second one reads the source as it would without the cache
            var copy = _read ? null : new MemoryStream();
            _read = true;
            var writer = copy is null ? null : new ArrowStreamWriter(copy, source.Schema, leaveOpen: true);
            var complete = false;
            try
            {
                await foreach (var batch in source.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (writer is not null) await writer.WriteRecordBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    yield return batch;
                }

                complete = true;
            }
            finally
            {
                if (writer is not null && copy is not null && complete)
                {
                    await writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
                    cache.Set(key, new ServingCachedAnswer(copy.ToArray(), source.Provenance), plan.Ttl, plan.Datasets, epoch);
                }

                writer?.Dispose();
                copy?.Dispose();
            }
        }

        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
