using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace DbDataKit.Knowledge;

/// <summary>
/// The deterministic fake of the embedding model (plan 27a KN05, decision SD4): the vector of a text is the normalised count of its words, each word put in a place and given a sign by its hash (SHA-256, so the same on every run and platform). Texts that share
/// words are near each other, so ranking, access filtering and refresh are tested with no model; tests of a real embedding kind are separate and need their server.
/// </summary>
public sealed partial class FakeEmbeddingGenerator(int dimensions = 64, string modelId = "fake-embedding") : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Dimensions { get; } = dimensions;

    public string ModelId { get; } = modelId;

    /// <summary>How many texts were embedded: a test that checks "only the changed chunks are embedded again" reads it.</summary>
    public int Embedded { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Vector(v)) { ModelId = ModelId }));
        return Task.FromResult(result);
    }

    public float[] Vector(string text)
    {
        Embedded++;
        var vector = new float[Dimensions];
        foreach (Match word in Words().Matches(text.ToLowerInvariant()))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word.Value));
            var index = (int)(BitConverter.ToUInt32(hash, 0) % (uint)Dimensions);
            vector[index] += (hash[4] & 1) == 0 ? 1f : -1f;
        }
        var length = MathF.Sqrt(vector.Sum(x => x * x));
        if (length > 0) for (var i = 0; i < vector.Length; i++) vector[i] /= length;
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Words();
}

/// <summary>A chunk in an index with its vector and the item it belongs to (so the access filter can be asked about the item at the time of the question).</summary>
public sealed record IndexedChunk(Chunk Chunk, KnowledgeItem Item, float[] Vector);

/// <summary>One match of a search: the chunk and how near it was (cosine similarity, 1 is the same direction).</summary>
public sealed record ChunkMatch(IndexedChunk Indexed, double Score);

/// <summary>An index of chunks by vector (plan 27a KN07 builds it on SQL Server 2025, PostgreSQL with pgvector and SQLite; <see cref="InMemoryKnowledgeIndex"/> is the reference every engine's ranking is compared with).</summary>
public interface IKnowledgeIndex
{
    string Model { get; }

    int Dimensions { get; }

    int Count { get; }

    void Put(IndexedChunk chunk);

    void RemoveItem(string itemId);

    /// <summary>The nearest chunks, best first, among those <paramref name="allowed"/> accepts (the filter runs on each candidate, so an access change takes effect at once with no rebuild).</summary>
    IReadOnlyList<ChunkMatch> Nearest(float[] query, int count, Func<IndexedChunk, bool> allowed);
}

/// <summary>Brute-force cosine search in memory: the reference for ranking and the index tests of everything built on <see cref="IKnowledgeIndex"/>.</summary>
public sealed class InMemoryKnowledgeIndex(string model, int dimensions) : IKnowledgeIndex
{
    private readonly Dictionary<string, IndexedChunk> _chunks = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string Model { get; } = model;

    public int Dimensions { get; } = dimensions;

    public int Count { get { lock (_gate) return _chunks.Count; } }

    public void Put(IndexedChunk chunk)
    {
        if (chunk.Vector.Length != Dimensions) throw new ArgumentException($"The index holds {Dimensions}-dimension vectors of model '{Model}'; this chunk has {chunk.Vector.Length}: one model per index.", nameof(chunk));
        lock (_gate) _chunks[chunk.Chunk.Id] = chunk;
    }

    public void RemoveItem(string itemId)
    {
        lock (_gate)
            foreach (var id in _chunks.Where(c => c.Value.Chunk.ItemId == itemId).Select(c => c.Key).ToList()) _chunks.Remove(id);
    }

