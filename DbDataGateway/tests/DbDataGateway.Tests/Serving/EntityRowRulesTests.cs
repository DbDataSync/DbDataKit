using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 22a GP11 (D2): an entity of a published model that has row rules is not read through the group's store API, never served unfiltered; an entity without rules is read as before.</summary>
public sealed class EntityRowRulesTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-entity-rules-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private const string Model = """
        schemaVersion: 2
        name: sales
        storage: {connection: mdm_sql}
        access:
          row:
            - dataset: territory
              groups:
                - id: by_region
                  rules:
                    - { id: r1, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
              region: {type: VARCHAR(20)}
          note:
            key: [id]
            attributes:
              id: {type: VARCHAR(10)}
        """;

    [Fact]
    public async Task A_server_with_no_resolver_refuses_an_entity_with_row_rules_and_never_serves_it_unfiltered()
    {
        var layout = StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main"));
        var models = new PublishedEntityModels(new DefinitionStore(new StateDatabase(file, layout)));
        models.Publish("sales", Model, "mdm_sql", null, "ada", format: 2);
        Assert.NotNull(models.Find("sales"));
        var api = new GroupStoreApi(() => new GatewayOptions(), null!, models, null!);
        var viewer = new StoreCaller("u1", "Ada", false, DbDataKit.Web.Trust.ServerRole.Viewer);

        var rows = await api.RowsAsync(viewer, "sales", "territory", new Dictionary<string, string>(), CancellationToken.None);
        var row = await api.RowAsync(viewer, "sales", "territory", new Dictionary<string, string> { ["key.code"] = "X" }, CancellationToken.None);
        Assert.Equal((403, 403), (rows.Status, row.Status));
        Assert.Contains("row rules", (string?)rows.Body["error"]);

        // an entity with no rule goes on to the database (which this test does not have: the data source is not served here)
        Assert.Equal(502, (await api.RowsAsync(viewer, "sales", "note", new Dictionary<string, string>(), CancellationToken.None)).Status);
    }

    [Fact]
    public void The_publish_of_an_entity_model_with_row_rules_warns_that_they_limit_reads_only_and_a_semantic_model_does_not()
    {
        var model = DbDataKit.DataModel.DataModelLoader.Load(Model, "sales.yaml").Model!;
        var entity = AccessPublishNotes.Warnings(model.Access, readOnDataSource: false)!;
        Assert.Contains(entity.Select(n => (string?)n), n => n == AccessPublishNotes.EntityWritesWarning);
        Assert.Contains("reads only", AccessPublishNotes.EntityWritesWarning);
        Assert.DoesNotContain(AccessPublishNotes.Warnings(model.Access, readOnDataSource: true)!.Select(n => (string?)n), n => n == AccessPublishNotes.EntityWritesWarning);
    }
}
