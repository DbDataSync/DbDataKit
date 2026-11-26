using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Catalog;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// The read of a database's shape for a Workspace project, with the real programs: a runner process holding the project's package <c>crm-to-dw</c> with its slot <c>crm</c> bound in <c>dev</c> to a PostgreSQL connection the runner has, a
/// Workspace server and a console in this process. The console asks the Workspace server for the project's package, starts the runner's built-in <c>discover</c> as the signed-in person, and answers the connection as
/// <c>catalog-cache/2</c> recorded as read from the runner and <c>dev</c>; a column added to the table is in the next read. The Workspace server is paired with no runner, holds no login and is never called by the read.
/// </summary>
public sealed class CatalogReadFromRunnerTests
{
    private const string Channel = "dev";
    private const string Package = "crm-to-dw";
    private const string PasswordVariable = "DBDATAKIT_TEST_CATALOG_READ_PASSWORD";

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The project's package as the runner holds it: one task with the slot <c>crm</c> (its files are never run by a read).</summary>
    private static string WritePackage(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        File.WriteAllText(Path.Combine(folder, "files", "Replicate.dll"), "not run by a read");
        var task = new PackageTask("replicate", new TaskEntry("Replicate"), new Schedule(), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], []) { Connections = [new ConnectionSlot("crm", "database")] };
        var manifest = new PackageManifest(Package, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "Replicate.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [task]);
        File.WriteAllText(Path.Combine(folder, "package.yaml"), PackageManifestWriter.Write(manifest));
        return folder;
    }

