using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 33 SC24: the group's master data on the management API (<c>/api/v1/store/models/...</c>), what the console's screens reach through its relay with the shared <c>store-ui</c> controls. The paths are the store module's, the caller is the
/// person the console's token names with its roles here, and every rule is <see cref="GroupStoreApi"/>'s (its own tests and the store module's cover them); this test is the routing and the person.
/// </summary>
public sealed class GatewayStoreManagementTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-store-management-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private const string Model = """
        schemaVersion: 2
        name: sales
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              name: {type: VARCHAR(100)}
        """;

    private static ValidatedToken Person(string id, string name, ServerRole roles)
    {
        var now = DateTimeOffset.UtcNow;
        return new ValidatedToken(new PairedClient("console-1", "console", [], ServerRole.Admin | ServerRole.Approver), id, name, true, roles, new Dictionary<string, ServerRole>(), "t", now, now.AddMinutes(5));
    }

    private GroupStoreApi Api(bool requests = false)
    {
        var layout = StateLayout.Create(StateEngineIds.Sqlite, requests ? [DefinitionsModule.Module, ServingModule.Module] : [DefinitionsModule.Module], new StateScope(Group: "main"));
        var state = new StateDatabase(file, layout);
        var models = new PublishedEntityModels(new DefinitionStore(state));
        models.Publish("sales", Model, "mdm_sql", null, "ada", format: 2);
        return new GroupStoreApi(() => new GatewayOptions(), null!, models, requests ? new GroupChangeRequests(state) : null!);
    }

    private static Task<(int Status, JsonNode Body)> Call(GroupStoreApi api, ValidatedToken who, string method, string path, string? body = null) =>
        GatewayStoreManagement.HandleAsync(api, who, method, GatewayStoreManagement.Prefix + path, body is null ? [] : Encoding.UTF8.GetBytes(body), CancellationToken.None);

    [Fact]
    public async Task The_store_paths_answer_the_person_the_console_names_with_their_roles_here()
    {
        var api = Api(requests: true);
        var ada = Person("u-ada", "Ada", ServerRole.Viewer);
        var none = Person("u-x", "Nobody", ServerRole.None);

        // the schema the controls draw the editor from, the one copy the group serves, the models a person may see
        var schema = await Call(api, ada, "GET", "/sales/schema");
        Assert.Equal(200, schema.Status);
        Assert.Equal("territory", (string)schema.Body["entities"]![0]!["name"]!);
        var instances = await Call(api, ada, "GET", "/sales/instances");
        Assert.Equal((200, "none", "default"), (instances.Status, (string)instances.Body["mode"]!, (string)instances.Body["instances"]![0]!["name"]!));
        Assert.Equal(200, (await Call(api, ada, "GET", "")).Status);

        // a person without a role on this server reads nothing; a model the group does not serve is said
        Assert.Equal(403, (await Call(api, none, "GET", "/sales/schema")).Status);
        Assert.Equal(403, (await Call(api, none, "GET", "/sales/instances")).Status);
        Assert.Equal(404, (await Call(api, ada, "GET", "/nowhere/instances")).Status);

        // a viewer may not write; the rows go to the model's data source (not served in this test: the store's own tests cover the data)
        var write = await Call(api, ada, "POST", "/sales/changes", """{"changes":[{"action":"insert","entity":"territory","values":{"code":"N"}}]}""");
        Assert.Equal(403, write.Status);
        Assert.Equal(502, (await Call(api, ada, "GET", "/sales/entities/territory/rows?limit=5&offset=0")).Status);

        // the change requests say whether this person may decide them (an author may here; a viewer may not), as a DbDataStore server's list does
        var asViewer = await Call(api, ada, "GET", "/sales/requests?state=pending");
        Assert.Equal((200, false, "an author or an administrator"), (asViewer.Status, (bool)asViewer.Body["can_decide"]!, (string)asViewer.Body["decision_needs"]!));
        Assert.True((bool)(await Call(api, Person("u-bea", "Bea", ServerRole.Author), "GET", "/sales/requests"))!.Body["can_decide"]!);

        // a path the store does not have, or a method it does not take there, is a 404 naming it
        var unknown = await Call(api, ada, "PUT", "/sales/schema");
        Assert.Equal(404, unknown.Status);
        Assert.Contains("There is no store route PUT /api/v1/store/models/sales/schema", (string)unknown.Body["error"]!);
    }
}
