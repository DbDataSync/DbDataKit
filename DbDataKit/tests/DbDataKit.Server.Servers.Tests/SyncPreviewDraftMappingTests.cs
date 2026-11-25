using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// <b>Preview rows</b> of a draft's mapping, with every piece the program it is (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): a Workspace server in this process builds a Sync set with the real
/// <c>dbdatasync</c>, the console stages the build on a real runner, and the set's <c>preview</c> is started through the console with the text of the mapping's file as the draft has it. The version's mapping has no filter;
/// the draft's has <c>region = 2</c>, and the rows answered are only those of region 2. The run's record on the runner keeps the mapping's text as an input.
/// </summary>
public sealed class SyncPreviewDraftMappingTests : IAsyncLifetime
{
    private SyncOnRunner h = null!;
    private WebApplication workspace = null!;
    private string workspaceId = null!;

    /// <summary>A value of a row of region 1, which the preview must not answer.</summary>
    private const string OtherRegion = "region-one-7713";

    public async ValueTask InitializeAsync()
    {
        if (SyncOnRunner.EnginesMissing is { } missing) Assert.Skip(missing);
        if (OperatingSystem.IsWindows()) Assert.Skip("the dbdatasync the Workspace server runs is a shell script here");
        h = await SyncOnRunner.StartAsync();
        var directory = Path.Combine(h.Root, "workspace");
        Directory.CreateDirectory(directory);
        var tool = Path.Combine(h.Root, "dbdatasync");
        File.WriteAllText(tool, $"#!/bin/bash\nexec dotnet \"{Programs.Sync}\" \"$@\"\n", new UTF8Encoding(false));
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(directory, new Uri("https://127.0.0.1:0")) { SyncExecutable = tool }, WorkspaceServer.Certificate(directory), _ => { });
        await workspace.StartAsync();
        var instances = h.Console.Services.GetRequiredService<ConsoleInstances>();
        var code = WorkspaceState.Pairing(directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
        workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), code.Code, "Workspace", default)).InstanceId;
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

    private static string Mapping(string? filter) => $"""
        schemaVersion: 1
        name: main
        sources:
        - schema: dbo
          table: region_src
        {(filter is null ? "" : $"  filter: \"{filter}\"\n")}targets:
        - schema: public
          table: region_tgt
        columnMappings:
        - sourceColumn: id
          targetColumn: id
        - sourceColumn: name
          targetColumn: name
        - sourceColumn: region
          targetColumn: region

        """;

    [Fact]
    public async Task A_preview_sent_the_drafts_mapping_with_a_region_filter_answers_only_that_region_over_a_version_without_it_and_its_run_record_keeps_the_text()
    {
        await h.ExecuteSourceAsync($"CREATE TABLE dbo.region_src (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NOT NULL, region INT NOT NULL); INSERT INTO dbo.region_src VALUES (1, '{OtherRegion}', 1), (2, 'b', 2), (3, 'c', 2);");

        const string project = "regions-sync";
        var made = await Workspace(HttpMethod.Post, "/projects", new JsonObject { ["id"] = project, ["kind"] = "sync" });
        Assert.True(made.Status == HttpStatusCode.Created, made.Body?.ToJsonString());
        var draft = (string)(await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces", new JsonObject { ["name"] = "draft" })).Body!["id"]!;
        foreach (var template in new[] { "config/connections/source-db.yaml", "config/connections/target-db.yaml", "config/replications/orders/table-mappings/orders.yaml" })
            await Workspace(HttpMethod.Delete, $"/projects/{project}/workspaces/{draft}/files/{template}");
        foreach (var name in new[] { "src", "tgt" })
            await Put(project, draft, $"config/connections/{name}.yaml", File.ReadAllText(Path.Combine(h.SyncDirectory, "config", "connections", name + ".yaml")));
        await Put(project, draft, "config/replications/regional/task.yaml", $"""
            schemaVersion: 2
            name: regional
            scheduling:
              mode: Periodic
              cronExpression: 0 3 * * *
            changeProcessing:
              reader:
                kind: BatchReload
              cache:
                kind: StagingTable
              writer:
                kind: DeleteInsert
            endpoints:
              source:
                connectionName: src
                database: {h.SourceDatabase}
              target:
                connectionName: tgt
                database: {h.TargetDatabase}

            """);
        // the version is built from a mapping without a filter
        await Put(project, draft, "config/replications/regional/table-mappings/main.yaml", Mapping(null));

        var requested = await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces/{draft}/builds");
        Assert.True(requested.Status == HttpStatusCode.Accepted, requested.Body?.ToJsonString());
        var build = (string)requested.Body!["id"]!;
        for (var i = 0; ; i++)
        {
            var status = (string)(await Workspace(HttpMethod.Get, $"/projects/{project}/workspaces/{draft}/builds/{build}")).Body!["build"]!["status"]!;
            if (status == "succeeded") break;
            Assert.True(status != "failed" && i < 1200, $"the build {status}");
            await Task.Delay(250);
        }

        var staged = await h.ConsoleCall(HttpMethod.Post, "/api/deploys", new JsonObject
        {
            ["workspaceServer"] = workspaceId, ["project"] = project, ["workspace"] = draft, ["build"] = build, ["target"] = h.RunnerId, ["channel"] = SyncOnRunner.Channel, ["serve"] = false,
            ["bind"] = new JsonObject { ["src"] = "src", ["tgt"] = "tgt" },
        });
        Assert.True(staged.Status == HttpStatusCode.OK, $"{staged.Status} {staged.Body}");
        var deploy = (string)staged.Body!["deploy"]!["id"]!;
        h.Step("staged");

        // the draft's mapping, as the person edits it: filtered on region 2, not saved, built or published
        var text = Mapping("region = 2");
        var (started, body) = await h.ConsoleCall(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject
        {
            ["task"] = "preview", ["answer"] = true,
            ["inputs"] = new JsonObject { ["replication"] = "regional", ["mapping"] = "main", ["rows"] = "20", ["repo_dir"] = "repo", ["mapping_text"] = text },
        });
        var until = DateTime.UtcNow.AddMinutes(3);
        while (started == HttpStatusCode.Accepted && DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            (started, body) = await h.ConsoleCall(HttpMethod.Get, $"/api/deploys/{deploy}/runs/{(string)body!["run_id"]!}/answer");
        }
        Assert.True(started == HttpStatusCode.OK, $"{started} {body}\n{await h.Diagnose("preview")}");
        var answer = body!["answer"]!;
        var rows = answer["rows"]!.AsArray();
        Assert.Equal([2, 2], rows.Select(r => (int)r![2]!).ToArray());
        Assert.DoesNotContain(OtherRegion, body.ToJsonString());

        // the run's record keeps the mapping's text as an input
        var run = Guid.Parse((string)body["run_id"]!);
        var state = StateDatabase.OpenModules(Path.Combine(h.RunnerDirectory, "state.db"), DbDataKit.Runner.RunnerNames.EngineModules, new StateScope(Group: "main", Service: DbDataKit.Runner.RunnerNames.ServiceId));
        var item = new ModuleWorkQueueStore(state, DbDataKit.Runner.RunnerNames.EngineService).FindByRun(run);
        Assert.NotNull(item);
        var inputs = JsonNode.Parse(item.PayloadJson!)!["inputs"]!;
        Assert.Equal(text, (string?)inputs["mapping_text"]);
    }
}
