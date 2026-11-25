using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Catalog;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// A draft read before it is ever published, with the real programs: a runner process serving <c>prod</c> and <c>dev</c> that holds no version of the project's package <c>sales-dw</c>, a Workspace server and a console in this
/// process. The channel picker lists the channels the runner serves; Set up for development from the draft binds the slot <c>warehouse</c> in <c>dev</c> to a copy of the draft's connection; then <b>Refresh</b> in <c>dev</c> reads its
/// tables and the binding test connects, and the runner still holds no version of <c>sales-dw</c>.
/// </summary>
public sealed class DraftReadBeforePublishTests
{
    private const string Package = "sales-dw";

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_never_published_draft_is_listed_in_every_channel_and_after_set_up_for_dev_refresh_reads_its_tables_and_the_binding_test_connects()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-draftread-" + Guid.NewGuid().ToString("N")[..8]);
        var (connection, drop) = await ConsoleJoinsRunnerGroupTests.ProvisionAsync("postgres");
        RealProcess? runnerProcess = null;
        WebApplication? workspace = null, console = null;
        try
        {
            await ExecuteAsync(connection, "CREATE SCHEMA dw; CREATE TABLE dw.sales (id int PRIMARY KEY, amount numeric(10,2) NOT NULL); INSERT INTO dw.sales VALUES (1, 4417.25);");

            var runner = Programs.Runner;
            var runnerDirectory = Path.Combine(root, "runner");
            Directory.CreateDirectory(runnerDirectory);
            var code = Programs.CodeIn(await RealProcess.RunAsync(runner, "server", "pair", "--repo", runnerDirectory, "--max-role", "admin"));
            var runnerUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
            runnerProcess = RealProcess.Start(runner, ["server", "run", "--repo", runnerDirectory, "--listen", runnerUrl.AbsoluteUri.TrimEnd('/'), "--channels", "prod,dev"]);
            await runnerProcess.WaitUntilAsync(new Uri(runnerUrl, "/health"), TimeSpan.FromSeconds(90));

            var workspaceDirectory = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceDirectory);
            workspace = WorkspaceServer.Build(new WorkspaceSettings(workspaceDirectory, new Uri("https://127.0.0.1:0")), WorkspaceServer.Certificate(workspaceDirectory), _ => { });
            await workspace.StartAsync();

            var consoleDirectory = Path.Combine(root, "console");
            console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
            await console.StartAsync();
            var instances = console.Services.GetRequiredService<ConsoleInstances>();
            var workspaceCode = WorkspaceState.Pairing(workspaceDirectory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
            var workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), workspaceCode.Code, "Workspace", default)).InstanceId;
            var runnerId = (await instances.PairAsync(runnerUrl, code, "plant", default)).InstanceId;
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
            var ada = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Author", null, UserRole.Admin).Id;
            async Task<(HttpStatusCode Status, JsonObject Body)> As(HttpMethod method, string path, object? body = null)
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(ada).Id}");
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request);
                return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
            }

            // a sync project (the Workspace server builds no Build project without dbdatabuild beside it) and a draft with the connection warehouse: the address of the test database, its user, and a reference for the password
            var (made, madeBody) = await As(HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects", new { id = Package, kind = "sync" });
            Assert.True(made == HttpStatusCode.Created, $"{made} {madeBody}");
            var (opened, draftBody) = await As(HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects/{Package}/workspaces", new { name = "draft" });
            Assert.True(opened is HttpStatusCode.OK or HttpStatusCode.Created, $"{opened} {draftBody}");
            var draft = (string)draftBody["id"]!;
            var pg = new NpgsqlConnectionStringBuilder(connection);
            var file = $"schema: connection/1\nname: warehouse\nengine: postgres\nconnection_string: 'Host={pg.Host};Port={pg.Port};Database={pg.Database}'\ntls: {{ encrypt: false }}\nauth:\n  mode: sql\n  user: {pg.Username}\n  password_secret: secret:dbdatakit:sync:connection:warehouse\n";
            var (saved, savedBody) = await As(HttpMethod.Put, $"/relay/{workspaceId}/api/v1/projects/{Package}/workspaces/{draft}/files/config/connections/warehouse.yaml", new { content = file });
            Assert.True(saved is HttpStatusCode.OK or HttpStatusCode.Created, $"{saved} {savedBody}");
            var routes = $"/api/workspace-catalog/{workspaceId}/{Package}";

            // the picker: the runner holds no version of sales-dw, so it lists the channels the runner serves, none bound
            var (listed, channels) = await As(HttpMethod.Get, $"{routes}/channels?runner={runnerId}&connection=warehouse");
            Assert.True(listed == HttpStatusCode.OK, $"{listed} {channels}\n{runnerProcess.Output}");
            Assert.Equal(["dev", "prod"], channels["channels"]!.AsArray().Select(c => (string)c!["channel"]!).ToArray());
            Assert.All(channels["channels"]!.AsArray(), c => Assert.False((bool)c!["bound"]!));
            Assert.Null((string?)channels["default"]);

            // Set up for development binds the slot in dev to a copy of the draft's connection
            var (setUp, setUpBody) = await As(HttpMethod.Post, $"/api/workspace-dev-logins/{workspaceId}/{Package}", new { runner = runnerId, channel = "dev", connection = "warehouse", password = pg.Password, draft });
            Assert.True(setUp == HttpStatusCode.OK && (bool?)setUpBody["bound"] == true, $"{setUp} {setUpBody}\n{runnerProcess.Output}");
            var (_, after) = await As(HttpMethod.Get, $"{routes}/channels?runner={runnerId}&connection=warehouse");
            Assert.Equal("dev", (string?)after["default"]);
            Assert.Equal("sales-dw-warehouse-dev", (string?)after["channels"]![0]!["bound_to"]);

            // Refresh in dev reads the tables, with no version of the package on the runner
            var (status, body) = await As(HttpMethod.Post, $"{routes}/read", new { runner = runnerId, channel = "dev", connection = "warehouse" });
            var until = DateTime.UtcNow.AddSeconds(120);
            while (status == HttpStatusCode.Accepted && DateTime.UtcNow < until)
            {
                await Task.Delay(500);
                (status, body) = await As(HttpMethod.Get, $"{routes}/read/{(string)body["run_id"]!}?runner={runnerId}&channel=dev&connection=warehouse");
            }
            Assert.True(status == HttpStatusCode.OK, $"{status} {body}\n{runnerProcess.Output}");
            Assert.DoesNotContain("4417.25", body.ToJsonString());
            var load = CatalogCache.Load((string)body["cache"]!);
            Assert.True(load.Ok, string.Join("\n", load.Diagnostics.Select(d => d.Message)));
            Assert.Contains(load.Cache!.Connections["warehouse"].Tables, t => t.Schema == "dw" && t.Name == "sales");

            // the binding test of the draft's connection in dev connects
            var (tested, test) = await As(HttpMethod.Post, $"{routes}/binding-test", new { runner = runnerId, channel = "dev", connection = "warehouse" });
            Assert.True(tested == HttpStatusCode.OK, $"{tested} {test}");
            Assert.True((bool)test["ok"]!, test.ToJsonString());
            Assert.Equal("connect", (string?)test["step"]);

            // the runner still holds no version of the package
            var (described, _) = await As(HttpMethod.Get, $"/relay/{runnerId}/api/v1/definitions/integration/{Package}");
            Assert.Equal(HttpStatusCode.NotFound, described);
        }
        finally
        {
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            if (workspace is not null) { await workspace.StopAsync(); await workspace.DisposeAsync(); }
            if (runnerProcess is not null) await runnerProcess.DisposeAsync();
            await drop();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
