using System.Text.Json.Nodes;
using DbDataKit.Knowledge.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Knowledge.Cli.Tests.Samples;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>
/// Plan 27a KN22: when a new version of a semantic model arrives, the knowledge module checks each verified query about it. A query that no longer holds is marked needs re-verification (still found, no longer
/// given as verified) and its owner is told; the model is never held up; a query marked by a check that holds again is restored. On every state engine (SQLite always).
/// </summary>
public sealed class VerifiedRecheckModuleTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T10:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly List<Folder> _folders = [];

    public void Dispose() { _engines.Dispose(); foreach (var f in _folders) f.Dispose(); }

    private sealed class Caller : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => true;
    }

    private sealed record World(DefinitionPublisher Publisher, PayloadStore Payloads, KnowledgeProjectModule Module, KnowledgeStore Store, List<VerifiedNotice> Notices);

    private World Open(string engine)
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, KnowledgeModule.GroupModule], new StateScope(Group: "main"), time: new StateTestClock(Start));
        var payloads = new PayloadStore(database);
        var publisher = new DefinitionPublisher(database, KnowledgeDefinitions.Registry(), payloads, "serving");
        var store = new KnowledgeStore(database);
        var notices = new List<VerifiedNotice>();
        var module = new KnowledgeProjectModule(publisher, payloads, store, null, "prod", null, () => Catalog(), new StateTestClock(Start)) { VerifiedNotifier = notices.Add };
        return new World(publisher, payloads, module, store, notices);
    }

    private static string Query(string title, string measure, string owner = "finance-data") =>
        $"---\nknowledge: 1\nkind: verified-query\ntitle: {title}\nowner: {owner}\nmodel: Sales\nquestions:\n  - How much {title} was there by region?\nrequest: '{{\"kind\":\"query\",\"query\":{{\"measures\":[\"{measure}\"],\"attributes\":[\"region.name\"],\"limit\":10}}}}'\nverified: {{ by: alice, at: 2026-11-01, model-version: \"4\" }}\n---\nNotes.";

    private async Task<World> Served(string engine)
    {
        var w = Open(engine);
        var folder = new Folder();
        _folders.Add(folder);
        folder.With("queries/revenue.md", Query("revenue", "revenue")).With("queries/margin.md", Query("margin", "margin", "sales-ops"));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, []);
        KnowledgeDefinitions.Publish(w.Publisher, new PublishArtifacts(w.Payloads, w.Publisher, "ada"), folder.Path, "sales", "ada", "prod", report, mode: PublishMode.Serve);
        await w.Module.StageAsync("sales", 1);
        w.Module.Apply("sales", new DefinitionVersion("knowledge-project", "sales", 1, "h", "", [], "ada", Start, null, 1));
        return w;
    }

    private static JsonNode ModelCatalog(string measures) => JsonNode.Parse("""{"name":"Sales","measures":MEASURES,"dimensions":{"region":{"attributes":{"name":{"type":"string"}}}}}""".Replace("MEASURES", measures, StringComparison.Ordinal))!;

    private static DefinitionVersion Model(string measures, int version = 2) =>
        new("semantic-model", "Sales", version, "h" + version, ModelCatalog(measures).ToJsonString(), [], "ada", Start, null);

    private static string Status(World w, string id) => w.Store.Verified("prod/sales@1", id)!.Status;

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_model_change_that_drops_a_measure_marks_the_query_and_tells_its_owner_and_the_model_is_not_held_up(string engine)
    {
        var w = await Served(engine);
        Assert.Equal("verified", Status(w, "queries/revenue.md"));

        // the new version drops the measure revenue: its query is marked, the other holds, the owner of the marked one is told
        w.Module.SemanticModels.Apply("Sales", Model("""{"margin":{}}"""));

        Assert.Equal("needs-reverification", Status(w, "queries/revenue.md"));
        Assert.Equal("verified", Status(w, "queries/margin.md"));
        var notice = Assert.Single(w.Notices);
        Assert.Equal(("queries/revenue.md", "finance-data", "Sales"), (notice.ItemId, notice.Owner, notice.Model));
        Assert.Contains("the measure revenue is no longer in Sales", notice.Reason, StringComparison.Ordinal);

        // still found, by its questions, as anyone who may see the model sees it: only its status says it is not given as verified
        var found = await w.Module.Reader(new Caller(), Catalog(), null, KnowledgeSearchMode.Words).SearchAsync("how much revenue by region", "prod", null, null, CancellationToken.None);
        Assert.Contains(found.Hits, h => h.ItemId == "queries/revenue.md");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task The_same_failure_is_not_told_twice_and_a_model_that_moves_back_restores_the_query(string engine)
    {
        var w = await Served(engine);
        var without = Model("""{"margin":{}}""");

        w.Module.SemanticModels.Apply("Sales", without);
        w.Module.SemanticModels.Apply("Sales", Model("""{"margin":{}}""", 3));
        Assert.Single(w.Notices);

        w.Module.SemanticModels.Apply("Sales", Model("""{"margin":{},"revenue":{}}""", 4));
        Assert.Equal("verified", Status(w, "queries/revenue.md"));
        Assert.Null(w.Store.Verified("prod/sales@1", "queries/revenue.md")!.StatusNote);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_model_that_is_dropped_marks_its_queries_and_a_model_nobody_asked_about_marks_nothing(string engine)
    {
        var w = await Served(engine);

        w.Module.SemanticModels.Apply("Finance", Model("{}"));                  // another model: no query is about it
        Assert.Empty(w.Notices);

        w.Module.SemanticModels.Apply("Sales", null);                           // the model is no longer served
        Assert.Equal(["needs-reverification", "needs-reverification"], new[] { Status(w, "queries/revenue.md"), Status(w, "queries/margin.md") });
        Assert.All(w.Notices, n => Assert.Contains("no longer served", n.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_holds_nothing_up_and_marks_nothing()
    {
        var w = await Served("Sqlite");

        w.Module.SemanticModels.Apply("Sales", new DefinitionVersion("semantic-model", "Sales", 2, "h", "{ not json", [], "ada", Start, null));

        Assert.Empty(w.Notices);
        Assert.Equal("verified", Status(w, "queries/revenue.md"));
    }

    [Fact]
    public async Task A_version_of_the_project_staged_later_keeps_the_mark_of_a_query_that_did_not_change()
    {
        var w = await Served("Sqlite");
        w.Module.SemanticModels.Apply("Sales", Model("""{"margin":{}}"""));

        var folder = new Folder();
        _folders.Add(folder);
        folder.With("queries/revenue.md", Query("revenue", "revenue")).With("queries/margin.md", Query("margin", "margin", "sales-ops")).With("queries/orders.md", Query("orders", "margin"));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, []);
        KnowledgeDefinitions.Publish(w.Publisher, new PublishArtifacts(w.Payloads, w.Publisher, "ada"), folder.Path, "sales", "ada", "prod", report, mode: PublishMode.Stage);
        await w.Module.StageAsync("sales", 2);

        Assert.Equal("needs-reverification", w.Store.Verified("prod/sales@2", "queries/revenue.md")!.Status);
        Assert.Equal("verified", w.Store.Verified("prod/sales@2", "queries/orders.md")!.Status);
    }
}
