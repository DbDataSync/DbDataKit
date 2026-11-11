using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN05 (the deterministic fake embedding) and KN09 (the access filter), on the in-memory index that every engine's ranking is compared with.</summary>
public sealed class SearchTests
{
    private sealed class Caller(string? audience, params string[] readable) : IKnowledgeCaller
    {
        public string? Audience { get; } = audience;

        public HashSet<string> Readable { get; } = [.. readable];

        public bool MayRead(KnowledgeSubject subject) => Readable.Contains(subject.Key) || Readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private static KnowledgeItem Item(string id, string frontMatter, string body)
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {id}\nrole: reference\nowner: data\n{frontMatter}\n---\n{body}", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private static readonly KnowledgeItem Revenue = Item("revenue.md", "about:\n  - semantic-model: Sales\n    measure: revenue", "Revenue means net revenue: gross sales minus returns minus discounts.");
    private static readonly KnowledgeItem Region = Item("region.md", "about:\n  - semantic-model: Sales\n    dimension: region", "In 2025 the EMEA region was split into EU and MEA; older rows carry the old code.");
    private static readonly KnowledgeItem Salaries = Item("salaries.md", "about:\n  - store-model: Payroll", "Salary bands are reviewed every spring and the bands are confidential.");
    private static readonly KnowledgeItem Glossary = Item("glossary.md", "for: { audience: front-end }", "A churned customer is one who has not ordered for ninety days.");
    private static readonly KnowledgeItem Everyone = Item("install.md", "about:\n  - install: true", "Write dates as year month day.");

    private static SubjectCatalog Catalog() => new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Sales", "dimension", "region").Add("store-model", "Payroll");

    private static async Task<(InMemoryKnowledgeIndex Index, FakeEmbeddingGenerator Embeddings)> Indexed(params KnowledgeItem[] items)
    {
        var embeddings = new FakeEmbeddingGenerator();
        var index = new InMemoryKnowledgeIndex(embeddings.ModelId, embeddings.Dimensions);
        foreach (var item in items)
            foreach (var chunk in Chunker.Split(item))
                index.Put(new IndexedChunk(chunk, item, (await embeddings.GenerateAsync([chunk.EmbeddedText]))[0].Vector.ToArray()));
        return (index, embeddings);
    }

    [Fact]
    public async Task The_fake_embedding_is_deterministic_normalised_and_puts_texts_that_share_words_near_each_other()
    {
        var a = new FakeEmbeddingGenerator();
        var b = new FakeEmbeddingGenerator();

        var v1 = a.Vector("net revenue of the Sales model");
        Assert.Equal(v1, b.Vector("net revenue of the Sales model"));                       // the same on every run
        Assert.Equal(v1, a.Vector("NET REVENUE of the sales MODEL"));                       // case does not matter
        Assert.Equal(1.0, Math.Sqrt(v1.Sum(x => (double)x * x)), 6);                        // unit length
        Assert.Equal(64, v1.Length);
        Assert.True(InMemoryKnowledgeIndex.Cosine(v1, a.Vector("net revenue sales")) > InMemoryKnowledgeIndex.Cosine(v1, a.Vector("holiday calendar for staff")));
        Assert.All(new float[64], x => Assert.Equal(0f, x));
        Assert.Equal(new float[64], a.Vector(""));                                          // no words, no direction
        var batch = await a.GenerateAsync(["one", "two", "three"]);
        Assert.Equal(3, batch.Count);
        Assert.Equal("fake-embedding", batch[0].ModelId);
    }

    [Fact]
    public async Task A_search_ranks_the_item_about_the_question_first()
    {
        var (index, embeddings) = await Indexed(Revenue, Region, Everyone);

        var hits = await KnowledgeSearch.SearchAsync(index, embeddings, "what does revenue mean net of returns", new Caller("back-end", "semantic-model:Sales"), Catalog());

        Assert.Equal("revenue.md", hits[0].ItemId);
        Assert.Equal("revenue.md", hits[0].Title);
        Assert.Equal(Revenue.Hash[..12], hits[0].Version);
        Assert.Equal("revenue.md", hits[0].Source);
        Assert.True(hits[0].Score > hits.Last().Score || hits.Count == 1);
    }

    [Fact]
    public async Task A_person_who_cannot_query_a_model_neither_finds_nor_is_shown_its_items_and_two_people_get_two_result_sets()
    {
        var (index, embeddings) = await Indexed(Revenue, Region, Salaries, Everyone);
        var analyst = new Caller("back-end", "semantic-model:Sales");
        var hr = new Caller("back-end", "store-model:Payroll");
        var nobody = new Caller("back-end");

        async Task<string[]> Ids(Caller who) => [.. (await KnowledgeSearch.SearchAsync(index, embeddings, "revenue region salary bands", who, Catalog())).Select(h => h.ItemId).Order()];

        Assert.Equal(["install.md", "region.md", "revenue.md"], await Ids(analyst));
        Assert.Equal(["install.md", "salaries.md"], await Ids(hr));
        Assert.Equal(["install.md"], await Ids(nobody));                                    // the install's own text is for whoever it is for
    }

