using System.Text.Json;
using DbDataKit.State;
using Microsoft.Extensions.AI;

namespace DbDataKit.Knowledge;

/// <summary>What the index of a channel holds and how it is searched (for the console's knowledge screens). <see cref="Vectors"/> is null when the database has no vector module (the engine cannot hold vectors, or the host did not create it).</summary>
public sealed record KnowledgeIndexStatus(string Channel, int Items, int Chunks, TextSearchMode Mode, VectorProgress? Vectors = null);

/// <summary>How a question is searched: by its words, by its meaning (vectors), or both with the two rankings fused.</summary>
public enum KnowledgeSearchMode
{
    Words,
    Vectors,
    Both,
}

/// <summary>What a search found and how it was found: <see cref="Used"/> can be less than what was asked for (no vector index yet, the question could not be embedded), and <see cref="Note"/> says why.</summary>
public sealed record KnowledgeSearchResult(IReadOnlyList<SearchHit> Hits, KnowledgeSearchMode Used, string? Note);

/// <summary>Finds the generator that embeds a question for a model id recorded in the vector index (the model the index was built with, which can be an earlier one than the model now bound); null when it is not available.</summary>
public delegate IEmbeddingGenerator<string, Embedding<float>>? EmbeddingResolver(string modelId);

/// <summary>
/// The knowledge index of one channel on a state database (plan 27a KN07, KN08): <see cref="Put"/> chunks an item's rows into the <c>knowledge</c> module, <see cref="SearchAsync"/> finds chunks by their words and applies the
/// same access filter as the in-memory index (<see cref="KnowledgeAccess"/>, decision SD9) to each item at the time of the question, then assembles the hits as <see cref="KnowledgeSearch"/> does. An item that has not changed is not written again.
/// The ranking is the engine's (bm25 on SQLite, <c>ts_rank_cd</c> on PostgreSQL, a count of matched words elsewhere): the order of a few chunks is reproducible on every engine, the numbers are not comparable between them.
/// <para>
/// Where the database also has the <c>knowledge_vectors</c> module (<see cref="KnowledgeVectorStore"/>), <see cref="FindAsync"/> searches by meaning or by both, fusing the two rankings; the vectors are made by <see cref="KnowledgeEmbedder"/>, never by <see cref="Put"/>.
/// </para>
/// </summary>
public sealed class StateKnowledgeIndex(KnowledgeStore store, string channel, KnowledgeVectorStore? vectors = null)
{
    /// <summary>The constant of reciprocal rank fusion: a chunk at rank r in a list scores 1/(K + r) there, and the lists' scores are added.</summary>
    public const int FusionConstant = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Channel { get; } = channel;

    public KnowledgeStore Store { get; } = store;

    /// <summary>The vector side (the <c>knowledge_vectors</c> module), or null when the database has none: the index then answers by words only.</summary>
    public KnowledgeVectorStore? Vectors { get; } = vectors;

    public KnowledgeIndexStatus Status()
    {
        var (items, chunks) = Store.Counts(Channel);
        return new KnowledgeIndexStatus(Channel, items, chunks, Store.Mode, Vectors?.Progress(Channel));
    }

    /// <summary>Indexes an item with the chunks it was split into. False when the item was already there with the same hash (nothing is written). It never calls an embedding model: vectors are made later, by <see cref="KnowledgeEmbedder"/>.</summary>
    /// <param name="item">The item.</param>
    /// <param name="chunks">The pieces <see cref="Chunker"/> split it into.</param>
    /// <param name="time">The clock the indexing time is read from (the system's by default).</param>
    /// <param name="force">Write it even when it is there with the same hash (after a change of how chunks are made).</param>
    /// <param name="source">What it came from (the project or definition).</param>
    /// <param name="version">The published version, a short hash.</param>
    public bool Put(KnowledgeItem item, IReadOnlyList<Chunk> chunks, string source, string version, TimeProvider? time = null, bool force = false)
    {
        var audience = JsonSerializer.Serialize(new AudienceJson(item.For.Audience, item.For.Agents, item.For.Channels), Json);
        var stored = new StoredItem(item.Id, item.File, item.Title, item.Role.ToString().ToLowerInvariant(), item.Owner, audience, item.AboutImplied, item.Hash, source, version, [.. item.About.Select(a => a.Key)], (time ?? TimeProvider.System).GetUtcNow());
        var pieces = chunks.Select((c, i) => new StoredChunk(i, string.Join(" > ", c.HeadingPath), c.Text, ChunkerOptions.EstimateTokens(c.Text), c.Hash, c.StartLine)).ToList();
        return Store.Put(Channel, stored, pieces, force);
    }

