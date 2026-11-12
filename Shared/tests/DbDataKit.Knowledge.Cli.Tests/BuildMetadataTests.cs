using System.Text.Json.Nodes;
using DbDataKit.Knowledge.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>
/// Plan 27a KN13: Build's metadata documents as the definition <c>build-metadata</c> and the generated knowledge the module renders from it: an item per model and per column with a stable id, the model's own access, provenance and direct upstream and downstream, an item changed by one
/// column's description and no other, and a model dropped from the project gone at the next publish. The life cycle runs on the state database of every engine.
/// </summary>
public sealed class BuildMetadataTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T11:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private static JsonNode Models(string regionDescription = "The sales region of the customer.", bool withOrders = true) => JsonNode.Parse($$"""
        [
          { "name": "dim_customer", "title": "Customer", "description": "One row per customer.", "kind": "full", "tags": ["pii"], "properties": { "owner": "sales" },
            "upstream": [ { "name": "staging.customers", "category": "model", "kind": "view" } ],
            "columns": [ { "name": "id", "logical_type": "bigint", "nullable": false, "description": "The customer's number." },
                         { "name": "region", "logical_type": "varchar(40)", "nullable": true, "description": "{{regionDescription}}" } ] }
          {{(withOrders ? """
          , { "name": "fct_orders", "description": "One row per order.", "kind": "incremental_by_time_range", "upstream": [ { "name": "dim_customer", "category": "model", "kind": "full" } ],
              "columns": [ { "name": "total", "logical_type": "decimal(18,2)", "nullable": false } ] }
          """ : "")}}
        ]
        """)!;

    private static JsonNode Document(JsonNode? models = null) => new JsonObject { ["project"] = "sales", ["connection"] = "dwh", ["models"] = (models ?? Models()).DeepClone() };

    private static GeneratedSource Source(string version = "1") => new("build-metadata", "sales.dwh", version, "prod", "data-office");

    private static SubjectCatalog Known() => new SubjectCatalog().Add("build-project", "sales", "model", "dim_customer").Add("build-project", "sales", "model", "fct_orders");

    // ---- the renderer, no database ----

    [Fact]
    public void Each_model_and_each_column_is_an_item_about_the_model_with_a_stable_id_and_its_neighbours_named()
    {
        var items = GeneratedBuildKnowledge.Render(Document(), Source());

        Assert.Equal(["generated:build-model:sales/dim_customer", "generated:build-model:sales/dim_customer/column:id", "generated:build-model:sales/dim_customer/column:region",
            "generated:build-model:sales/fct_orders", "generated:build-model:sales/fct_orders/column:total"], items.Select(i => i.Id));
        Assert.All(items, i => { Assert.Equal(KnowledgeRole.Reference, i.Role); Assert.Equal("data-office", i.Owner); });
        var customer = items[0];
        Assert.Equal("build-project:sales/model:dim_customer", Assert.Single(customer.About).Key);
        Assert.Contains("One row per customer.", customer.Body);
        Assert.Contains("Tags: pii.", customer.Body);
        Assert.Contains("Columns (2): id (bigint), region (varchar(40)).", customer.Body);
        Assert.Contains("Reads from: staging.customers.", customer.Body);
        Assert.Contains("Read by: fct_orders.", customer.Body);               // direct downstream, from what the other model says it reads
        Assert.Contains("never empty", items[1].Body);
        Assert.Contains("The sales region of the customer.", items[2].Body);
        Assert.Equal("build-project:sales/model:dim_customer", Assert.Single(items[2].About).Key);   // a column is about its model: who may see the model sees it
    }

    [Fact]
    public void One_changed_description_changes_one_item_and_nothing_volatile_is_in_the_text()
    {
        var before = GeneratedBuildKnowledge.Render(Document(), Source("1"));
        var after = GeneratedBuildKnowledge.Render(Document(Models("Where the customer lives.")), Source("2"));

        Assert.Equal(before.Count, after.Count);
        var changed = before.Zip(after).Where(p => p.First.Hash != p.Second.Hash).Select(p => p.First.Id).ToList();
        Assert.Equal(["generated:build-model:sales/dim_customer/column:region"], changed);   // the version is kept beside the item by the index, never in its text
    }

    [Fact]
    public void Columns_with_no_description_are_listed()
    {
        Assert.Equal([("fct_orders", "total")], GeneratedBuildKnowledge.UndescribedColumns(Document()));
    }

    [Fact]
    public void The_kind_is_a_serving_kind_that_serves_at_once_and_its_validator_reads_the_manifest_and_the_one_file()
    {
        var kind = BuildMetadataDefinitions.Registry().Require("build-metadata");
        Assert.Equal(["serving"], kind.TargetTypes);
        Assert.Equal(PublishMode.Serve, kind.DefaultMode);
        Assert.Equal(1, kind.FormatOf!(BuildMetadataDefinitions.Manifest("sales", "dwh")));
        string? Validate(string name, string manifest, params string[] paths) => kind.Validator!(new DefinitionCandidate("build-metadata", name, manifest, [.. paths.Select(p => new ArtifactRef(p, new string('a', 64), 10, "application/json"))], [], 1));

        Assert.Null(Validate("sales.dwh", BuildMetadataDefinitions.Manifest("sales", "dwh", "data-office"), "metadata.json"));
        Assert.Contains("published as", Validate("other", BuildMetadataDefinitions.Manifest("sales", "dwh"), "metadata.json")!);
        Assert.Contains("no `connection`", Validate("sales.dwh", "build-metadata: 1\nproject: sales\n", "metadata.json")!);
        Assert.Contains("has one file", Validate("sales.dwh", BuildMetadataDefinitions.Manifest("sales", "dwh"), "metadata.json", "extra.json")!);
    }

    // ---- the life cycle, on every engine ----

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_publish_writes_the_kind_the_module_renders_an_item_per_model_and_column_and_a_dropped_model_disappears_at_the_next_publish(string engine)
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, KnowledgeModule.GroupModule], new StateScope(Group: "main"), time: new StateTestClock(Start));
        var payloads = new PayloadStore(database);
        var publisher = new DefinitionPublisher(database, BuildMetadataDefinitions.Registry(), payloads, "serving");
        var store = new KnowledgeStore(database);
        var module = new KnowledgeProjectModule(publisher, payloads, store, null, "prod", null, () => Known(), new StateTestClock(Start));
        var follower = module.BuildMetadata;
        var artifacts = () => new PublishArtifacts(payloads, publisher, "build");

        var one = BuildMetadataDefinitions.Publish(publisher, artifacts(), "sales", "dwh", Models(), "build", owner: "data-office");
        Assert.True(one.Changed);
        follower.Apply("sales.dwh", new DefinitionStore(database).Current("build-metadata", "sales.dwh"));
        Assert.Equal(5, store.Counts(module.GeneratedChannel).Items);
        Assert.Equal(("sales.dwh", 0), (module.Served().Single().Project is "generated" ? "sales.dwh" : "", module.Served().Single().Version));

        // found under the model's own access: a person who may read the project's model finds it, another does not
        string[] Found(string question, params string[] readable) =>
            [.. module.Reader(new Caller(readable), Known(), null, KnowledgeSearchMode.Words).SearchAsync(question, "prod", null, null, default).GetAwaiter().GetResult().Hits.Select(h => h.ItemId)];
        Assert.Contains("generated:build-model:sales/dim_customer/column:region", Found("sales region customer", "build-project:sales"));
        Assert.Empty(Found("sales region customer", "build-project:other"));

        // the same documents again change nothing; a changed description changes one item; a dropped model takes its items
        Assert.False(BuildMetadataDefinitions.Publish(publisher, artifacts(), "sales", "dwh", Models(), "build", owner: "data-office").Changed);
        var before = store.Item(module.GeneratedChannel, "generated:build-model:sales/dim_customer/column:id")!.IndexedAt;
        var two = BuildMetadataDefinitions.Publish(publisher, artifacts(), "sales", "dwh", Models("Where the customer lives.", withOrders: false), "build", owner: "data-office");
        Assert.Equal(2, two.Version.Version);
        follower.Apply("sales.dwh", new DefinitionStore(database).Current("build-metadata", "sales.dwh"));
        Assert.Equal(3, store.Counts(module.GeneratedChannel).Items);
        Assert.Null(store.Item(module.GeneratedChannel, "generated:build-model:sales/fct_orders"));
        Assert.Equal("v2", store.Item(module.GeneratedChannel, "generated:build-model:sales/dim_customer/column:id")!.Version);   // unchanged text, brought up to date
        Assert.Equal(before, store.Item(module.GeneratedChannel, "generated:build-model:sales/dim_customer/column:id")!.IndexedAt);   // and not indexed again
        Assert.Contains("Where the customer lives.", string.Join(' ', store.Chunks(module.GeneratedChannel, "generated:build-model:sales/dim_customer/column:region").Select(c => c.Text)));

        // retired: the definition goes and so do its items
        follower.Apply("sales.dwh", null);
        Assert.Equal(0, store.Counts(module.GeneratedChannel).Items);
        await Task.CompletedTask;
    }
}
