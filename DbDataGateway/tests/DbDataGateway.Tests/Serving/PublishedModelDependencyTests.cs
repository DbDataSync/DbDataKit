using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 32 DBR11: a published semantic model writes its <c>depends_on</c> as the registry reads a dependency (<c>kind:name</c>), the form a dashboard's <c>semantic-model:name@version</c> has too, so a publisher that
/// registers the kinds (the views module's) can make a model active, and move it with a dashboard, without refusing the Gateway's own rows.
/// </summary>
public sealed class PublishedModelDependencyTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-model-deps-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    [Fact]
    public void A_models_dependency_on_its_sources_is_written_kind_colon_name_and_the_registry_accepts_it()
    {
        var layout = StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main"));
        var database = new StateDatabase(file, layout);
        var store = new DefinitionStore(database);
        var models = new PublishedSemanticModels(store, new SemanticModels());

        var sources = new Dictionary<string, string> { ["sales"] = "warehouse", ["budget"] = "warehouse", ["ops"] = "ops" };
        models.Publish("sales_catalog", "name: sales_catalog", null, "ada", sources, [], DateTimeOffset.UtcNow);
        models.Publish("sales_catalog", "name: sales_catalog # 2", null, "ada", sources, [], DateTimeOffset.UtcNow);

        var version = store.Current(PublishedSemanticModels.DefinitionKind, "sales_catalog")!;
        Assert.Equal(2, version.Version);
        Assert.Equal(["data-source:warehouse", "data-source:ops"], version.DependsOn);

        // a publisher with the kinds registered reads those rows: version 1 becomes active again once the sources it names are
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("data-source", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind(PublishedSemanticModels.DefinitionKind, ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(2) });
        var publisher = new DefinitionPublisher(database, kinds, null, "serving");
        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Switch(PublishedSemanticModels.DefinitionKind, "sales_catalog", 1, "ada"));
        Assert.Equal(PublishRefusal.DependencyNotActive, refused.Reason);
        Assert.Contains("data-source:warehouse", refused.Message);
        publisher.Publish(new PublishRequest("data-source", "warehouse", "kind: sqlserver", "ada", "prod", PublishMode.Serve));
        publisher.Publish(new PublishRequest("data-source", "ops", "kind: sqlserver", "ada", "prod", PublishMode.Serve));
        Assert.Equal(1, publisher.Switch(PublishedSemanticModels.DefinitionKind, "sales_catalog", 1, "ada").Version);
    }

    [Fact]
    public void An_entity_models_dependency_on_its_data_source_is_written_the_same_way_and_the_registry_accepts_it()
    {
        var layout = StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main"));
        var database = new StateDatabase(file, layout);
        var store = new DefinitionStore(database);
        var models = new PublishedEntityModels(store);

        models.Publish("territories", "schemaVersion: 1\nname: territories\nentities: {}\n", "mdm", null, "ada");
        models.Publish("territories", "schemaVersion: 1\nname: territories\nentities: {} # 2\n", "mdm", null, "ada");

        var version = store.Current(PublishedEntityModels.DefinitionKind, "territories")!;
        Assert.Equal(2, version.Version);
        Assert.Equal(["data-source:mdm"], version.DependsOn);

        // a publisher with the kinds registered reads the row: the model is refused until the source it names is active, then it is
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("data-source", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind(PublishedEntityModels.DefinitionKind, ["serving"], PublishMode.Serve));
        var publisher = new DefinitionPublisher(database, kinds, null, "serving");
        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Switch(PublishedEntityModels.DefinitionKind, "territories", 1, "ada"));
        Assert.Equal(PublishRefusal.DependencyNotActive, refused.Reason);
        Assert.Contains("data-source:mdm", refused.Message);
        publisher.Publish(new PublishRequest("data-source", "mdm", "kind: sqlserver", "ada", "prod", PublishMode.Serve));
        Assert.Equal(1, publisher.Switch(PublishedEntityModels.DefinitionKind, "territories", 1, "ada").Version);
    }
}