    public bool Remove(string itemId) => Store.Remove(Channel, itemId);

    private sealed record AudienceJson(IReadOnlyList<string> Audience, IReadOnlyList<string> Agents, IReadOnlyList<string> Channels);

    /// <summary>An item as the access filter needs it, from what the index kept (the text of the item is not needed to decide who may see it).</summary>
    public static KnowledgeItem ItemOf(StoredItem stored)
    {
        var audience = JsonSerializer.Deserialize<AudienceJson>(stored.Audience, Json) ?? new AudienceJson([], [], []);
        var about = stored.Subjects.Select(KnowledgeSubject.TryParse).OfType<KnowledgeSubject>().ToList();
        Enum.TryParse<KnowledgeRole>(stored.Role, ignoreCase: true, out var role);
        return new KnowledgeItem(stored.Id, stored.File, stored.Title, role, about, stored.AboutImplied, new KnowledgeAudience(audience.Audience, audience.Agents, audience.Channels), stored.Owner, ChunkingSetting.Default, "", 0, stored.Hash);
    }

    /// <summary>
    /// Finds the chunks that have any of the question's words, among those the caller may see, and gives them back per item with the title, the heading path, the source and the version. At most <paramref name="items"/> items.
    /// </summary>
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string question, IKnowledgeCaller caller, ISubjectCatalog catalog, int items = 5, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(Widen(items, limit => Store.Search(Channel, question, limit), caller, catalog));
    }

    /// <summary>
    /// Searches by words, by meaning or by both, under the same access filter as the search by words. <b>Both</b> fuses the two rankings by reciprocal rank (a chunk found by both is placed above one found by either alone). An index with no vector module, no ready
    /// vector index yet, or no way to embed the question (<c>embeddings</c> finds the generator for the model the ready index was made with) still answers, by words, and says so in <see cref="KnowledgeSearchResult.Note"/>: meaning is added where it is available and never required.
    /// </summary>
    public async Task<KnowledgeSearchResult> FindAsync(string question, IKnowledgeCaller caller, ISubjectCatalog catalog, KnowledgeSearchMode mode, EmbeddingResolver? embeddings, int items = 5, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode == KnowledgeSearchMode.Words) return new(await SearchAsync(question, caller, catalog, items, cancellation).ConfigureAwait(false), KnowledgeSearchMode.Words, null);

        string? note = null;
        float[]? query = null;
        if (Vectors is null) note = "This database has no vector module (the engine cannot hold vectors), so the question is searched by its words.";
        else if (Vectors.ReadyIndex(Channel) is not { } ready) note = Vectors.Indexes(Channel).Count == 0 ? "No vector index is built for this channel yet, so the question is searched by its words." : "The vector index is still being built, so the question is searched by its words.";
        else if (embeddings?.Invoke(ready.ModelId) is not { } generator) note = $"The embedding model '{ready.ModelId}' of the vector index is not available now, so the question is searched by its words.";
        else
        {
            try { query = (await generator.GenerateAsync([question], cancellationToken: cancellation).ConfigureAwait(false))[0].Vector.ToArray(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { note = $"The question could not be embedded ({ex.Message}), so it is searched by its words."; }
            if (query is not null && query.Length != ready.Dimension) { note = KnowledgeVectorStore.DimensionMismatch(Channel, ready.ModelId, ready.Dimension, query.Length) + " The question is searched by its words."; query = null; }
        }
        if (query is null) return new(await SearchAsync(question, caller, catalog, items, cancellation).ConfigureAwait(false), KnowledgeSearchMode.Words, note);

        if (mode == KnowledgeSearchMode.Vectors)
            return new(Widen(items, limit => Vectors!.Nearest(Channel, query, limit), caller, catalog), KnowledgeSearchMode.Vectors, null);
        return new(Widen(items, limit => Fuse(Store.Search(Channel, question, limit), Vectors!.Nearest(Channel, query, limit), limit), caller, catalog), KnowledgeSearchMode.Both, null);
    }

    /// <summary>Reciprocal rank fusion of two rankings of chunks: each chunk scores the sum of 1/(<see cref="FusionConstant"/> + rank) over the lists that have it; best first, ties by item and place.</summary>
    public static IReadOnlyList<TextMatch> Fuse(IReadOnlyList<TextMatch> words, IReadOnlyList<TextMatch> vectors, int limit)
    {
        var scores = new Dictionary<(string Item, int Seq), double>();
        foreach (var list in new[] { words, vectors })
            for (var rank = 0; rank < list.Count; rank++)
            {
                var key = (list[rank].ItemId, list[rank].Seq);
                scores[key] = scores.GetValueOrDefault(key) + 1.0 / (FusionConstant + rank + 1);
            }
        return [.. scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Item, StringComparer.Ordinal).ThenBy(p => p.Key.Seq).Take(limit).Select(p => new TextMatch(p.Key.Item, p.Key.Seq, p.Value))];
    }

    /// <summary>
    /// The candidates are taken before the access filter, so a caller who may see little could be left with too few: the number asked for is widened (four times, then sixteen) while the candidates were all used and the visible hits do not yet fill the answer.
    /// </summary>
    private IReadOnlyList<SearchHit> Widen(int items, Func<int, IReadOnlyList<TextMatch>> candidates, IKnowledgeCaller caller, ISubjectCatalog catalog)
    {
        var limit = items * KnowledgeSearch.MaximumChunksPerItem * 4;
        for (var round = 0; ; round++)
        {
            var found = candidates(limit);
            var hits = Resolve(found, caller, catalog, items);
            if (hits.Count >= items || found.Count < limit || round == 2) return hits;
            limit *= 4;
        }
    }

    private IReadOnlyList<SearchHit> Resolve(IReadOnlyList<TextMatch> found, IKnowledgeCaller caller, ISubjectCatalog catalog, int items)
    {
        var visible = new Dictionary<string, (KnowledgeItem Item, string Version)?>(StringComparer.Ordinal);
        (KnowledgeItem Item, string Version)? Visible(string id)
        {
            if (visible.TryGetValue(id, out var known)) return known;
            var stored = Store.Item(Channel, id);
            var item = stored is null ? null : ItemOf(stored);
            return visible[id] = item is not null && KnowledgeAccess.Visible(item, caller, catalog) ? (item, stored!.Version) : null;
        }

        var chunkCache = new Dictionary<string, IReadOnlyList<StoredChunk>>(StringComparer.Ordinal);
        var matches = new List<ChunkMatch>();
        foreach (var match in found)
        {
            if (Visible(match.ItemId) is not { } allowed) continue;
            if (!chunkCache.TryGetValue(match.ItemId, out var chunks)) chunkCache[match.ItemId] = chunks = Store.Chunks(Channel, match.ItemId);
            var stored = chunks.FirstOrDefault(c => c.Seq == match.Seq);
            if (stored is null) continue;
            var path = stored.HeadingPath.Length == 0 ? [] : stored.HeadingPath.Split(" > ");
            var chunk = new Chunk($"{match.ItemId}#{stored.Seq}", match.ItemId, allowed.Item.Title, path, stored.Text, stored.Text, stored.Hash, stored.StartLine);
            // the item carries the version as its hash, which is what a hit cites; the index's own version of the item is the published one
            matches.Add(new ChunkMatch(new IndexedChunk(chunk, allowed.Item with { Hash = allowed.Version.PadRight(12, '0') }, []), match.Score));
        }
        return KnowledgeSearch.Assemble(matches, items);
    }
}
