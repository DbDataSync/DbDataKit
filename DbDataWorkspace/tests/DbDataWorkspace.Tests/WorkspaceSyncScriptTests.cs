using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The scripts of a Sync draft (plan 33 SC22k): the list and the test of one run through <c>dbdatasync script list|test --json</c> on a copy of the draft, offline, the draft untouched; testing needs the Author role because it runs the draft's C#. These run the
/// stand-in <see cref="FakeSync"/> for the mechanics; the real program is the test of <c>DBDATAKIT_TEST_DBDATASYNC</c> (<see cref="WorkspaceSyncKindTests"/>).
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
    public async Task A_script_is_tested_on_a_copy_and_the_cases_come_back_as_the_tool_printed_them()
    {
        var fake = Fake();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: fake);
        var id = await DraftAsync(server);

        var tested = await Post(server, id, "script-test", """{"name":"upper-name","rows":3}""");
        Assert.Equal(HttpStatusCode.OK, tested.Response.StatusCode);
        Assert.True((bool)tested.Body!["ok"]!, tested.Body.ToJsonString());
        var data = tested.Body["data"]!;
        Assert.True((bool)data["ok"]!);
        Assert.Contains("generated sample", (string)data["source"]!);
        Assert.Contains("UPPER", (string)data["cases"]![0]!["output"]!);

        // the program was asked for the JSON form, with the rows, on a folder that is not the draft's
        var arguments = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fake)!, "arguments.txt")).Last();
        Assert.Contains("script test upper-name --rows 3 --json --repo ", arguments);
        Assert.DoesNotContain(id, arguments.Split(' ').Last());
    }

    [Fact]
    public async Task A_script_that_does_not_compile_is_an_answer_with_the_compilers_words_not_an_error_of_the_route()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/config/scripts/upper-name.csx", new JsonObject { ["content"] = "this is not C#;\nnew X()" }.ToJsonString())).Response.IsSuccessStatusCode);

        var tested = await Post(server, id, "script-test", """{"name":"upper-name"}""");
        Assert.Equal(HttpStatusCode.OK, tested.Response.StatusCode);
        Assert.False((bool)tested.Body!["data"]!["ok"]!);
        Assert.Contains("did not compile", (string)tested.Body["data"]!["error"]!);
    }

    [Fact]
    public async Task A_script_that_does_not_exist_is_named_and_the_path_of_the_servers_copy_is_not_shown()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake());
        var id = await DraftAsync(server);

        var tested = await Post(server, id, "script-test", """{"name":"nope"}""");
        var error = (string)tested.Body!["data"]!["error"]!;
        Assert.Contains("'nope' does not exist", error);
        Assert.DoesNotContain("ddw-sync-", error);
    }

    [Theory]
    [InlineData("""{"name":"../x"}""")]
    [InlineData("""{"name":"-rf"}""")]
    [InlineData("""{"name":"a/b"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"name":"ok","rows":0}""")]
    [InlineData("""{"name":"ok","rows":51}""")]
    public async Task The_body_is_checked_before_anything_runs(string body)
    {
        var fake = Fake();
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: fake);
        var id = await DraftAsync(server);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(server, id, "script-test", body)).Response.StatusCode);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fake)!, "arguments.txt")) && File.ReadAllText(Path.Combine(Path.GetDirectoryName(fake)!, "arguments.txt")).Contains("script test"));
    }

    [Fact]
    public async Task Running_a_script_needs_the_author_role_and_a_build_project_has_no_sync_scripts()
    {
        await using var server = await WorkspaceHarness.StartAsync(syncExecutable: Fake(), buildExecutable: FakeBuild.Create());
        var id = await DraftAsync(server);

        var viewer = await server.Send("vic", ServerRole.Viewer, HttpMethod.Post, $"/api/v1/projects/orders-set/workspaces/{id}/script-test", """{"name":"upper-name"}""");
        Assert.Equal(HttpStatusCode.Forbidden, viewer.Response.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","kind":"build"}""")).Response.StatusCode);
        var other = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await server.AsAda(HttpMethod.Post, $"/api/v1/projects/shop/workspaces/{other}/script-test", """{"name":"x"}""")).Response.StatusCode);
    }

    /// <summary>The skeletons the Scripts tab writes (the same text as <c>Shared/web/workspace/src/syncScripts.ts</c>, kept in step by this test) compile and run in the real program.</summary>
    private static readonly (string Kind, string Code)[] Skeletons =
    [
        ("valueColumnExpression", "using System;\nusing System.Collections.Generic;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing DbDataSync.Scripting.Abstractions;\n\npublic sealed class MyScript : IValueColumnExpression\n{\n    public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext context) => [\"Name\"];\n\n    public object? Evaluate(object? value, ValueColumnExpressionContext context) => value is string text ? text.ToUpperInvariant() : value;\n}\n\nnew MyScript()\n"),
        ("rowTransform", "using System;\nusing System.Collections.Generic;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing DbDataSync.Scripting.Abstractions;\n\npublic sealed class MyScript : IRowTransform\n{\n    public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext context) => input;\n\n    public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext context, CancellationToken cancellationToken) => ValueTask.FromResult<ChangeRow?>(row);\n}\n\nnew MyScript()\n"),
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
        foreach (var name in names)
        {
            var tested = await Post(server, id, "script-test", new JsonObject { ["name"] = name }.ToJsonString());
            Assert.True((bool)tested.Body!["data"]!["ok"]!, tested.Body.ToJsonString());
            Assert.NotEmpty(tested.Body["data"]!["cases"]!.AsArray());
        }
        Assert.Contains("UPPER(", (await Post(server, id, "script-test", """{"name":"upper-name"}""")).Body!.ToJsonString());

        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/orders-set/workspaces/{id}/files/config/scripts/upper-name.csx", new JsonObject { ["content"] = "this is not C#;\nnew X()" }.ToJsonString())).Response.IsSuccessStatusCode);
        var broken = (await Post(server, id, "script-test", """{"name":"upper-name"}""")).Body!["data"]!;
        Assert.False((bool)broken["ok"]!);
        Assert.Contains("did not compile", (string)broken["error"]!);
    }
}
