using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The scripts of a Sync draft (plan 33 SC22k): the list runs through <c>dbdatasync script list --json</c> on a copy of the draft, offline, the draft untouched. The Workspace server never tests a script: that runs the
/// draft's C#, which is the set's task <c>script-test</c> on a Runner (Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner), and the route that did it answers where it went. These run the stand-in
/// <see cref="FakeSync"/> for the mechanics; the real program is the test of <c>DBDATAKIT_TEST_DBDATASYNC</c> (<see cref="WorkspaceSyncKindTests"/>).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceSyncScriptTests
{
    private static string Fake()
    {
        var path = FakeSync.Create();
        if (path is null) Assert.Skip("the stand-in dbdatasync is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"orders-set","kind":"sync"}""")).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders-set/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static Task<(HttpResponseMessage Response, JsonNode? Body)> Post(WorkspaceHarness server, string id, string route, string json) =>
        server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{id}/{route}", json);

    [Fact]
    public async Task A_new_sync_project_has_a_scripts_folder_with_a_manifest_and_its_code()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);

        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("config/scripts/upper-name.yaml", files);
        Assert.Contains("config/scripts/upper-name.csx", files);
        var manifest = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files/config/scripts/upper-name.yaml")).Body!["content"]!;
        Assert.Contains("kind: sqlColumnExpression", manifest);
    }

    [Fact]
    public async Task The_scripts_of_the_draft_are_listed_with_their_slot()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);

        var listed = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/scripts");
        Assert.Equal(HttpStatusCode.OK, listed.Response.StatusCode);
        Assert.True((bool)listed.Body!["ok"]!, listed.Body.ToJsonString());
        var script = Assert.Single(listed.Body["data"]!["scripts"]!.AsArray())!;
        Assert.Equal(("upper-name", "sqlColumnExpression"), ((string)script["name"]!, (string)script["kind"]!));
    }

    [Fact]
    public async Task The_route_that_tested_a_script_says_it_runs_on_a_Runner_and_starts_no_program()
    {
        var fake = Fake();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: fake);
        var id = await DraftAsync(server);

        var tested = await Post(server, id, "script-test", """{"name":"upper-name","rows":3}""");
        Assert.Equal(HttpStatusCode.Gone, tested.Response.StatusCode);
        Assert.Equal(WorkspaceManagement.ScriptTestMoved, (string)tested.Body!["error"]!);
        Assert.Contains("on a Runner", WorkspaceManagement.ScriptTestMoved);
        var arguments = Path.Combine(Path.GetDirectoryName(fake)!, "arguments.txt");
        Assert.False(File.Exists(arguments) && File.ReadAllText(arguments).Contains("script test"), "the Workspace server started dbdatasync script test");
    }

    [Fact]
    public async Task A_build_project_has_no_sync_scripts()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake(), buildExecutable: FakeBuild.Create());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","kind":"build"}""")).Response.StatusCode);
        var other = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/shop/workspaces/{other}/scripts")).Response.StatusCode);
    }

    /// <summary>What the program printed on its standard output.</summary>
    private static async Task<string> RunAsync(string program, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await error;
        return await output;
    }

    /// <summary>The skeletons the Scripts tab writes (the same text as <c>Shared/web/workspace/src/syncScripts.ts</c>, kept in step by this test) compile and run in the real program.</summary>
    private static readonly (string Kind, string Code)[] Skeletons =
    [
        ("valueColumnExpression", "using System;\nusing System.Collections.Generic;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing DbDataKit.Sync.Scripting.Abstractions;\n\npublic sealed class MyScript : IValueColumnExpression\n{\n    public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext context) => [\"Name\"];\n\n    public object? Evaluate(object? value, ValueColumnExpressionContext context) => value is string text ? text.ToUpperInvariant() : value;\n}\n\nnew MyScript()\n"),
        ("rowTransform", "using System;\nusing System.Collections.Generic;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing DbDataKit.Sync.Scripting.Abstractions;\n\npublic sealed class MyScript : IRowTransform\n{\n    public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext context) => input;\n\n    public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext context, CancellationToken cancellationToken) => ValueTask.FromResult<ChangeRow?>(row);\n}\n\nnew MyScript()\n"),
    ];

    [Fact]
    public async Task The_real_program_lists_and_tests_the_templates_script_and_the_skeletons_of_the_other_slots()
    {
        var real = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_DBDATASYNC");
        Assert.False(string.IsNullOrEmpty(real) && string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_SYNC"), "true", StringComparison.OrdinalIgnoreCase), "dbdatasync is required but DBDATAKIT_TEST_DBDATASYNC is not set.");
        if (string.IsNullOrEmpty(real) || !File.Exists(real)) Assert.Skip("DBDATAKIT_TEST_DBDATASYNC is not set (a build of DbDataSync/src/DbDataSync.Cli)");
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: real);
        var id = await DraftAsync(server);
        foreach (var (kind, code) in Skeletons)
        {
            var name = "my-" + kind.ToLowerInvariant();
            Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/config/scripts/{name}.yaml", new JsonObject { ["content"] = $"schemaVersion: 1\nname: {name}\nkind: {kind}\nlanguage: CSharp\nparameters: []\nenabled: true\n" }.ToJsonString())).Response.IsSuccessStatusCode);
            Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/config/scripts/{name}.csx", new JsonObject { ["content"] = code }.ToJsonString())).Response.IsSuccessStatusCode);
        }

        var listed = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/scripts");
        Assert.True((bool)listed.Body!["ok"]!, listed.Body.ToJsonString());
        var names = listed.Body["data"]!["scripts"]!.AsArray().Select(s => (string)s!["name"]!).ToList();
        Assert.Contains("upper-name", names);
        Assert.Equal(3, names.Count);

        // each script compiles and runs in the real program's sample test (on a Runner, the set's task script-test runs the same test), on a folder of its own: the Workspace server runs none
        var repo = Directory.CreateTempSubdirectory("ddw-script-skeletons-").FullName;
        try
        {
            foreach (var name in names)
                foreach (var extension in new[] { ".yaml", ".csx" })
                {
                    var path = Path.Combine(repo, "config", "scripts", name + extension);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders-set/workspaces/{id}/files/config/scripts/{name}{extension}")).Body!["content"]!);
                }

            foreach (var name in names)
            {
                var tested = JsonNode.Parse(await RunAsync(real, "script", "test", name, "--json", "--repo", repo))!;
                Assert.True((bool)tested["ok"]!, tested.ToJsonString());
                Assert.NotEmpty(tested["cases"]!.AsArray());
            }
            Assert.Contains("UPPER(", await RunAsync(real, "script", "test", "upper-name", "--json", "--repo", repo));

            File.WriteAllText(Path.Combine(repo, "config", "scripts", "upper-name.csx"), "this is not C#;\nnew X()");
            var broken = JsonNode.Parse(await RunAsync(real, "script", "test", "upper-name", "--json", "--repo", repo))!;
            Assert.False((bool)broken["ok"]!);
            Assert.Contains("did not compile", (string)broken["error"]!);
        }
        finally { Directory.Delete(repo, recursive: true); }
    }
}
