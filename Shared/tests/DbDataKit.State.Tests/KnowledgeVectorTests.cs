using DbDataKit.Knowledge;
using DbDataKit.State.Snapshots;
using Microsoft.Extensions.AI;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a KN07 and KN08, the vector half (owner decision 2026-11-11: words stay on every engine, vectors are the module <c>knowledge_vectors</c> for the engines that can hold them): the module on SQL Server 2025 and PostgreSQL with pgvector (the rows
/// that need an engine run on <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c>; SQLite has no vector index and is refused), ranking against the in-memory reference, the dimension check, embedding only what changed and never while saving,
/// failures that do not block, a model change that keeps answering from the old index until the new one is complete, the access filter on vectors, and an index with no vector module that still answers by words.
/// </summary>
public sealed class KnowledgeVectorTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    /// <summary>The engines that can hold vectors here.</summary>
    public static TheoryData<string> VectorEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    private sealed class Caller(string? audience, params string[] readable) : IKnowledgeCaller
    {
        public string? Audience { get; } = audience;

        public bool MayRead(KnowledgeSubject subject) => readable.Contains(subject.Key) || readable.Contains($"{subject.Type}:{subject.Name}");
    }

    /// <summary>The fake embedder with a few synonyms (turnover and income are revenue), so a question can be near a chunk it shares no word with: what a real model gives and the words alone do not.</summary>
    private sealed class Concepts(int dimensions = 64, string model = "fake-concepts") : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly Dictionary<string, string> Synonyms = new() { ["turnover"] = "revenue", ["income"] = "revenue", ["pay"] = "salary", ["wages"] = "salary", ["staff"] = "employees" };

        public FakeEmbeddingGenerator Inner { get; } = new(dimensions, model);

        public bool Down { get; set; }

        public string? Poison { get; set; }

        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var list = values.ToList();
            if (Down) throw new HttpRequestException("the embedding server is not reachable");
            if (Poison is not null && list.Any(v => v.Contains(Poison, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("the model refused this text");
            return Inner.GenerateAsync(list.Select(v => string.Join(' ', v.Split(' ', '\n').Select(w => Synonyms.GetValueOrDefault(w.ToLowerInvariant().Trim('.', ',', '?'), w)))), options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private static KnowledgeItem Item(string id, string frontMatter, string body)
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {id}\nrole: reference\nowner: data\n{frontMatter}\n---\n{body}", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private static readonly KnowledgeItem Revenue = Item("revenue.md", "about:\n  - semantic-model: Sales\n    measure: revenue", "# Revenue\n\nRevenue means net revenue: gross sales minus returns minus discounts.\n\n## Recognition\n\nRevenue is recognised when the order ships, not when it is invoiced.");
    private static readonly KnowledgeItem Region = Item("region.md", "about:\n  - semantic-model: Sales\n    dimension: region", "In 2025 the EMEA region was split into EU and MEA; older rows carry the old code.");
    private static readonly KnowledgeItem Salaries = Item("salaries.md", "about:\n  - store-model: Payroll", "Salary bands are reviewed every spring and the bands are confidential.");
    private static readonly KnowledgeItem Everyone = Item("install.md", "about:\n  - install: true", "Write dates as year month day.");

    private static SubjectCatalog Catalog() => new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Sales", "dimension", "region").Add("store-model", "Payroll");

    private sealed record Opened(StateKnowledgeIndex Index, StateDatabase Database, KnowledgeVectorStore Vectors, KnowledgeEmbedder Embedder);

    /// <summary>The module needs SQL Server 2025's vector type: the previous release (the N-1 job runs SQL Server 2022) refuses it by design, so its rows skip there.</summary>
    private static StateDatabase OpenVectors(StateEngines.Location location)
    {
        try { return location.Open([KnowledgeModule.Module, KnowledgeVectorsModule.Module], time: new StateTestClock(Start)); }
        catch (InvalidOperationException e) when (e.Message.Contains("engine feature 'vector'")) { Assert.Skip(e.Message); throw; }
    }

    private Opened Open(string engine, string channel = "main", StateEngines.Location? location = null, StateDatabase? existing = null)
    {
        var database = existing ?? OpenVectors(location ?? _engines.Provision(engine));
        var vectors = new KnowledgeVectorStore(database);
        return new Opened(new StateKnowledgeIndex(new KnowledgeStore(database), channel, vectors), database, vectors, new KnowledgeEmbedder(vectors, channel));
    }

    private static void Fill(StateKnowledgeIndex index, params KnowledgeItem[] items)
    {
        foreach (var item in items) index.Put(item, Chunker.Split(item), "project", item.Hash[..12]);
    }

    private static EmbeddingBinding Bind(Concepts generator, string? model = null) => new(generator, model ?? generator.Inner.ModelId);

    [Fact]
    public void The_module_follows_the_conventions_requires_the_vector_feature_and_sits_beside_the_knowledge_module()
    {
        Assert.Empty(StateConventions.Violations(KnowledgeVectorsModule.Module));
        Assert.Empty(StateConventions.Violations(KnowledgeVectorsModule.GroupModule));
        Assert.Equal(["vector_failure", "vector_index"], StateTableDefinitions.Read(KnowledgeVectorsModule.Module).Select(t => t.Name).Order());
        Assert.All(StateTableDefinitions.Read(KnowledgeVectorsModule.Module), t => Assert.NotEmpty(t.PrimaryKey));
        Assert.Equal([EngineFeature.Vector], KnowledgeVectorsModule.Module.RequiresFeatures);
        Assert.Equal(StateScopeKind.Group, KnowledgeVectorsModule.GroupModule.Scope);
        Assert.Equal([KnowledgeModule.Module], KnowledgeVectorsModule.Module.DependsOn);
        Assert.Equal([KnowledgeModule.GroupModule], KnowledgeVectorsModule.GroupModule.DependsOn);
        Assert.Empty(KnowledgeModule.Module.RequiresFeatures);                              // words are found on every engine
        Assert.Contains(KnowledgeVectorsModule.Module.Retention, r => r.Table == "vector_failure");
    }

    [Fact]
    public void An_engine_with_no_vector_feature_refuses_the_module_with_the_message_of_the_feature_check_and_creates_nothing()
    {
        var location = _engines.Provision(StateEngineIds.Sqlite);

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([KnowledgeModule.Module, KnowledgeVectorsModule.Module]));

        Assert.Equal(EngineFeatures.Refusal(KnowledgeVectorsModule.Module, EngineFeature.Vector, StateEngineIds.Sqlite, EngineFeatures.EngineName(StateEngineIds.Sqlite, null)), ex.Message);
        Assert.Contains("'knowledge_vectors'", ex.Message);
        Assert.Contains("To fix it:", ex.Message);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public void The_module_is_created_with_its_tables_and_a_table_per_dimension_is_made_when_a_model_is_first_bound(string engine)
    {
        var o = Open(engine);

        var index = o.Vectors.Begin("main", "ollama/nomic", 768);

        Assert.Equal(("main", "ollama/nomic", 768, "building"), (index.Channel, index.ModelId, index.Dimension, index.State));
        Assert.Null(o.Vectors.ReadyIndex("main"));
        Assert.Equal([index], o.Vectors.Indexes("main"));
        Assert.Equal(0, o.Vectors.Progress("main").Chunks);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task Nearest_chunks_rank_as_the_in_memory_reference_ranks_them(string engine)
    {
        var o = Open(engine);
        var generator = new Concepts();
        Fill(o.Index, Revenue, Region, Salaries, Everyone);

        var run = await o.Embedder.RunAsync(Bind(generator));

        var chunks = o.Index.Store.Items("main").SelectMany(i => o.Index.Store.Chunks("main", i.Id).Select(c => (Item: i.Id, Chunk: c))).ToList();
        Assert.True(run.Switched);
        Assert.Equal((chunks.Count, 0, 0), (run.Embedded, run.Failed, run.Remaining));
        var reference = new InMemoryKnowledgeIndex("fake-concepts", 64);
        foreach (var (item, chunk) in chunks)
        {
            var embedded = new IndexedChunk(new Chunk($"{item}#{chunk.Seq}", item, item, [], chunk.Text, chunk.Text, chunk.Hash, chunk.StartLine), ItemOf(item), generator.Inner.Vector(chunk.HeadingPath.Length == 0 ? chunk.Text : chunk.HeadingPath + "\n" + chunk.Text));
            reference.Put(embedded);
        }

        foreach (var question in new[] { "what is net revenue after returns", "when does the order ship", "salary bands every spring", "EMEA region split" })
        {
            var query = generator.Inner.Vector(question);
            var expected = reference.Nearest(query, 6, _ => true).Select(m => (m.Indexed.Chunk.ItemId, m.Indexed.Chunk.Id)).ToList();
            var actual = o.Vectors.Nearest("main", query, 6);

            Assert.Equal(expected.Select(e => e.ItemId), actual.Select(m => m.ItemId));
            Assert.Equal(reference.Nearest(query, 6, _ => true).Select(m => m.Score), actual.Select(m => m.Score), new ScoreComparer());
        }
    }

    private sealed class ScoreComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-4;

        public int GetHashCode(double obj) => 0;
    }

    private static KnowledgeItem ItemOf(string id) => new[] { Revenue, Region, Salaries, Everyone }.Single(i => i.Id == id);

    [Theory, MemberData(nameof(VectorEngines))]
    public void The_dimension_is_recorded_and_a_vector_of_another_size_is_refused(string engine)
    {
        var o = Open(engine);
        var index = o.Vectors.Begin("main", "m", 32);
        Assert.True(o.Vectors.Complete("main"));                                       // no chunk to embed: ready at once
        index = index with { State = "ready", ReadyAt = o.Vectors.ReadyIndex("main")!.ReadyAt };

        var again = o.Vectors.Begin("main", "m", 32);
        var different = Assert.Throws<VectorDimensionException>(() => o.Vectors.Begin("main", "m", 48));
        var wrong = Assert.Throws<VectorDimensionException>(() => o.Vectors.Put("main", index, [new ChunkVector("a", 0, "h", new float[31])]));
        var query = Assert.Throws<VectorDimensionException>(() => o.Vectors.Nearest("main", new float[5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => o.Vectors.Begin("main", "huge", 5000));

        Assert.Equal(index, again);
        Assert.Equal(KnowledgeVectorStore.DimensionMismatch("main", "m", 32, 48), different.Message);
        Assert.Contains("32-dimension", wrong.Message);
        Assert.Equal(KnowledgeVectorStore.DimensionMismatch("main", "m", 32, 31), wrong.Message);
        Assert.NotNull(query);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task Saving_an_item_never_calls_the_model_and_only_a_changed_chunk_is_embedded_again(string engine)
    {
        var o = Open(engine);
        var generator = new Concepts();
        Fill(o.Index, Revenue, Region);
        Assert.Equal(0, generator.Calls);                                               // saving is words and chunks: no model, so a slow or absent model never blocks it

        var first = await o.Embedder.RunAsync(Bind(generator));
        var embeddedAfterFirst = generator.Inner.Embedded;
        var second = await o.Embedder.RunAsync(Bind(generator));

        Assert.Equal(o.Index.Store.Counts("main").Chunks, first.Embedded);
        Assert.Equal(0, second.Embedded);                                               // nothing changed: nothing embedded
        Assert.Equal(embeddedAfterFirst, generator.Inner.Embedded);

        var changed = Item("region.md", "about:\n  - semantic-model: Sales\n    dimension: region", "Regions are now called territories after the 2026 reorganisation.");
        Fill(o.Index, changed);
        Assert.Equal(1, o.Vectors.Progress("main").Chunks - o.Vectors.Progress("main").Embedded);   // the new text has no vector yet, the old one is not returned

        var third = await o.Embedder.RunAsync(Bind(generator));

        Assert.Equal(o.Index.Store.Chunks("main", "region.md").Count, third.Embedded);  // only the changed item's chunks
        Assert.Equal(embeddedAfterFirst + third.Embedded, generator.Inner.Embedded);
        Assert.Equal(o.Index.Store.Counts("main").Chunks, o.Vectors.Progress("main").Embedded);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task A_chunk_that_changed_or_an_item_that_was_removed_has_no_vector_and_the_sweep_frees_it(string engine)
    {
        var o = Open(engine);
        var generator = new Concepts();
        Fill(o.Index, Revenue, Region);
        await o.Embedder.RunAsync(Bind(generator));
        var all = o.Vectors.Progress("main").Embedded;

        o.Index.Remove("region.md");
        var query = generator.Inner.Vector("EMEA region split");

        Assert.DoesNotContain(o.Vectors.Nearest("main", query, 20), m => m.ItemId == "region.md");     // gone with its chunk at once, joined on the hash
        Assert.Equal(all - 1, o.Vectors.Progress("main").Embedded);
        Assert.Equal(1, o.Vectors.Sweep("main"));                                                      // and its vector is removed: it lived as long as its chunk
        Assert.Equal(0, o.Vectors.Sweep("main"));
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task A_chunk_the_model_refuses_is_recorded_and_the_rest_are_embedded_and_a_down_provider_uses_up_no_attempts(string engine)
    {
        var o = Open(engine);
        var generator = new Concepts();
        var poisoned = Item("bad.md", "about:\n  - install: true", "# Bad\n\nThis text mentions xylophone which the model refuses.");
        Fill(o.Index, Revenue, poisoned);
        generator.Poison = "xylophone";

        var run = await o.Embedder.RunAsync(Bind(generator));

        Assert.Equal(1, run.Failed);
        Assert.Equal(o.Index.Store.Counts("main").Chunks - 1, run.Embedded);
        Assert.Null(run.Problem);
        var failure = Assert.Single(o.Vectors.Failures("main"));
        Assert.Equal(("bad.md", 1), (failure.ItemId, failure.Attempts));
        Assert.Equal(generator.Inner.ModelId, failure.ModelId);
        Assert.Contains("refused", failure.Message);
        Assert.False(run.Switched);                                                                    // one chunk is still to be tried

        await o.Embedder.RunAsync(Bind(generator));
        var last = await o.Embedder.RunAsync(Bind(generator));
        Assert.Equal(KnowledgeVectorStore.MaximumAttempts, o.Vectors.Failures("main")[0].Attempts);
        Assert.True(last.Switched);                                                                    // left to the words, the rest is ready
        Assert.Equal(0, last.Remaining);
        Assert.Equal(["bad.md"], (await o.Index.FindAsync("xylophone refuses", new Caller("back-end"), Catalog(), KnowledgeSearchMode.Words, null, 5)).Hits.Select(h => h.ItemId));
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task A_provider_that_goes_down_midway_is_told_in_the_run_and_no_chunk_is_given_up_on(string engine)
    {
        var o = Open(engine);
        var generator = new Concepts();
        Fill(o.Index, Revenue, Region, Salaries);
        await o.Embedder.RunAsync(Bind(generator), maximum: 1);                                        // one chunk done, the model is known
        generator.Down = true;

        var run = await o.Embedder.RunAsync(Bind(generator));

        Assert.NotNull(run.Problem);
        Assert.Contains("not reachable", run.Problem);
        Assert.Equal(0, run.Embedded);
        Assert.All(o.Vectors.Failures("main"), f => Assert.Equal(0, f.Attempts));                      // the outage is shown, and no chunk was used up by it
        generator.Down = false;
        var after = await o.Embedder.RunAsync(Bind(generator));
        Assert.Null(after.Problem);
        Assert.True(after.Switched);
        Assert.Empty(o.Vectors.Failures("main"));
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task Words_and_meaning_together_find_what_either_alone_misses_under_the_same_access_filter(string engine)
    {
        var o = Open(engine);
        var generator = new Concepts();
        Fill(o.Index, Revenue, Region, Salaries, Everyone);
        await o.Embedder.RunAsync(Bind(generator));
        EmbeddingResolver resolve = model => model == "fake-concepts" ? generator : null;
        var finance = new Caller("back-end", "semantic-model:Sales", "store-model:Payroll");
        var sales = new Caller("back-end", "semantic-model:Sales");

        var words = await o.Index.FindAsync("what is our turnover", sales, Catalog(), KnowledgeSearchMode.Words, resolve);
        var meaning = await o.Index.FindAsync("what is our turnover", sales, Catalog(), KnowledgeSearchMode.Vectors, resolve);
        var both = await o.Index.FindAsync("what is our turnover", sales, Catalog(), KnowledgeSearchMode.Both, resolve);

        Assert.Empty(words.Hits);                                                                      // no chunk has the word turnover
        Assert.Equal("revenue.md", meaning.Hits[0].ItemId);                                            // the model knows it means revenue
        Assert.Equal(KnowledgeSearchMode.Vectors, meaning.Used);
        Assert.Equal("revenue.md", both.Hits[0].ItemId);
        Assert.Equal(KnowledgeSearchMode.Both, both.Used);
        Assert.Null(both.Note);

        // a chunk found by both rankings is above one found by either alone
        var fused = await o.Index.FindAsync("revenue returns discounts", sales, Catalog(), KnowledgeSearchMode.Both, resolve);
        Assert.Equal("revenue.md", fused.Hits[0].ItemId);
        Assert.True(fused.Hits[0].Score > 1.0 / (StateKnowledgeIndex.FusionConstant + 1));   // above what one ranking alone can give

        // the access filter is the one of the words: a person who cannot read the Payroll model never finds its item, however near
        var hidden = await o.Index.FindAsync("what do staff pay", sales, Catalog(), KnowledgeSearchMode.Vectors, resolve, 10);
        var shown = await o.Index.FindAsync("what do staff pay", finance, Catalog(), KnowledgeSearchMode.Vectors, resolve, 10);
        Assert.DoesNotContain(hidden.Hits, h => h.ItemId == "salaries.md");
        Assert.Contains(shown.Hits, h => h.ItemId == "salaries.md");
        Assert.Equal("salaries.md", (await o.Index.FindAsync("wages", finance, Catalog(), KnowledgeSearchMode.Both, resolve)).Hits[0].ItemId);
        Assert.DoesNotContain((await o.Index.FindAsync("wages", sales, Catalog(), KnowledgeSearchMode.Vectors, resolve, 10)).Hits, h => h.ItemId == "salaries.md");
        Assert.DoesNotContain((await o.Index.FindAsync("wages", finance, new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue"), KnowledgeSearchMode.Vectors, resolve, 10)).Hits, h => h.ItemId == "salaries.md");   // the model is gone
    }

    [Fact]
    public void Fusing_two_rankings_adds_reciprocal_ranks()
    {
        TextMatch M(string id, int seq = 0) => new(id, seq, 0);

        var fused = StateKnowledgeIndex.Fuse([M("a"), M("b"), M("c")], [M("c"), M("a"), M("d")], 10);

        Assert.Equal(["a", "c", "b", "d"], fused.Select(m => m.ItemId));
        Assert.Equal(1.0 / 61 + 1.0 / 62, fused[0].Score, 9);
        Assert.Equal(1.0 / 63 + 1.0 / 61, fused[1].Score, 9);
        Assert.Equal(2, StateKnowledgeIndex.Fuse([M("a"), M("b"), M("c")], [], 2).Count);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task A_model_change_builds_beside_the_old_index_which_answers_until_the_new_one_is_complete_and_then_the_switch_is_at_once(string engine)
    {
        var o = Open(engine);
        var old = new Concepts(64, "fake-old");
        var next = new Concepts(48, "fake-new");
        Fill(o.Index, Revenue, Region, Salaries, Everyone);
        await o.Embedder.RunAsync(Bind(old));
        var total = o.Index.Store.Counts("main").Chunks;
        EmbeddingResolver resolve = model => model switch { "fake-old" => old, "fake-new" => next, _ => null };
        var caller = new Caller("back-end", "semantic-model:Sales", "store-model:Payroll");

        var partial = await o.Embedder.RunAsync(Bind(next), maximum: 2);                               // another model is bound: a second index starts

        Assert.Equal(2, partial.Embedded);
        Assert.False(partial.Switched);
        var during = o.Vectors.Progress("main");
        Assert.Equal(("fake-old", 64, "ready"), (during.Ready!.ModelId, during.Ready.Dimension, during.Ready.State));
        Assert.Equal(("fake-new", 48, "building"), (during.Building!.ModelId, during.Building.Dimension, during.Building.State));
        Assert.Equal((total, 2), (during.Embedded, during.BuildingEmbedded));
        var whileBuilding = await o.Index.FindAsync("what is our turnover", caller, Catalog(), KnowledgeSearchMode.Vectors, resolve);
        Assert.Equal("revenue.md", whileBuilding.Hits[0].ItemId);                                      // still answered from the old index, embedded with the old model
        Assert.Equal(KnowledgeSearchMode.Vectors, whileBuilding.Used);

        var rest = await o.Embedder.RunAsync(Bind(next));

        Assert.True(rest.Switched);
        var after = o.Vectors.Progress("main");
        Assert.Equal(("fake-new", 48), (after.Ready!.ModelId, after.Ready.Dimension));
        Assert.Null(after.Building);
        Assert.Equal((total, 0), (after.Embedded, after.BuildingEmbedded));
        Assert.Single(o.Vectors.Indexes("main"));                                                      // the old index and its vectors are gone in the same step
        var now = await o.Index.FindAsync("what is our turnover", caller, Catalog(), KnowledgeSearchMode.Vectors, resolve);
        Assert.Equal("revenue.md", now.Hits[0].ItemId);

        // the old model can no longer embed a question for the new index, and the search says so and answers by words
        var gone = await o.Index.FindAsync("revenue", caller, Catalog(), KnowledgeSearchMode.Both, model => model == "fake-old" ? old : null);
        Assert.Equal(KnowledgeSearchMode.Words, gone.Used);
        Assert.Contains("fake-new", gone.Note);
        Assert.Equal("revenue.md", gone.Hits[0].ItemId);

        // binding the first model again while the new one is ready starts a build for it; binding the ready one again abandons a build
        await o.Embedder.RunAsync(Bind(old), maximum: 1);
        Assert.NotNull(o.Vectors.Progress("main").Building);
        await o.Embedder.RunAsync(Bind(next));
        Assert.Null(o.Vectors.Progress("main").Building);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task Channels_have_indexes_of_their_own(string engine)
    {
        var database = OpenVectors(_engines.Provision(engine));
        var main = Open(engine, "main", existing: database);
        var test = Open(engine, "test", existing: database);
        var generator = new Concepts();
        Fill(main.Index, Revenue);
        Fill(test.Index, Region);

        await main.Embedder.RunAsync(Bind(generator));

        Assert.NotNull(main.Vectors.ReadyIndex("main"));
        Assert.Null(test.Vectors.ReadyIndex("test"));
        Assert.Empty(test.Vectors.Nearest("test", generator.Inner.Vector("revenue"), 10));
        Assert.Equal("revenue.md", main.Vectors.Nearest("main", generator.Inner.Vector("revenue"), 10)[0].ItemId);
        await test.Embedder.RunAsync(Bind(generator));
        Assert.DoesNotContain(test.Vectors.Nearest("test", generator.Inner.Vector("revenue"), 10), m => m.ItemId == "revenue.md");
        main.Vectors.Drop("main");
        Assert.Empty(main.Vectors.Indexes("main"));
        Assert.NotNull(test.Vectors.ReadyIndex("test"));
    }

    [Theory, MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task An_index_with_no_vector_module_still_answers_by_words_and_says_why(string engine)
    {
        var database = _engines.Provision(engine).Open([KnowledgeModule.Module], time: new StateTestClock(Start));
        var index = new StateKnowledgeIndex(new KnowledgeStore(database), "main");
        Fill(index, Revenue, Region);

        var both = await index.FindAsync("net revenue returns", new Caller("back-end", "semantic-model:Sales"), Catalog(), KnowledgeSearchMode.Both, _ => new Concepts());

        Assert.Equal(KnowledgeSearchMode.Words, both.Used);
        Assert.Contains("no vector module", both.Note);
        Assert.Equal("revenue.md", both.Hits[0].ItemId);
        Assert.Null(index.Status().Vectors);
    }

    [Theory, MemberData(nameof(VectorEngines))]
    public async Task With_the_module_but_no_vector_index_yet_the_search_answers_by_words(string engine)
    {
        var o = Open(engine);
        Fill(o.Index, Revenue);
        var caller = new Caller("back-end", "semantic-model:Sales");

        var none = await o.Index.FindAsync("net revenue", caller, Catalog(), KnowledgeSearchMode.Both, _ => new Concepts());
        Assert.Equal(KnowledgeSearchMode.Words, none.Used);
        Assert.Contains("No vector index", none.Note);
        Assert.Equal("revenue.md", none.Hits[0].ItemId);

        var generator = new Concepts();
        await o.Embedder.RunAsync(Bind(generator), maximum: 0);
        var unready = await o.Index.FindAsync("net revenue", caller, Catalog(), KnowledgeSearchMode.Both, _ => generator);
        Assert.Contains("still being built", unready.Note);

        var failing = new Concepts();
        await o.Embedder.RunAsync(Bind(generator));
        failing.Down = true;
        var down = await o.Index.FindAsync("net revenue", caller, Catalog(), KnowledgeSearchMode.Both, _ => failing);
        Assert.Equal(KnowledgeSearchMode.Words, down.Used);
        Assert.Contains("could not be embedded", down.Note);
        Assert.Equal("revenue.md", down.Hits[0].ItemId);
    }
}