    [Fact]
    public async Task A_read_in_dev_answers_the_connection_recorded_as_read_from_dev_for_the_person_and_a_column_added_is_in_the_next_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-catalog-" + Guid.NewGuid().ToString("N")[..8]);
        var (connection, drop) = await ConsoleJoinsRunnerGroupTests.ProvisionAsync("postgres");
        RealProcess? runnerProcess = null;
        WebApplication? workspace = null, console = null;
        try
        {
            await ExecuteAsync(connection, "CREATE SCHEMA crm; CREATE TABLE crm.accounts (id int PRIMARY KEY, name text NOT NULL); INSERT INTO crm.accounts VALUES (1, 'row-value-5521');");

            // the runner: a PostgreSQL connection of its own, the project's package, its slot bound in dev
            var runner = Programs.Runner;
            var runnerDirectory = Path.Combine(root, "runner");
            Directory.CreateDirectory(Path.Combine(runnerDirectory, "connections"));
            var pg = new NpgsqlConnectionStringBuilder(connection);
            File.WriteAllText(Path.Combine(runnerDirectory, "connections", "crm_dev.yaml"),
                $"schema: connection/1\nname: crm_dev\nengine: postgres\nconnection_string: 'Host={pg.Host};Port={pg.Port};Database={pg.Database}'\ntls: {{ encrypt: false }}\nauth: {{ mode: sql, user: {pg.Username}, password_secret: 'env:{PasswordVariable}' }}\n");
            var state = StateDatabase.OpenModules(Path.Combine(runnerDirectory, "state.db"), RunnerNames.EngineModules, new StateScope(Group: "main", Service: RunnerNames.ServiceId));
            new RunnerPublishing(state).Bind(Package, Channel, new Dictionary<string, string> { ["crm"] = "crm_dev" }, null, RunnerActor.Local());
            var package = WritePackage(Path.Combine(root, "package"));
            await RealProcess.RunAsync(runner, "package", "install", Path.Combine(package, "package.yaml"), Path.Combine(package, "files"), "--channel", Channel, "--repo", runnerDirectory);
            var code = Programs.CodeIn(await RealProcess.RunAsync(runner, "server", "pair", "--repo", runnerDirectory, "--max-role", "admin"));
            var runnerUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
            runnerProcess = RealProcess.Start(runner, ["server", "run", "--repo", runnerDirectory, "--listen", runnerUrl.AbsoluteUri.TrimEnd('/')], environment: new Dictionary<string, string> { [PasswordVariable] = pg.Password! });
            await runnerProcess.WaitUntilAsync(new Uri(runnerUrl, "/health"), TimeSpan.FromSeconds(90));

            // the Workspace server, paired with the console only
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
            var runnerId = (await instances.PairAsync(runnerUrl, code, "Runners", default)).InstanceId;
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
            var accounts = console.Services.GetRequiredService<IUserAccounts>();
            var ada = accounts.CreateUser("Ada Author", null, UserRole.Admin).Id;
            var vic = accounts.CreateUser("Vic Viewer", null, UserRole.Viewer).Id;
            async Task<(HttpStatusCode Status, JsonObject Body)> As(string person, HttpMethod method, string path, object? body = null)
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(person).Id}");
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request);
                return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
            }

            var (made, madeBody) = await As(ada, HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects", new { id = Package, kind = "sync" });
            Assert.True(made == HttpStatusCode.Created, $"{made} {madeBody}");
            var routes = $"/api/workspace-catalog/{workspaceId}/{Package}";

            // the channel picker: dev binds the slot, prod does not
            var (listed, channels) = await As(ada, HttpMethod.Get, $"{routes}/channels?runner={runnerId}&connection=crm");
            Assert.True(listed == HttpStatusCode.OK, $"{listed} {channels}\n{runnerProcess.Output}");
            Assert.Equal("dev", (string?)channels["default"]);

            // a read as Ada, followed to its end when the first start of the task process takes longer than the console waits
            async Task<CacheConnection> ReadAsync()
            {
                var (status, body) = await As(ada, HttpMethod.Post, $"{routes}/read", new { runner = runnerId, channel = Channel, connection = "crm" });
                var until = DateTime.UtcNow.AddSeconds(120);
                while (status == HttpStatusCode.Accepted && DateTime.UtcNow < until)
                {
                    await Task.Delay(500);
                    (status, body) = await As(ada, HttpMethod.Get, $"{routes}/read/{(string)body["run_id"]!}?runner={runnerId}&channel={Channel}&connection=crm");
                }
                Assert.True(status == HttpStatusCode.OK, $"{status} {body}\n{runnerProcess.Output}");
                Assert.DoesNotContain("row-value-5521", body.ToJsonString());
                var load = CatalogCache.Load((string)body["cache"]!);
                Assert.True(load.Ok, string.Join("\n", load.Diagnostics.Select(d => d.Message)));
                return load.Cache!.Connections["crm"];
            }

            var first = await ReadAsync();
            Assert.Equal(("Runners", "runner", "dev"), (first.Read!.Server, first.Read.Product, first.Read.Channel));
            var table = Assert.Single(first.Tables, t => t.Schema == "crm" && t.Name == "accounts");
            Assert.Equal(["id", "name"], table.Columns.Select(c => c.Name).ToArray());

            // the run is recorded as Ada's, beside the console that asked
            var audit = new AuditLog(state, RunnerNames.ServiceId).Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 10));
            Assert.Contains(audit, e => (e.Entry.TargetId ?? "").Contains("dbdatakit-metadata") && (e.Entry.Detail ?? "").Contains("Ada Author"));

            // a column added to the table is in the next read: the difference a screen shows
            await ExecuteAsync(connection, "ALTER TABLE crm.accounts ADD COLUMN segment text");
            var second = await ReadAsync();
            var added = Assert.Single(second.Tables, t => t.Name == "accounts").Columns.Select(c => c.Name).Except(table.Columns.Select(c => c.Name)).ToList();
            Assert.Equal(["segment"], added);

            // a viewer of the runner may not start a read there
            var (viewer, refused) = await As(vic, HttpMethod.Post, $"{routes}/read", new { runner = runnerId, channel = Channel, connection = "crm" });
            Assert.Equal(HttpStatusCode.Forbidden, viewer);
            Assert.Contains("operator", (string)refused["error"]!);

            // the Workspace server has no runner or gateway it could call: the reads went from the console to the runner
            Assert.Equal(HttpStatusCode.Forbidden, (await As(ada, HttpMethod.Get, $"/relay/{workspaceId}/api/v1/publish-targets")).Status);
        }
        finally
        {
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            if (workspace is not null) { await workspace.StopAsync(); await workspace.DisposeAsync(); }
            if (runnerProcess is not null) await runnerProcess.DisposeAsync();
            await drop();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
