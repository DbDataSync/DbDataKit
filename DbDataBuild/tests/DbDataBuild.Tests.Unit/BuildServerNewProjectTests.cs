using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// New projects and models from the console (plan 13, W07): the template catalog and making a project on the server (the author role, on the server itself), a project made is a git repository that workspaces can be made from, and the commands
/// the screens run for a new model and for an import (writing files needs the author role even where the command reads a connection). The commands run for real, in child processes, against a DuckDB file as the origin: no engine is needed.
/// </summary>
public sealed class BuildServerNewProjectTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-newproject-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private ProjectsFile projects = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;
    private string origin = null!;

    private string ServerDir => Path.Combine(root, "server");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ServerDir);
        // a DuckDB file with the retail template's source tables: the origin an import reads
        var retail = Path.Combine(root, "retail-source");
        Assert.Equal(0, CliApp.Run(["project", "create", "retail", retail, "--format", "json"], new StringWriter(), new StringWriter()));
        origin = Path.Combine(root, "origin.duckdb");
        Assert.Equal(0, CliApp.Run(["project", "seed", "--project", retail, "--out", origin, "--format", "json"], new StringWriter(), new StringWriter()));
        var store = ServerSecrets.Open(ServerDir);
        await store.SetAsync(ServerSecrets.NameOf("origin", LoginRole.Read), SecretValue.From(origin));

        projects = new ProjectsFile(ServerDir);
        var settings = new BuildServerSettings(ServerDir, new Uri("https://127.0.0.1:0"));
        using var sessions = new WarmSessionRunner(logins: token => ServerSecrets.LoginsAsync(store, token));
        app = BuildServer.Build(settings, BuildServer.Certificate(ServerDir), new ChildProcessCommandRunner(logins: token => ServerSecrets.LoginsAsync(store, token)), projects, new TuiCommand.CliHost(_ => null).Commands, _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Approver | ServerRole.Author));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch (IOException) { }
    }

    private async Task<HttpResponseMessage> Send(string who, ServerRole role, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, who, who, role)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private Task<HttpResponseMessage> AsAda(HttpMethod method, string path, string? body = null) => Send("ada", ServerRole.Author, method, path, body);
    private Task<HttpResponseMessage> AsVic(HttpMethod method, string path, string? body = null) => Send("vic", ServerRole.Viewer, method, path, body);

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static async Task<JsonNode> Document(HttpResponseMessage response) => (await Json(response))["document"]!;

    [Fact]
    public async Task The_templates_are_listed_for_everyone_and_only_an_author_may_make_a_project_from_one()
    {
        var asAuthor = await Json(await AsAda(HttpMethod.Get, "/api/v1/templates"));
        Assert.Contains("retail", asAuthor["templates"]!.AsArray().Select(t => (string)t!["name"]!));
        Assert.False(string.IsNullOrWhiteSpace((string)asAuthor["templates"]!.AsArray().First(t => (string)t!["name"]! == "retail")!["description"]!));
        Assert.True((bool)asAuthor["can_create"]!);

        var asViewer = await Json(await AsVic(HttpMethod.Get, "/api/v1/templates"));
        Assert.Equal(asAuthor["templates"]!.AsArray().Count, asViewer["templates"]!.AsArray().Count);
        Assert.False((bool)asViewer["can_create"]!);
    }

    [Fact]
    public async Task The_servers_lineage_is_the_graph_of_its_projects_for_anyone_who_may_view_and_a_console_can_read_it()
    {
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","template":"retail"}""")).StatusCode);

        var response = await AsVic(HttpMethod.Get, "/api/v1/lineage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await Json(response);
        var graph = DbDataKit.Lineage.LineageJson.Read(document);
        Assert.Contains(graph.Nodes, n => n.Name == "marts.dim_customer");
        Assert.NotEmpty(graph.Edges);
        Assert.Empty(document["problems"]!.AsArray());
    }

    [Fact]
    public async Task A_project_made_from_a_template_is_hosted_a_repository_with_the_author_as_its_first_committer_and_has_workspaces()
    {
        var made = await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","template":"retail"}""");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var folder = Path.Combine(ServerDir, "projects", "shop");
        Assert.True(File.Exists(Path.Combine(folder, "dbdatabuild.yml")));
        Assert.True(File.Exists(Path.Combine(folder, ".gitignore")));
        using (var repo = new Repository(folder))
        {
            var only = Assert.Single(repo.Commits);
            Assert.Equal("ada", only.Author.Name);
            Assert.Contains("retail", only.MessageShort);
            Assert.True(repo.RetrieveStatus().IsDirty is false);
        }

        var listed = (await Json(await AsVic(HttpMethod.Get, "/api/v1/projects")))["projects"]!.AsArray();
        Assert.Contains(listed, p => (string)p!["id"]! == "shop" && (string)p["source"]! == "path");
        Assert.Contains("shop", new ProjectsFile(ServerDir).All().Select(p => p.Id));

        // the project reads, and a workspace can be made from it
        var graph = await Document(await AsVic(HttpMethod.Post, "/api/v1/projects/shop/commands/project_show_graph", "{}"));
        Assert.True((bool)graph["ok"]!);
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects/shop/workspaces", """{"name":"draft"}""")).StatusCode);
    }

    [Fact]
    public async Task A_project_is_refused_for_a_viewer_a_taken_or_invalid_name_an_unknown_template_and_a_body_that_is_not_one()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await AsVic(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","template":"retail"}""")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(ServerDir, "projects", "shop")));

        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","template":"retail"}""")).StatusCode);
        var taken = await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"shop","template":"starter"}""");
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("already hosts", (string)(await Json(taken))["error"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"Has Spaces","template":"retail"}""")).StatusCode);
        var unknown = await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"other","template":"nope"}""");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("retail", (string)(await Json(unknown))["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, "/api/v1/projects", "not json")).StatusCode);

        // a folder the server already has under that name is not written over
        Directory.CreateDirectory(Path.Combine(ServerDir, "projects", "kept"));
        File.WriteAllText(Path.Combine(ServerDir, "projects", "kept", "mine.txt"), "mine");
        Assert.Equal(HttpStatusCode.Conflict, (await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"kept","template":"retail"}""")).StatusCode);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(ServerDir, "projects", "kept", "mine.txt")));
    }

    /// <summary>A project with a connection to the DuckDB origin, and a workspace of it for ada.</summary>
    private async Task<(string Project, string Workspace)> ProjectWithOrigin()
    {
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"imp","template":"starter"}""")).StatusCode);
        var folder = Path.Combine(ServerDir, "projects", "imp");
        File.WriteAllText(Path.Combine(folder, "connections", "origin.yaml"), "schema: connection/1\nname: origin\nengine: duckdb\naccess: readonly\n");
        using (var repo = new Repository(folder))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("ada", "ada@example.com", DateTimeOffset.UtcNow);
            repo.Commit("An origin connection", who, who);
        }
        var workspace = (string)(await Json(await AsAda(HttpMethod.Post, "/api/v1/projects/imp/workspaces", """{"name":"draft"}""")))["id"]!;
        return ("imp", workspace);
    }

    [Fact]
    public async Task A_model_is_created_in_the_workspace_only_and_by_an_author()
    {
        var (project, workspace) = await ProjectWithOrigin();
        var body = """{"model":"marts.fct_returns","kind":"view"}""";

        Assert.Equal(HttpStatusCode.Forbidden, (await AsVic(HttpMethod.Post, $"/api/v1/projects/{project}/commands/project_model_create?workspace={workspace}", body)).StatusCode);   // a viewer holds no workspace
        var createdAnswer = await AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/commands/project_model_create?workspace={workspace}", body);
        var created = JsonNode.Parse(await createdAnswer.Content.ReadAsStringAsync())!["document"] ?? throw new Xunit.Sdk.XunitException(await createdAnswer.Content.ReadAsStringAsync());
        Assert.True((bool)created["ok"]!);
        Assert.Equal(["models/marts/fct_returns.yml", "models/marts/fct_returns.sql"], created["data"]!["files"]!.AsArray().Select(f => (string)f!).ToArray());
        var draft = Path.Combine(ServerDir, "workspaces", project, workspace);
        Assert.True(File.Exists(Path.Combine(draft, "models", "marts", "fct_returns.sql")));
        Assert.False(File.Exists(Path.Combine(ServerDir, "projects", project, "models", "marts", "fct_returns.sql")));

        // the same name again is refused in words, and writes nothing more
        var again = await Document(await AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/commands/project_model_create?workspace={workspace}", body));
        Assert.False((bool)again["ok"]!);
        Assert.Contains("already exists", (string)again["errors"]![0]!);
    }

    [Fact]
    public async Task Importing_lists_the_origins_tables_for_a_viewer_and_writing_them_needs_the_author_role()
    {
        var (project, workspace) = await ProjectWithOrigin();
        var run = $"/api/v1/projects/{project}/commands/project_import";

        // what a person sees before choosing: the tables the origin has that the project does not map yet, with their columns
        var preview = await Document(await AsAda(HttpMethod.Post, $"{run}?workspace={workspace}", """{"connection":"origin","tables":["shop.*"]}"""));
        Assert.True((bool)preview["ok"]!, preview.ToJsonString());
        var rows = preview["data"]!["mapped_models"]!.AsArray();
        Assert.Contains("shop.orders", rows.Select(r => (string)r!["name"]!));
        Assert.All(rows, r => Assert.Equal("new", (string)r!["status"]!));
        Assert.False(File.Exists(Path.Combine(ServerDir, "workspaces", project, workspace, "models", "shop", "orders.yml")));

        // a viewer's write is refused before anything runs; the same call without the flag is a read
        var refused = await AsVic(HttpMethod.Post, run, """{"connection":"origin","tables":["shop.orders"],"write":true}""");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("author", (string)(await Json(refused))["error"]!);
        Assert.False(File.Exists(Path.Combine(ServerDir, "projects", project, "models", "shop", "orders.yml")));
        Assert.Equal(HttpStatusCode.OK, (await AsVic(HttpMethod.Post, run, """{"connection":"origin","tables":["shop.orders"]}""")).StatusCode);

        // the author's write goes to the workspace and not to the checkout the scheduler reads, and no login comes back
        var written = await AsAda(HttpMethod.Post, $"{run}?workspace={workspace}", """{"connection":"origin","tables":["shop.orders","shop.customers"],"write":true}""");
        var text = await written.Content.ReadAsStringAsync();
        Assert.DoesNotContain(origin, text);
        Assert.DoesNotContain(Path.GetFileName(origin), text);
        var document = JsonNode.Parse(text)!["document"]!;
        Assert.True((bool)document["ok"]!, text);
        Assert.Equal(["models/shop/customers.yml", "models/shop/orders.yml"], document["data"]!["written"]!.AsArray().Select(f => (string)f!).Order().ToArray());
        Assert.True(File.Exists(Path.Combine(ServerDir, "workspaces", project, workspace, "models", "shop", "orders.yml")));
        Assert.False(File.Exists(Path.Combine(ServerDir, "projects", project, "models", "shop", "orders.yml")));
    }

    [Fact]
    public async Task Updating_definitions_with_write_needs_the_author_role_too_and_asks_its_questions_instead_of_guessing()
    {
        var (project, workspace) = await ProjectWithOrigin();
        var update = $"/api/v1/projects/{project}/commands/project_model_update";
        Assert.Equal(HttpStatusCode.Forbidden, (await AsVic(HttpMethod.Post, update, """{"write":true,"offline":true}""")).StatusCode);

        // a model with a query and no definition: the answer is its open questions, and nothing is written until they are answered
        var wrote = await Document(await AsAda(HttpMethod.Post, $"/api/v1/projects/{project}/commands/project_file_write?workspace={workspace}", """{"path":"models/marts/fct_new.sql","create":true,"content":"SELECT CAST(1 AS BIGINT) AS id, 'x' AS label\n"}"""));
        Assert.True((bool)wrote["ok"]!, wrote.ToJsonString());
        var asked = await Document(await AsAda(HttpMethod.Post, $"{update}?workspace={workspace}", """{"paths":["models/marts/fct_new.sql"],"write":true,"offline":true}"""));
        var model = Assert.Single(asked["data"]!["models"]!.AsArray());
        Assert.Equal("incomplete", ((string)model!["status"]!).ToLowerInvariant());
        var questions = model["open_questions"]!.AsArray();
        Assert.NotEmpty(questions);
        Assert.All(questions, q => Assert.StartsWith("Q-define-", (string)q!["id"]!));
        Assert.False(File.Exists(Path.Combine(ServerDir, "workspaces", project, workspace, "models", "marts", "fct_new.yml")));
    }

    [Fact]
    public async Task The_rendered_files_a_deploy_plans_from_are_written_in_the_draft_by_an_author_and_reach_the_project_only_by_a_merge()
    {
        // plan 13a, W10: a deploy refuses a project whose committed rendered/ differs from a fresh render (DDB-424), so a project made in the console needs a compile that writes, in the draft, and a commit and merge that carry it
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Post, "/api/v1/projects", """{"id":"made","template":"starter"}""")).StatusCode);
        var checkout = Path.Combine(ServerDir, "projects", "made");
        Assert.False(Directory.Exists(Path.Combine(checkout, "rendered")));
        var workspace = (string)(await Json(await AsAda(HttpMethod.Post, "/api/v1/projects/made/workspaces", """{"name":"draft"}""")))["id"]!;
        var run = "/api/v1/projects/made/commands/project_compile";

        // the check says the rendered files are not there yet and writes none; a viewer may not compile into the project
        var check = await Document(await AsAda(HttpMethod.Post, $"{run}?workspace={workspace}", """{"check":true}"""));
        Assert.NotEmpty(check["data"]!["out_of_date"]!.AsArray());
        var draft = Path.Combine(ServerDir, "workspaces", "made", workspace);
        Assert.False(Directory.Exists(Path.Combine(draft, "rendered")));
        var refused = await AsVic(HttpMethod.Post, run, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("author", (string)(await Json(refused))["error"]!);
        Assert.False(Directory.Exists(Path.Combine(checkout, "rendered")));

        // the author's compile writes them in the draft, and the draft shows them as its changes; the checkout the scheduler reads has none
        var compiled = await Document(await AsAda(HttpMethod.Post, $"{run}?workspace={workspace}", "{}"));
        Assert.True((bool)compiled["ok"]!, compiled.ToJsonString());
        var wrote = compiled["data"]!["wrote"]!.AsArray().Select(f => "rendered/" + (string)f!).ToArray();   // the compiler names them relative to rendered/
        Assert.NotEmpty(wrote);
        Assert.All(wrote, f => Assert.True(File.Exists(Path.Combine(draft, f))));
        var changes = (await Json(await AsAda(HttpMethod.Get, $"/api/v1/projects/made/workspaces/{workspace}/changes")))["changes"]!.AsArray().Select(c => (string)c!["path"]!).ToArray();
        Assert.All(wrote, f => Assert.Contains(f, changes));
        Assert.False(Directory.Exists(Path.Combine(checkout, "rendered")));

        // a second compile has nothing left to write
        var again = await Document(await AsAda(HttpMethod.Post, $"{run}?workspace={workspace}", "{}"));
        Assert.Empty(again["data"]!["wrote"]!.AsArray());

        // committed and merged (an approver's act), they are the project's, and the project is no longer out of date
        Assert.True((bool)(await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/made/workspaces/{workspace}/commit", """{"message":"Compiled"}""")))["committed"]!);
        var merged = await Send("grace", ServerRole.Approver, HttpMethod.Post, $"/api/v1/projects/made/workspaces/{workspace}/merge");
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.All(wrote, f => Assert.True(File.Exists(Path.Combine(checkout, f))));
        var after = await Document(await AsAda(HttpMethod.Post, run, """{"check":true}"""));
        Assert.True((bool)after["ok"]!, after.ToJsonString());
        Assert.True(after["data"]!["out_of_date"] is null || after["data"]!["out_of_date"]!.AsArray().Count == 0);
    }
}
