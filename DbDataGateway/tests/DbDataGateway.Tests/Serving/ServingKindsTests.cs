using DbDataGateway.Server.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 30 PB01: the serving kinds are in the registry, and the stores publish through it.</summary>
public sealed class ServingKindsTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-serving-kinds-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private StateDatabase Open() => new(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main")));

    [Fact]
    public void Every_serving_kind_is_registered_for_serving_targets_in_serve_mode()
    {
        foreach (var name in new[] { "semantic-model", "data-source", "entity-model", "app", "app-instance", "app-nav" })
        {
            var kind = ServingKinds.All.Find(name);
            Assert.NotNull(kind);
            Assert.Equal(["serving"], kind.TargetTypes);
            Assert.Equal(PublishMode.Serve, kind.DefaultMode);
        }
    }

    [Fact]
    public void A_version_that_does_not_load_is_refused_by_the_validator()
    {
        var store = ServingKinds.StoreOf(Open());
        var refused = Assert.Throws<PublishRefusedException>(() => store.Publish("data-source", "bad", "not: [a connection", "ada"));
        Assert.Equal(PublishRefusal.Invalid, refused.Reason);
        Assert.Contains("data-source", refused.Message);
        Assert.Throws<PublishRefusedException>(() => store.Publish("app-instance", "i", "[]", "ada"));
    }

    [Fact]
    public void A_format_the_kind_does_not_read_is_refused_naming_kind_name_and_numbers()
    {
        var store = ServingKinds.StoreOf(Open());
        var refused = Assert.Throws<PublishRefusedException>(() => store.Publish("app-nav", "menu", "{\"title\":\"t\",\"entries\":[]}", "ada", format: 2));
        Assert.Equal(PublishRefusal.Format, refused.Reason);
        Assert.Contains("app-nav", refused.Message);
        Assert.Contains("menu", refused.Message);
        Assert.Contains("2", refused.Message);
    }

    [Fact]
    public void A_model_names_only_the_data_sources_the_group_publishes()
    {
        var database = Open();
        var store = ServingKinds.StoreOf(database);
        var models = new PublishedSemanticModels(store, new SemanticModels());
        var catalog = "name: m\n";
        // a source set up by hand is not a definition: the registry would refuse the model if it were named
        try { models.Publish("m", catalog, null, "ada", new() { ["s"] = "by_hand" }, [], DateTimeOffset.UtcNow); }
        catch (PublishRefusedException ex) { Assert.Equal(PublishRefusal.Invalid, ex.Reason); return; }
        Assert.Empty(store.Current("semantic-model", "m")!.DependsOn);
    }

    [Fact]
    public void A_kind_that_does_not_list_serving_is_refused()
    {
        var kinds = new DefinitionKinds().Register(new DefinitionKind("x", ["runner"], PublishMode.Stage));
        var store = new DefinitionStore(Open()).WithRegistry(kinds, "serving");
        Assert.Equal(PublishRefusal.WrongTarget, Assert.Throws<PublishRefusedException>(() => store.Publish("x", "n", "c", "ada")).Reason);
    }
}
