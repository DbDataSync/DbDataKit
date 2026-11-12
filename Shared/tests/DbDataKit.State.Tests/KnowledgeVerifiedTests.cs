using DbDataKit.Knowledge;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a KN21 and KN22: what the <c>knowledge</c> module keeps of a verified query (the second migration), on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and
/// <c>DBDATAKIT_TEST_POSTGRES</c> name them): the request and who verified it are stored with the item and removed with it, the questions are what is found, a status can be set and is kept when the item did not change, and an item
/// that changed is written again as verified.
/// </summary>
public sealed class KnowledgeVerifiedTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private static KnowledgeItem Query(string id, string question, string model = "Sales", string limit = "10")
    {
        var text = $"---\nknowledge: 1\nkind: verified-query\ntitle: {id}\nowner: data\nmodel: {model}\nquestions:\n  - {question}\nrequest:\n  kind: query\n  query:\n    measures: [revenue]\n    limit: {limit}\nverified: {{ by: alice, at: 2026-11-01, model-version: \"4\" }}\n---\nNotes.";
        var result = KnowledgeItemLoader.Load(text, id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private StateKnowledgeIndex Open(string engine, string channel = "main") =>
        new(new KnowledgeStore(_engines.Provision(engine).Open([KnowledgeModule.Module], time: new StateTestClock(Start))), channel);

    private static void Fill(StateKnowledgeIndex index, params KnowledgeItem[] items)
    {
        foreach (var item in items) index.Put(item, Chunker.Split(item), "project", item.Hash[..12]);
    }

    [Fact]
    public void The_module_has_a_table_for_verified_queries_and_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(KnowledgeModule.Module));
        Assert.Equal(["channel", "item_id"], StateTableDefinitions.Read(KnowledgeModule.Module).Single(t => t.Name == "verified_query").PrimaryKey);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_verified_query_keeps_its_request_and_who_verified_it_beside_the_item(string engine)
    {
        var index = Open(engine);
        Fill(index, Query("revenue-by-region.md", "What was revenue by region last quarter?"));

        var stored = index.Store.Verified("main", "revenue-by-region.md")!;

        Assert.Equal("Sales", stored.Model);
        Assert.Contains("\"measures\"", stored.Request, StringComparison.Ordinal);
        Assert.Equal(("alice", "2026-11-01", "4"), (stored.VerifiedBy, stored.VerifiedAt, stored.ModelVersion));
        Assert.Equal(VerifiedStatus.Verified, stored.Status);
        // the questions are what is searched
        Assert.NotEmpty(index.Store.Search("main", "revenue by region last quarter"));
        Assert.Null(index.Store.Verified("main", "nothing.md"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Verified_queries_are_listed_per_model_and_per_channel(string engine)
    {
        var index = Open(engine);
        Fill(index, Query("a.md", "Revenue by region?"), Query("b.md", "Budget by month?", model: "Finance"));
        var other = new StateKnowledgeIndex(index.Store, "test");
        Fill(other, Query("c.md", "Revenue in the test channel?"));

        Assert.Equal(["a.md", "b.md"], index.Store.VerifiedQueries("main").Select(v => v.ItemId));
        Assert.Equal(["b.md"], index.Store.VerifiedQueries("main", "Finance").Select(v => v.ItemId));
        Assert.Equal(["c.md"], index.Store.VerifiedQueries("test").Select(v => v.ItemId));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_status_is_kept_when_the_item_did_not_change_and_reset_when_it_did(string engine)
    {
        var index = Open(engine);
        var item = Query("a.md", "Revenue by region?");
        Fill(index, item);
        Assert.True(index.Store.SetVerifiedStatus("main", "a.md", VerifiedStatus.NeedsReverification, "the measure revenue is gone"));

        Fill(index, item);   // the same text again: nothing is written
        var kept = index.Store.Verified("main", "a.md")!;
        Assert.Equal(VerifiedStatus.NeedsReverification, kept.Status);
        Assert.Equal("the measure revenue is gone", kept.StatusNote);

        Fill(index, Query("a.md", "Revenue by region?", limit: "20"));   // a person verified it again
        Assert.Equal(VerifiedStatus.Verified, index.Store.Verified("main", "a.md")!.Status);
        Assert.False(index.Store.SetVerifiedStatus("main", "nothing.md", VerifiedStatus.Verified, null));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Removing_the_item_removes_what_is_kept_of_the_query(string engine)
    {
        var index = Open(engine);
        Fill(index, Query("a.md", "Revenue by region?"));

        Assert.True(index.Remove("a.md"));

        Assert.Null(index.Store.Verified("main", "a.md"));
        Assert.Empty(index.Store.VerifiedQueries("main"));
    }
}
