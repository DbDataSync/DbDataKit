using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The <c>integration</c> kind (plan 27a IX20, docs/integrations.md): a new project is a manifest, a job class and a test; a draft is checked, tested and built by <c>dbdatarunner package build|test</c> as a child process (stand-ins here,
/// <see cref="FakeIntegrationTools"/>; the real program is the test of <c>DBDATAKIT_TEST_DBDATARUNNER</c>); a compiled project needs the SDK and a server without one says so; the package is staged on a channel of a (fake) Runner.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceIntegrationKindTests
{
    private const string Project = "orders-pull";

    private static string Tool()
    {
        var path = FakeIntegrationTools.Runner();
        if (path is null) Assert.Skip("the stand-in dbdatarunner is a shell script");
        return path;
    }

    private static async Task<string> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "integration", ["title"] = "Orders pull" }.ToJsonString())).Response.StatusCode);
        return (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
    }

    private static async Task<JsonNode> CheckAsync(WorkspaceHarness server, string id) => (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{id}/check")).Body!;

    private static async Task Put(WorkspaceHarness server, string id, string path, string content) =>
        Assert.True((await server.AsAda(HttpMethod.Put, $"/api/v1/projects/{Project}/workspaces/{id}/files/{path}", new JsonObject { ["content"] = content }.ToJsonString())).Response.IsSuccessStatusCode);

    private static async Task<string> Read(WorkspaceHarness server, string id, string path) =>
        (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{id}/files/{path}")).Body!["content"]!;

    private static async Task<JsonNode> BuildAsync(WorkspaceHarness server, string workspace, bool expectSuccess)
    {
        var made = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds");
        Assert.Equal(HttpStatusCode.Accepted, made.Response.StatusCode);
        var build = (string)made.Body!["id"]!;
        for (var i = 0; i < 300; i++)
        {
            var detail = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}")).Body!;
            var status = (string)detail["build"]!["status"]!;
            if (status is "succeeded" or "failed") { Assert.Equal(expectSuccess ? "succeeded" : "failed", status); detail["id"] = build; return detail; }
            await Task.Delay(100);
        }
        throw new TimeoutException("the build did not finish");
    }

    [Fact]
    public async Task A_new_integration_project_is_a_manifest_a_job_class_and_a_test_and_its_manifest_is_one_the_runner_reads()
    {
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: Tool());
        var id = await DraftAsync(server);

        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{id}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("package.yaml", files);
        Assert.Contains("src/hello.csx", files);
        Assert.Contains("tests/hello.csx", files);
        Assert.Contains("bin/", await Read(server, id, ".gitignore"));
        Assert.Contains("Orders pull", await Read(server, id, "README.md"));
        var manifest = PackageManifestReader.Read(await Read(server, id, "package.yaml"));
        Assert.Equal("orders-pull", manifest.Name);
        Assert.Equal("bin/orders-pull.dll", manifest.Launch.Assembly);
        Assert.Equal(["hello"], manifest.Tasks.Select(t => t.Name));
        Assert.Contains("[RunnerTask(\"hello\")]", await Read(server, id, "src/hello.csx"));

        var check = await CheckAsync(server, id);
        Assert.Equal("integration", (string)check["kind"]!);
        Assert.True((bool)check["ok"]!, check.ToJsonString());
    }

    [Fact]
    public async Task A_source_that_does_not_compile_is_a_finding_with_its_file_and_line_and_the_build_fails()
    {
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: Tool());
        var id = await DraftAsync(server);
        await Put(server, id, "src/hello.csx", "namespace Integration;\n// BROKEN\nclass X {\n");

        var finding = (await CheckAsync(server, id))["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("src/hello.csx", (string)finding["file"]!);
        Assert.Equal(3, (int)finding["line"]!);

        var build = await BuildAsync(server, id, expectSuccess: false);
        Assert.Contains("src/hello.csx", build.ToJsonString());
    }

    [Fact]
    public async Task A_build_runs_the_tests_first_and_a_failing_test_fails_it_naming_the_test_file()
    {
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: Tool());
        var id = await DraftAsync(server);
        var passing = await BuildAsync(server, id, expectSuccess: true);
        var info = passing["result"]!["info"]!;
        Assert.Equal("orders-pull", (string)info["package"]!);
        Assert.Equal("hello", (string)info["tasks"]!);
        Assert.Contains("1 test(s) passed", (string)info["tests"]!);
        Assert.Equal("csx", (string)info["language"]!);

        await Put(server, id, "tests/hello.csx", "// FAILING\nEqual(1, 2);\n");
        var failing = await BuildAsync(server, id, expectSuccess: false);
        Assert.Contains("tests/hello.csx", failing.ToJsonString());
        Assert.Contains("The values differ", failing.ToJsonString());
    }

    [Fact]
    public async Task The_child_is_told_where_to_find_packages_and_does_not_get_the_servers_environment()
    {
        var program = Tool();
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: program, integrationSources: ["/feeds/offline"]);
        var id = await DraftAsync(server);
        Assert.True((bool)(await CheckAsync(server, id))["ok"]!);
        var arguments = File.ReadAllText(Path.Combine(Path.GetDirectoryName(program)!, "arguments.txt"));
        Assert.Contains("package build", arguments);
        Assert.Contains("--no-lock-write", arguments);                    // a check writes nothing into the draft
        Assert.Contains("--source /feeds/offline", arguments);
        Assert.Contains("--cache ", arguments);
        Assert.Contains("--format json", arguments);
    }

    [Fact]
    public async Task A_server_with_no_dbdatarunner_says_what_to_install()
    {
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: "/no/such/dbdatarunner");
        var id = await DraftAsync(server);
        var finding = (await CheckAsync(server, id))["diagnostics"]!.AsArray().Single(d => (string)d!["severity"]! == "error")!;
        Assert.Contains("dbdatarunner is not beside it or on its PATH", (string)finding["message"]!);
    }

    [Fact]
    public async Task A_compiled_project_on_a_server_with_no_sdk_is_told_so_in_the_check_and_the_build_and_not_with_a_failing_build_program()
    {
        var dotnet = FakeIntegrationTools.Dotnet(withSdk: false);
        if (dotnet is null) Assert.Skip("the stand-in dotnet is a shell script");
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: Tool(), dotnetExecutable: dotnet);
        var id = await DraftAsync(server);
        await Put(server, id, "src/Orders.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var finding = (await CheckAsync(server, id))["diagnostics"]!.AsArray().Single(d => (string)d!["severity"]! == "error")!;
        Assert.Equal("DBD-WS-INTEGRATION-SDK", (string)finding["code"]!);
        Assert.Contains("needs the .NET SDK", (string)finding["message"]!);
        Assert.Contains("C# files under src/ (csx)", (string)finding["message"]!);
        Assert.Contains("needs the .NET SDK", (await BuildAsync(server, id, expectSuccess: false)).ToJsonString());
    }

    [Fact]
    public async Task A_compiled_project_on_a_server_with_the_sdk_is_published_with_dotnet_and_its_package_is_the_manifest_and_the_output()
    {
        var dotnet = FakeIntegrationTools.Dotnet(withSdk: true);
        if (dotnet is null) Assert.Skip("the stand-in dotnet is a shell script");
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: Tool(), dotnetExecutable: dotnet);
        var id = await DraftAsync(server);
        await Put(server, id, "src/Orders.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await Put(server, id, "package.yaml", (await Read(server, id, "package.yaml")).Replace("bin/orders-pull.dll", "bin/Orders.dll"));
        Assert.True((bool)(await CheckAsync(server, id))["ok"]!);

        var build = await BuildAsync(server, id, expectSuccess: true);
        var info = build["result"]!["info"]!;
        Assert.Equal("compiled", (string)info["language"]!);
        Assert.Equal("hello", (string)info["tasks"]!);
    }

    [Fact]
    public async Task A_built_integration_is_staged_on_a_channel_of_a_runner_by_the_publisher_key()
    {
        await using var runner = await FakeRunner.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: Tool());
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/jobs/pair", new JsonObject { ["url"] = runner.Url.ToString(), ["code"] = runner.NewCode() }.ToJsonString())).Response.StatusCode);
        var id = await DraftAsync(server);
        var build = await BuildAsync(server, id, expectSuccess: true);

        var published = await server.Send("grace", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{id}/builds/{(string)build["id"]!}/publish", """{"target":"jobs","channel":"prod"}""");
        Assert.Equal(HttpStatusCode.OK, published.Response.StatusCode);
        var answer = published.Body!["published"]!;
        Assert.Equal(("orders-pull", "prod", "stage"), ((string)answer["package"]!, (string)answer["channel"]!, (string)answer["mode"]!));
        var sent = Assert.Single(runner.Published);
        Assert.Contains("name: orders-pull", (string)sent["manifest"]!);
        Assert.Contains("bin/orders-pull.dll", sent["artifacts"]!.AsArray().Select(a => (string)a!["path"]!));
    }

    [Fact]
    public async Task The_real_program_checks_tests_and_builds_the_template_with_no_edits()
    {
        var real = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_DBDATARUNNER");
        Assert.False(string.IsNullOrEmpty(real) && string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_RUNNER_TOOL"), "true", StringComparison.OrdinalIgnoreCase), "dbdatarunner is required and DBDATAKIT_TEST_DBDATARUNNER is not set");
        if (string.IsNullOrEmpty(real) || !File.Exists(real)) Assert.Skip("DBDATAKIT_TEST_DBDATARUNNER is not set (a build of DbDataRunner/src/DbDataRunner.Server)");
        await using var server = await WorkspaceHarness.StartAsync(runnerExecutable: real);
        var id = await DraftAsync(server);
        var check = await CheckAsync(server, id);
        Assert.True((bool)check["ok"]!, check.ToJsonString());
        var build = await BuildAsync(server, id, expectSuccess: true);
        Assert.Contains("1 test(s) passed", (string)build["result"]!["info"]!["tests"]!);
        Assert.Equal("hello", (string)build["result"]!["info"]!["tasks"]!);

        await Put(server, id, "tests/hello.csx", (await Read(server, id, "tests/hello.csx")).Replace("Equal(1L", "Equal(2L"));
        var failing = await BuildAsync(server, id, expectSuccess: false);
        Assert.Contains("tests/hello.csx", failing.ToJsonString());
    }
}