    [Fact]
    public async Task A_removed_role_hides_the_item_at_once_without_a_rebuild()
    {
        var (index, embeddings) = await Indexed(Salaries);
        var caller = new Caller("back-end", "store-model:Payroll");
        Assert.Single(await KnowledgeSearch.SearchAsync(index, embeddings, "salary bands", caller, Catalog()));

        caller.Readable.Clear();

        Assert.Empty(await KnowledgeSearch.SearchAsync(index, embeddings, "salary bands", caller, Catalog()));
        Assert.Equal(1, index.Count);                                                       // the index was not touched
    }

    [Fact]
    public async Task An_orphaned_or_unknown_subject_hides_the_item_and_an_item_with_no_subject_follows_its_for()
    {
        var (index, embeddings) = await Indexed(Revenue, Glossary);
        var frontEnd = new Caller("front-end", "semantic-model:Sales");
        var backEnd = new Caller("back-end", "semantic-model:Sales");

        Assert.Equal(["glossary.md", "revenue.md"], (await KnowledgeSearch.SearchAsync(index, embeddings, "revenue churned customer", frontEnd, Catalog())).Select(h => h.ItemId).Order());
        Assert.Equal(["revenue.md"], (await KnowledgeSearch.SearchAsync(index, embeddings, "revenue churned customer", backEnd, Catalog())).Select(h => h.ItemId));   // the glossary is for front-end people

        var dropped = Catalog().Retire("semantic-model", "Sales");
        Assert.Equal(["glossary.md"], (await KnowledgeSearch.SearchAsync(index, embeddings, "revenue churned customer", frontEnd, dropped)).Select(h => h.ItemId));          // fail closed
        Assert.Equal(["glossary.md"], (await KnowledgeSearch.SearchAsync(index, embeddings, "revenue churned customer", frontEnd, new SubjectCatalog())).Select(h => h.ItemId));
    }

    [Fact]
    public async Task One_item_gives_at_most_three_chunks_best_first_with_the_heading_path_and_neighbours_joined()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"## Step {i}\n\nBackfill step {i}: " + string.Join(' ', Enumerable.Repeat($"backfill range {i} day", 60))));
        var howto = Item("howto.md", "about:\n  - install: true", "# Backfill\n\n" + body);
        var embeddings = new FakeEmbeddingGenerator();
        var index = new InMemoryKnowledgeIndex(embeddings.ModelId, embeddings.Dimensions);
        var options = new ChunkerOptions(TargetTokens: 100);
        foreach (var chunk in Chunker.Split(howto, options))
            index.Put(new IndexedChunk(chunk, howto, (await embeddings.GenerateAsync([chunk.EmbeddedText]))[0].Vector.ToArray()));
        Assert.True(index.Count > 3);

        var hits = await KnowledgeSearch.SearchAsync(index, embeddings, "backfill range 3 day", new Caller("back-end"), new SubjectCatalog());

        var hit = Assert.Single(hits);
        Assert.True(hit.Chunks <= KnowledgeSearch.MaximumChunksPerItem);
        Assert.Equal(["Backfill", "Step 3"], hit.HeadingPath);                              // the best chunk's place, for citing
        Assert.Contains("Backfill step 3", hit.Excerpt);
    }

    [Fact]
    public void The_index_holds_one_model_and_refuses_a_vector_of_another_size()
    {
        var index = new InMemoryKnowledgeIndex("model-a", 8);
        var chunk = Chunker.Split(Revenue)[0];

        Assert.Throws<ArgumentException>(() => index.Put(new IndexedChunk(chunk, Revenue, new float[16])));
        index.Put(new IndexedChunk(chunk, Revenue, new float[8]));
        index.RemoveItem("revenue.md");
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public async Task Only_the_chunks_that_changed_are_embedded_again()
    {
        var embeddings = new FakeEmbeddingGenerator();
        var before = Chunker.Split(Item("howto.md", "about:\n  - install: true", "# T\n\n## A\n\n" + string.Join(' ', Enumerable.Repeat("alpha beta gamma delta.", 40)) + "\n\n## B\n\n" + string.Join(' ', Enumerable.Repeat("epsilon zeta eta theta.", 40))), new ChunkerOptions(TargetTokens: 100));
        var after = Chunker.Split(Item("howto.md", "about:\n  - install: true", "# T\n\n## A\n\n" + string.Join(' ', Enumerable.Repeat("alpha beta gamma delta.", 40)) + "\n\n## B\n\n" + string.Join(' ', Enumerable.Repeat("epsilon zeta eta THETA.", 40))), new ChunkerOptions(TargetTokens: 100));

        var changed = Chunker.Changed(after, before.Select(c => c.Hash).ToHashSet());
        await embeddings.GenerateAsync(changed.Select(c => c.EmbeddedText));

        Assert.NotEmpty(changed);
        Assert.All(changed, c => Assert.Contains("B", c.HeadingPath));                      // section A did not change
        Assert.Equal(changed.Count, embeddings.Embedded);
        Assert.True(embeddings.Embedded < after.Count);
    }
}
