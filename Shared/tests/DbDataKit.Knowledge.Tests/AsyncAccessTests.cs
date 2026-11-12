using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>The asynchronous access beside the synchronous caller (plan 27a's open item): the adapter answers as the caller does and finishes without waiting, and the search applies the asynchronous check after the normal one.</summary>
public sealed class AsyncAccessTests
{
    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    /// <summary>Handles one subject type and really waits for its answer: the only place in the path that does.</summary>
    private sealed class SlowReports(params string[] allowed) : IAsyncKnowledgeAccess
    {
        public int Asked;

        public bool Handles(KnowledgeSubject subject) => subject.Type == "report-catalog";

        public async ValueTask<bool> MayReadAsync(KnowledgeSubject subject, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Asked);
            await Task.Delay(5, cancellationToken);
            return allowed.Contains(subject.NarrowingName);
        }
    }

    private static KnowledgeItem Item(string id, string about, string body)
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {id}\nrole: reference\nowner: data\nabout:\n{about}\n---\n{body}", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private static readonly KnowledgeItem Revenue = Item("revenue.md", "  - semantic-model: Sales", "Revenue means net revenue for the report catalog dashboards.");
    private static readonly KnowledgeItem DashA = Item("a.md", "  - report-catalog: views\n    item: dashboard/a", "Dashboard a shows revenue by region.");
    private static readonly KnowledgeItem DashB = Item("b.md", "  - report-catalog: views\n    item: dashboard/b", "Dashboard b shows revenue by month.");

    private static SubjectCatalog Catalog() => new SubjectCatalog().Add("semantic-model", "Sales").Add("report-catalog", "views", "item", "dashboard/a").Add("report-catalog", "views", "item", "dashboard/b");

    private static async Task<(InMemoryKnowledgeIndex, FakeEmbeddingGenerator)> Indexed(params KnowledgeItem[] items)
    {
        var embeddings = new FakeEmbeddingGenerator();
        var index = new InMemoryKnowledgeIndex(embeddings.ModelId, embeddings.Dimensions);
        foreach (var item in items)
            foreach (var chunk in Chunker.Split(item))
                index.Put(new IndexedChunk(chunk, item, (await embeddings.GenerateAsync([chunk.EmbeddedText]))[0].Vector.ToArray()));
        return (index, embeddings);
    }

    [Fact]
    public async Task The_adapter_answers_as_the_synchronous_caller_does_and_is_already_complete()
    {
        var access = new SyncKnowledgeAccess(new Caller("semantic-model:Sales"));
        var yes = new KnowledgeSubject("semantic-model", ["Sales"]);
        var no = new KnowledgeSubject("store-model", ["Payroll"]);
        Assert.True(access.Handles(no));
        var read = access.MayReadAsync(yes);
        Assert.True(read.IsCompletedSuccessfully);
        Assert.True(await read);
        Assert.False(await access.MayReadAsync(no));
        Assert.False(await new CompositeKnowledgeAccess().MayReadAsync(yes));   // nobody handles it: closed
    }

    [Fact]
    public async Task The_async_check_decides_only_the_items_about_what_it_handles_after_the_normal_check_and_asks_once_per_item()
    {
        var (index, embeddings) = await Indexed(Revenue, DashA, DashB);
        var reports = new SlowReports("dashboard/a");
        var hits = await KnowledgeSearch.SearchAsync(index, embeddings, "revenue dashboard region month", new Caller("semantic-model:Sales"), reports, Catalog());
        Assert.Equal(["a.md", "revenue.md"], hits.Select(h => h.ItemId).Order());
        Assert.Equal(2, reports.Asked);                                                   // both dashboards asked, once each; the model item is the caller's

        // without the caller's right to the model, the item about the model stays hidden, the async one is unaffected
        var other = await KnowledgeSearch.SearchAsync(index, embeddings, "revenue dashboard region month", new Caller(), new SlowReports("dashboard/b"), Catalog());
        Assert.Equal(["b.md"], other.Select(h => h.ItemId));
    }
}
