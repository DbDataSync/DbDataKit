using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// A Sync project's build deployed to <c>prod</c> through the console, with every piece the program it is (Design: docs/publishing.md#preflight-before-activation): a Workspace server in this process builds the set with the real
/// <c>dbdatasync</c>, the console deploys the build to a real runner whose engine is the runtime package <c>dbdatasync-runtime</c>, stages it held, runs the set's own <c>preflight</c> there with the run logins of the
/// slots bound in <c>prod</c> (a SQL Server source, a SQL Server and a PostgreSQL target), and activates the version only when the preflight found no error. Change tracking turned off on the source table is an
/// error with the statement that turns it on, and the next version stays staged until the person confirms.
/// </summary>
public sealed class SyncDeployPreflightTests : IAsyncLifetime
{
    private SyncOnRunner h = null!;
    private WebApplication workspace = null!;
    private string workspaceId = null!;
    private string directory = null!;

    public async ValueTask InitializeAsync()
    {
        if (SyncOnRunner.EnginesMissing is { } missing) Assert.Skip(missing);
        if (OperatingSystem.IsWindows()) Assert.Skip("the dbdatasync the Workspace server runs is a shell script here");
        h = await SyncOnRunner.StartAsync();
        directory = Path.Combine(h.Root, "workspace");
        Directory.CreateDirectory(directory);

        // the Workspace server runs dbdatasync as built, through a script that starts it with dotnet
        var tool = Path.Combine(h.Root, "dbdatasync");
        File.WriteAllText(tool, $"#!/bin/bash\nexec dotnet \"{Programs.Sync}\" \"$@\"\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(directory, new Uri("https://127.0.0.1:0")) { SyncExecutable = tool }, WorkspaceServer.Certificate(directory), _ => { });
        await workspace.StartAsync();

        // a CI runner is slow to start the engine's process: the deploy waits for the preflight longer than a person's two minutes, so the test reads its outcome, not a 202
        h.Console.Services.GetRequiredService<ConsoleDeploys>().PreflightWait = TimeSpan.FromMinutes(5);
        var instances = h.Console.Services.GetRequiredService<ConsoleInstances>();
        var code = WorkspaceState.Pairing(directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
        workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), code.Code, "Workspace", default)).InstanceId;
        // the console's publisher key in the runner's trust store, as the console's own pass puts it there once the runner is paired
        var trusted = await h.Console.Services.GetRequiredService<ConsoleTrust>().EnsureAsync(instances.Registry.Find(h.RunnerId)!);
        Assert.True(trusted.Done, trusted.Problem);
    }

    public async ValueTask DisposeAsync()
    {
        if (workspace is not null) { await workspace.StopAsync(); await workspace.DisposeAsync(); }
        if (h is not null) await h.DisposeAsync();
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Workspace(HttpMethod method, string path, JsonNode? body = null) => h.ConsoleCall(method, $"/relay/{workspaceId}/api/v1{path}", body);

    private async Task Put(string project, string draft, string path, string text)
    {
        var put = await Workspace(HttpMethod.Put, $"/projects/{project}/workspaces/{draft}/files/{path}", new JsonObject { ["content"] = text });
        Assert.True(put.Status is HttpStatusCode.OK or HttpStatusCode.Created, $"{path}: {put.Status} {put.Body}");
    }

    private async Task<string> BuildAsync(string project, string draft)
    {
        var requested = await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces/{draft}/builds");
        Assert.True(requested.Status == HttpStatusCode.Accepted, requested.Body?.ToJsonString());
        var build = (string)requested.Body!["id"]!;
        for (var i = 0; i < 1200; i++)
        {
            var read = (await Workspace(HttpMethod.Get, $"/projects/{project}/workspaces/{draft}/builds/{build}")).Body!;
            var status = (string)read["build"]!["status"]!;
            if (status is "succeeded" or "failed")
            {
                Assert.True(status == "succeeded", read.ToJsonString());
                return build;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("the build did not finish");
    }

    /// <summary>The configuration of the set: the harness's connection files, a replication <c>orders</c> that reads <c>inc_src</c> with change tracking and merges it into the SQL Server <c>dw</c>, and
    /// <c>copy</c>, which reloads <c>per_src</c> into the PostgreSQL <c>tgt</c>; both periodic at 03:00, so nothing but the preflight runs while the test does.</summary>
    private async Task WriteSetAsync(string project, string draft, string cron)
    {
        foreach (var template in new[] { "config/connections/source-db.yaml", "config/connections/target-db.yaml", "config/replications/orders/table-mappings/orders.yaml" })
            await Workspace(HttpMethod.Delete, $"/projects/{project}/workspaces/{draft}/files/{template}");
        foreach (var name in new[] { "src", "dw", "tgt" })
            await Put(project, draft, $"config/connections/{name}.yaml", File.ReadAllText(Path.Combine(h.SyncDirectory, "config", "connections", name + ".yaml")));

        foreach (var (name, reader, cache, writer, from, to, database, schema) in new[]
        {
            ("orders", "MsSqlChangeTracking", "MsSqlStagingTable", "MsSqlMerge", "inc", "dw", h.DwDatabase, "dbo"),
            ("copy", "BatchReload", "StagingTable", "DeleteInsert", "per", "tgt", h.TargetDatabase, "public"),
        })
        {
            await Put(project, draft, $"config/replications/{name}/task.yaml", $"""
                schemaVersion: 2
                name: {name}
                scheduling:
                  mode: Periodic
                  cronExpression: {cron}
                changeProcessing:
                  reader:
                    kind: {reader}
                  cache:
                    kind: {cache}
                  writer:
                    kind: {writer}
                endpoints:
                  source:
                    connectionName: src
                    database: {h.SourceDatabase}
                  target:
                    connectionName: {to}
                    database: {database}

                """);
            await Put(project, draft, $"config/replications/{name}/table-mappings/main.yaml", $"""
                schemaVersion: 1
                name: main
                sources:
                - schema: dbo
                  table: {from}_src
                  filter: id > 0
                targets:
                - schema: {schema}
                  table: {from}_tgt
                columnMappings:
                - sourceColumn: id
                  targetColumn: id
                - sourceColumn: name
                  targetColumn: name
                sourceColumns:
                - name: id
                  nativeType: int
                  isPrimaryKey: true
                  keyOrdinal: 1
                - name: name
                  nativeType: nvarchar(50)

                """);
        }
    }

    [Fact]
    public async Task A_sync_build_served_in_prod_through_the_console_runs_the_sets_preflight_on_the_staged_version_and_change_tracking_off_holds_the_next_version()
    {
        const string project = "sales-sync";
        var made = await Workspace(HttpMethod.Post, "/projects", new JsonObject { ["id"] = project, ["kind"] = "sync" });
        Assert.True(made.Status == HttpStatusCode.Created, made.Body?.ToJsonString());
        var draft = (string)(await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces", new JsonObject { ["name"] = "draft" })).Body!["id"]!;
        await WriteSetAsync(project, draft, "0 3 * * *");
        var build = await BuildAsync(project, draft);
        h.Step("first build");

        JsonObject Deploy(string b) => new()
        {
            ["workspaceServer"] = workspaceId, ["project"] = project, ["workspace"] = draft, ["build"] = b, ["target"] = h.RunnerId, ["channel"] = "prod", ["serve"] = true,
            ["bind"] = new JsonObject { ["src"] = "src", ["dw"] = "dw", ["tgt"] = "tgt" },
        };

        // every table is as the replications need it: the preflight finds no error, and the console activates the version it checked
        var served = await h.ConsoleCall(HttpMethod.Post, "/api/deploys", Deploy(build));
        Assert.True(served.Status == HttpStatusCode.OK, $"{served.Status} {served.Body}\n{await h.Diagnose(DbDataKit.Runner.Preflight.TaskName)}");
        var preflight = served.Body!["deployed"]!["preflight"]!;
        Assert.Equal(("serve", "activated", 0), ((string?)served.Body["deployed"]!["mode"], (string?)preflight["activation"], (int)preflight["errors"]!));
        var findings = preflight["findings"]!.AsArray();
        Assert.Contains(findings, f => ((string?)f!["message"] ?? "").Contains("needs nothing more for the reader 'MsSqlChangeTracking'", StringComparison.Ordinal));
        Assert.Contains(findings, f => (string?)f!["about"] == "tgt");   // the PostgreSQL target was read too
        h.Step("first version served after its preflight");

        // change tracking off on the source table, and a new version (another schedule): the preflight of the staged version is an error with the statement that turns it on, so it stays staged and held
        await h.ExecuteSourceAsync("ALTER TABLE dbo.[inc_src] DISABLE CHANGE_TRACKING;");
        await WriteSetAsync(project, draft, "30 3 * * *");
        var second = await BuildAsync(project, draft);
        var held = await h.ConsoleCall(HttpMethod.Post, "/api/deploys", Deploy(second));
        Assert.True(held.Status == HttpStatusCode.Conflict, $"{held.Status} {held.Body}");
        Assert.Contains("so it was not activated in prod", (string?)held.Body!["error"]);
        Assert.Contains("ENABLE CHANGE_TRACKING", (string?)held.Body["error"]);
        var refused = held.Body["deploy"]!["preflight"]!;
        Assert.Equal("refused", (string?)refused["activation"]);
        Assert.StartsWith($"/servers/{h.RunnerId}/runs?run=", (string?)refused["link"]);
        var error = refused["findings"]!.AsArray().First(f => (string?)f!["severity"] == "error")!;
        Assert.Equal("src", (string?)error["about"]);

        // the person confirms what the preflight found; the runner activates the version and keeps the confirmation
        var deploy = (string)held.Body["deploy"]!["id"]!;
        var confirmed = await h.ConsoleCall(HttpMethod.Post, $"/api/deploys/{deploy}/activate", new JsonObject { ["confirm"] = true });
        Assert.True(confirmed.Status == HttpStatusCode.OK, $"{confirmed.Status} {confirmed.Body}");
        Assert.Equal("activated", (string?)confirmed.Body!["activation"]);
    }
}
