using System.Text;
using DbDataKit.Knowledge.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Knowledge.Cli.Tests.Samples;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>
/// Plan 27a KN11, the module of the serving layer as a library, on the state database of every engine (SQLite, and SQL Server 2025 and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> / <c>DBDATAKIT_TEST_POSTGRES</c> name a server): a version has an index of its own, staging
/// embeds only what changed, a search during activation never sees half a version, a rollback restores the previous items and index, and the health reports the model and the counts.
/// </summary>
public sealed class KnowledgeModuleTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T10:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly List<Folder> _folders = [];

    public void Dispose() { _engines.Dispose(); foreach (var f in _folders) f.Dispose(); }

    public static TheoryData<string> VectorEngines => [StateEngineIds.MsSql, StateEngineIds.Postgres];

    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private sealed record World(DefinitionPublisher Publisher, PayloadStore Payloads, KnowledgeProjectModule Module, FakeEmbeddingGenerator? Embedder, KnowledgeStore Store);

    private World Open(string engine, bool vectors)
    {
        var modules = vectors ? new[] { DefinitionsModule.Module, PayloadStore.Module, KnowledgeModule.GroupModule, KnowledgeVectorsModule.GroupModule } : [DefinitionsModule.Module, PayloadStore.Module, KnowledgeModule.GroupModule];
        var database = _engines.Provision(engine).Open(modules, new StateScope(Group: "main"), time: new StateTestClock(Start));
        var payloads = new PayloadStore(database);
        var publisher = new DefinitionPublisher(database, KnowledgeDefinitions.Registry(), payloads, "serving");
        var embedder = vectors ? new FakeEmbeddingGenerator(32, "fake-1") : null;
        var store = new KnowledgeStore(database);
        var module = new KnowledgeProjectModule(publisher, payloads, store, vectors ? new KnowledgeVectorStore(database) : null, "prod",
            embedder is null ? null : () => new EmbeddingBinding(embedder, "fake/fake-1"), () => Catalog(), new StateTestClock(Start));
        return new World(publisher, payloads, module, embedder, store);
    }

    private Folder Project(params (string Path, string Title, string Body)[] items)
    {
        var folder = new Folder();
        _folders.Add(folder);
        foreach (var (path, title, body) in items) folder.With(path, Item(title, body, role: "reference"));
        return folder;
    }

    private static void Publish(World w, Folder folder, string name = "sales", PublishMode mode = PublishMode.Stage)
    {
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, []);
        KnowledgeDefinitions.Publish(w.Publisher, new PublishArtifacts(w.Payloads, w.Publisher, "ada"), folder.Path, name, "ada", "prod", report, mode: mode);
    }

    private static async Task<string[]> Found(World w, string question, params string[] readable)
    {
        var result = await w.Module.Reader(new Caller(readable.Length == 0 ? ["semantic-model:Sales"] : readable), Catalog(), null, KnowledgeSearchMode.Words).SearchAsync(question, "prod", null, null, default);
        return [.. result.Hits.Select(h => h.ItemId)];
    }

    private static readonly (string, string, string) Revenue = ("revenue.md", "Revenue", "# Revenue\n\nRevenue means net revenue after returns and discounts.");
    private static readonly (string, string, string) Margin = ("margin.md", "Margin", "# Margin\n\nMargin is revenue less the cost of goods.");
    private static readonly (string, string, string) Orders = ("orders.md", "Orders", "# Orders\n\nAn order is counted when it ships, not when it is placed.");

    // ---- words, on every engine ----

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_version_is_staged_beside_the_served_one_and_a_search_sees_the_whole_of_one_version_never_half_of_two(string engine)
    {
        var w = Open(engine, vectors: false);
        Publish(w, Project(Revenue, Margin, Orders), mode: PublishMode.Serve);
        w.Module.Apply("sales", Version(w, 1));
        Assert.Equal(["revenue.md"], await Found(w, "returns discounts"));

        // version 2 changes one item and drops another: staged, it is written beside the first and the first keeps serving
        var folder = Project(Revenue with { Item3 = "# Revenue\n\nRevenue means net revenue after returns, discounts and VAT." }, Margin);
        Publish(w, folder);
        var staged = await w.Module.StageAsync("sales", 2);
        Assert.Equal((2, 1), (staged.Items, staged.Unchanged));
        Assert.Equal(1, w.Publisher.Head("knowledge-project", "sales")!.Active);
        Assert.Empty(await Found(w, "VAT"));                                  // the served version does not say it yet
        Assert.Equal(["orders.md"], await Found(w, "ships placed"));          // the dropped item is still served

        // activation is the head moving: the whole of version 2 at once
        w.Publisher.Activate("knowledge-project", "sales", "ada");
        Assert.Equal(["revenue.md"], await Found(w, "VAT"));
        Assert.Empty(await Found(w, "ships placed"));

        // a rollback is the head moving back to an index that is still there: the previous items, with no staging
        w.Publisher.Rollback("knowledge-project", "sales", "ada");
        Assert.Empty(await Found(w, "VAT"));
        Assert.Equal(["orders.md"], await Found(w, "ships placed"));
        Assert.Equal(1, w.Module.Served().Single().Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Indexes_of_versions_the_head_no_longer_names_are_removed_and_a_retired_project_takes_its_own(string engine)
    {
        var w = Open(engine, vectors: false);
        for (var v = 1; v <= 4; v++)
        {
            Publish(w, Project(Revenue with { Item3 = $"# Revenue\n\nVersion {v} of the definition." }), mode: PublishMode.Serve);
            await w.Module.StageAsync("sales", v);
            w.Module.Apply("sales", Version(w, v));
        }
        // served 4, previous 3: the indexes of 1 and 2 are gone
        Assert.Equal(["prod/sales@3", "prod/sales@4"], w.Store.Channels("prod/sales@"));

        w.Module.Apply("sales", null);
        Assert.Empty(w.Store.Channels("prod/"));
    }

    private static DefinitionVersion Version(World w, int version) =>
        new("knowledge-project", "sales", version, "h", "", [], "ada", Start, null, 1);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_version_with_a_bad_item_is_refused_at_stage_naming_file_and_line_and_the_served_version_keeps_serving(string engine)
    {
        var w = Open(engine, vectors: false);
        Publish(w, Project(Revenue), mode: PublishMode.Serve);
        await w.Module.StageAsync("sales", 1);

        // a publisher that did not run the checks (another tool) stores a file that does not read
        var artifacts = new PublishArtifacts(w.Payloads, w.Publisher, "ada");
        var good = artifacts.Add("revenue.md", Encoding.UTF8.GetBytes(Item("Revenue", "Revenue after returns.", role: "reference")), "text/markdown");
        var bad = artifacts.Add("ghost.md", Encoding.UTF8.GetBytes(Item("Ghost", "About nothing.", "  - semantic-model: Ghosts", role: "reference")), "text/markdown");
        w.Publisher.Publish(new PublishRequest("knowledge-project", "sales", KnowledgeDefinitions.Manifest("sales"), "ada", "prod", PublishMode.Stage, null, null, [good, bad]));

        var refused = await Assert.ThrowsAsync<KnowledgeStageRefusedException>(() => w.Module.StageAsync("sales", 2));
        Assert.Contains("ghost.md:6", refused.Message);
        Assert.Equal(["revenue.md"], await Found(w, "returns"));
    }

    // ---- vectors, on the engines that hold them ----

    [Theory]
    [MemberData(nameof(VectorEngines))]
    public async Task Staging_embeds_only_the_changed_items_a_search_during_stage_sees_the_served_version_and_a_rollback_restores_items_and_vectors(string engine)
    {
        var w = Open(engine, vectors: true);
        Publish(w, Project(Revenue, Margin, Orders), mode: PublishMode.Serve);
        var first = await w.Module.StageAsync("sales", 1);
        Assert.Equal((3, 0, 3), (first.Items, first.ChunksCopied, first.ChunksEmbedded));   // nothing was served before: every chunk is embedded once
        w.Module.Apply("sales", Version(w, 1));
        const int Probe = 1;                                                 // the first time a model is used, one sample text asks how many dimensions it gives
        Assert.Equal(3 + Probe, w.Embedder!.Embedded);

        // one item changes: the stage embeds that item's chunk and takes the other two over from the served version
        Publish(w, Project(Revenue with { Item3 = "# Revenue\n\nRevenue means net revenue after returns, discounts and VAT." }, Margin, Orders));
        var second = await w.Module.StageAsync("sales", 2);
        Assert.Equal((3, 2, 2, 1), (second.Items, second.Unchanged, second.ChunksCopied, second.ChunksEmbedded));
        Assert.Equal(4 + Probe, w.Embedder.Embedded);                        // three for the first version, one for the one that changed
        Assert.Equal(1, w.Module.Served().Single().Version);                 // a search during the stage reads version 1
        Assert.Empty(await Found(w, "VAT"));

        // staging it again embeds nothing
        var again = await w.Module.StageAsync("sales", 2);
        Assert.Equal(0, again.ChunksEmbedded);
        Assert.Equal(4 + Probe, w.Embedder.Embedded);

        w.Publisher.Activate("knowledge-project", "sales", "ada");
        w.Module.Apply("sales", Version(w, 2));
        Assert.Equal(["revenue.md"], await Found(w, "VAT"));
        var health = Assert.Single(w.Module.Health());
        Assert.Equal(("sales", 2, 3, "fake/fake-1", true), (health.Project, health.Version, health.Items, health.Model, health.VectorsComplete));

        // a rollback restores the previous items and the index that goes with them
        w.Publisher.Rollback("knowledge-project", "sales", "ada");
        w.Module.Apply("sales", Version(w, 1));
        Assert.Empty(await Found(w, "VAT"));
        Assert.Equal(1, Assert.Single(w.Module.Health()).Version);
        Assert.True(Assert.Single(w.Module.Health()).VectorsComplete);
        Assert.Equal(4 + Probe, w.Embedder.Embedded);                        // nothing was embedded again by going back
    }
}
