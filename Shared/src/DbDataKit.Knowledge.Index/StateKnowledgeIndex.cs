using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Knowledge;

/// <summary>What the index of a channel holds and how it is searched (for the console's knowledge screens).</summary>
public sealed record KnowledgeIndexStatus(string Channel, int Items, int Chunks, TextSearchMode Mode);

/// <summary>
/// The knowledge index of one channel on a state database (plan 27a KN07, the text-search half): <see cref="Put"/> chunks an item's rows into the <c>knowledge</c> module, <see cref="SearchAsync"/> finds chunks by their words and applies the
/// same access filter as the in-memory index (<see cref="KnowledgeAccess"/>, decision SD9) to each item at the time of the question, then assembles the hits as <see cref="KnowledgeSearch"/> does. An item that has not changed is not written again.
/// The ranking is the engine's (bm25 on SQLite, <c>ts_rank_cd</c> on PostgreSQL, a count of matched words elsewhere): the order of a few chunks is reproducible on every engine, the numbers are not comparable between them.
/// </summary>
public sealed class StateKnowledgeIndex(KnowledgeStore store, string channel)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Channel { get; } = channel;

    public KnowledgeStore Store { get; } = store;

    public KnowledgeIndexStatus Status()
    {
        var (items, chunks) = Store.Counts(Channel);
        return new KnowledgeIndexStatus(Channel, items, chunks, Store.Mode);
    }

    /// <summary>Indexes an item with the chunks it was split into. False when the item was already there with the same hash (nothing is written).</summary>
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
        var found = Store.Search(Channel, question, items * KnowledgeSearch.MaximumChunksPerItem * 4);
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
        return Task.FromResult(KnowledgeSearch.Assemble(matches, items));
    }
}