    public IReadOnlyList<ChunkMatch> Nearest(float[] query, int count, Func<IndexedChunk, bool> allowed)
    {
        List<IndexedChunk> snapshot;
        lock (_gate) snapshot = [.. _chunks.Values];
        return [.. snapshot.Where(allowed)
            .Select(c => new ChunkMatch(c, Cosine(query, c.Vector)))
            .OrderByDescending(m => m.Score).ThenBy(m => m.Indexed.Chunk.Id, StringComparer.Ordinal)
            .Take(count)];
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>
/// Who asks, for the access filter (plan 27a KN09, decision SD9): the audience they work in and which subjects they may read (a role on the model, the project, the Store model). A host answers from the identity home and the roles; a test from a list.
/// </summary>
public interface IKnowledgeCaller
{
    /// <summary>The audience of the one asking (<c>front-end</c> or <c>back-end</c>), or null when not known.</summary>
    string? Audience { get; }

    /// <summary>Whether the caller may read the object a subject names (may query the model, may read the Store model, has a role on the project). <c>product</c> and <c>install</c> subjects are not asked.</summary>
    bool MayRead(KnowledgeSubject subject);
}

/// <summary>
/// The access filter (plan 27a KN09): an item is visible to someone who may read its subject; an item with no subject follows its <c>for</c>; an unknown or orphaned subject hides it (fail closed); one readable subject is enough when an item names several.
/// An agent cannot learn through knowledge what its user could not query, because it asks as its user. Hidden columns and values (KN15) wait for column rules.
/// </summary>
public static class KnowledgeAccess
{
    public static bool Visible(KnowledgeItem item, IKnowledgeCaller caller, ISubjectCatalog catalog)
    {
        if (item.About.Count == 0) return item.For.Includes(caller.Audience, null, null);
        foreach (var subject in item.About)
        {
            if (SubjectResolver.Status(catalog, subject) != SubjectStatus.Exists) continue;      // gone or never there: not a way in
            if (subject.Type is "product" or "install") return true;                              // about the install itself: for whoever the item is for
            if (caller.MayRead(subject)) return true;
        }
        return false;
    }
}

/// <summary>What a search gives back for one item: its best chunks (at most three), joined where they are next to each other, with where they came from.</summary>
public sealed record SearchHit(string ItemId, string Title, IReadOnlyList<string> HeadingPath, string Excerpt, string Source, string Version, double Score, int Chunks);

/// <summary>
/// Search over an index (plan 27a KN16's core, KN09's filter): the question is embedded, the nearest chunks are found among those the caller may see, grouped per item (at most three chunks each, best first, adjacent ones joined into one excerpt) and returned
/// with the title, heading path, source and version so an agent can cite it.
/// </summary>
public static class KnowledgeSearch
{
    public const int MaximumChunksPerItem = 3;

    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(IKnowledgeIndex index, IEmbeddingGenerator<string, Embedding<float>> embeddings, string question, IKnowledgeCaller caller, ISubjectCatalog catalog, int items = 5, CancellationToken cancellation = default)
    {
        var query = (await embeddings.GenerateAsync([question], cancellationToken: cancellation).ConfigureAwait(false))[0].Vector.ToArray();
        var visible = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Allowed(IndexedChunk c) => visible.TryGetValue(c.Item.Id, out var yes) ? yes : visible[c.Item.Id] = KnowledgeAccess.Visible(c.Item, caller, catalog);
        var matches = index.Nearest(query, items * MaximumChunksPerItem * 4, Allowed);
        return Assemble(matches, items);
    }

    /// <summary>Matches grouped per item (at most three chunks each, best first, adjacent ones joined into one excerpt), the items best first: what a search gives back whatever found the chunks (their meaning or their words).</summary>
    public static IReadOnlyList<SearchHit> Assemble(IEnumerable<ChunkMatch> matches, int items)
    {
        var hits = new List<SearchHit>();
        foreach (var group in matches.GroupBy(m => m.Indexed.Item.Id).Select(g => g.OrderByDescending(m => m.Score).Take(MaximumChunksPerItem).ToList()).OrderByDescending(g => g[0].Score).Take(items))
        {
            var item = group[0].Indexed.Item;
            var ordered = group.OrderBy(m => m.Indexed.Chunk.StartLine).ToList();
            var excerpt = new StringBuilder();
            string[]? lastPath = null;
            foreach (var match in ordered)
            {
                var path = match.Indexed.Chunk.HeadingPath.ToArray();
                if (excerpt.Length > 0) excerpt.Append(lastPath is not null && path.SequenceEqual(lastPath) ? "\n\n" : "\n\n...\n\n");   // chunks of one section are joined, others are told apart
                excerpt.Append(match.Indexed.Chunk.Text.Trim());
                lastPath = path;
            }
            hits.Add(new SearchHit(item.Id, item.Title, group[0].Indexed.Chunk.HeadingPath, excerpt.ToString(), item.File, item.Hash[..12], group[0].Score, group.Count));
        }
        return hits;
    }
}
