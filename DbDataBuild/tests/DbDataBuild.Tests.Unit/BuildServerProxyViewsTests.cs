using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataKit.Views;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Microsoft.AspNetCore.Http;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Dashboards and reports of a project (plan 32 DBR29, Build's side): the project check reads the <c>dashboards/</c> and <c>reports/</c> folders with the views loader and the project's own semantic models, a workspace gives the
/// editor its models' catalog, and a document of the project reaches a paired Gateway's registry through <c>publish-view</c>, as the approver, with the Publisher role and the person on the call. The Gateway here is the stand-in of
/// <see cref="BuildServerProxyTests"/>: it records what it was sent and answers as a test says.
/// </summary>
public sealed partial class BuildServerProxyTests
{
    private (int Status, JsonObject Body) viewsAnswerIs = (200, new JsonObject { ["version"] = "3", ["created"] = false, ["unchanged"] = false, ["depends_on"] = new JsonArray("semantic-model:retail_sales@2") });

    private IResult ViewsAnswer(string _) => Results.Json(viewsAnswerIs.Body, statusCode: viewsAnswerIs.Status);

    private string ViewText(string folder, string file) => File.ReadAllText(Path.Combine(retail, folder, file)).Replace("\r\n", "\n");

    private async Task<HttpResponseMessage> PublishView(string who, ServerRole role, string proxyName, object body, string projectId = "retail")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/proxies/{proxyName}/publish-view") { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, who, who, role)));
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task A_dashboard_of_the_project_is_published_as_its_text_with_the_publisher_role_and_the_person_and_the_gateways_answer_comes_back()
    {
        await Pair();
        var text = ViewText("dashboards", "retail-sales-today.yaml");
        using var published = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "dashboard", name = "retail-sales-today", note = "from the project" });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var answer = JsonNode.Parse(await published.Content.ReadAsStringAsync())!;
        Assert.Equal(("edge", "dashboard", "retail-sales-today", "3"), ((string?)answer["proxy"], (string?)answer["kind"], (string?)answer["name"], (string?)answer["version"]));
        Assert.Equal("semantic-model:retail_sales@2", (string?)answer["depends_on"]![0]);

        var put = seen.Single(s => s.Path == "/api/v1/dashboards/retail-sales-today");
        Assert.Equal("PUT", put.Method);
        var sent = JsonNode.Parse(put.Body)!;
        Assert.Equal(text, (string?)sent["text"]);
        Assert.Equal("from the project", (string?)sent["note"]);
        Assert.Null(sent["expected_version"]);
        Assert.Equal(("ana", ServerRole.Publisher), (put.Token.SubjectId, put.Token.Roles & ServerRole.Publisher));
    }

    [Fact]
    public async Task A_report_goes_to_the_reports_route_and_an_expected_version_is_passed_on()
    {
        await Pair();
        using var published = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "report", name = "retail-sales-monthly", expected_version = 2 });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var put = seen.Single(s => s.Path == "/api/v1/reports/retail-sales-monthly");
        Assert.Equal(2, (int)JsonNode.Parse(put.Body)!["expected_version"]!);
    }

    [Fact]
    public async Task The_gateways_refusal_is_passed_on_in_its_own_words_and_under_its_status()
    {
        await Pair();
        viewsAnswerIs = (409, new JsonObject { ["error"] = "The document was changed by someone else: the current version is 4.", ["code"] = "version_conflict" });
        using var refused = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "dashboard", name = "retail-sales-today", expected_version = 2 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("the current version is 4", (string?)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]);
    }

    [Fact]
    public async Task Only_an_approver_publishes_and_a_wrong_body_a_missing_file_or_an_unpaired_proxy_is_refused_before_anything_is_sent()
    {
        await Pair();
        using (var author = await PublishView("ana", ServerRole.Author, "edge", new { kind = "dashboard", name = "retail-sales-today" })) Assert.Equal(HttpStatusCode.Forbidden, author.StatusCode);
        using (var viewer = await PublishView("ana", ServerRole.Viewer, "edge", new { kind = "dashboard", name = "retail-sales-today" })) Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        using (var kind = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "widget", name = "retail-sales-today" })) Assert.Equal(HttpStatusCode.BadRequest, kind.StatusCode);
        using (var nameless = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "dashboard" })) Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
        using (var missing = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "dashboard", name = "nope" })) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var other = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "dashboard", name = "retail-sales-today" }, "nothing")) Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using (var unpaired = await PublishView("ana", ServerRole.Approver, "elsewhere", new { kind = "dashboard", name = "retail-sales-today" })) Assert.Equal(HttpStatusCode.NotFound, unpaired.StatusCode);
        Assert.DoesNotContain(seen, s => s.Method == "PUT");
    }

    [Fact]
    public async Task A_document_with_a_problem_is_not_sent_and_the_refusal_names_the_file_line_and_the_engines_code()
    {
        await Pair();
        var path = Path.Combine(retail, "dashboards", "broken.yaml");
        File.WriteAllText(path, ViewText("dashboards", "retail-sales-today.yaml").Replace("sales.net_amount", "sales.nope_amount"));
        using var refused = await PublishView("ana", ServerRole.Approver, "edge", new { kind = "dashboard", name = "broken" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        var first = body["diagnostics"]![0]!;
        Assert.Equal("dashboards/broken.yaml", (string?)first["file"]);
        Assert.True((int)first["line"]! > 0);
        Assert.Contains("nope_amount", (string?)first["message"]);
        Assert.Contains("unknown_member", (string?)first["code"] + (string?)first["message"]);
        Assert.DoesNotContain(seen, s => s.Method == "PUT");
    }

    [Fact]
    public void The_project_check_reads_dashboards_and_reports_against_the_projects_own_models_and_the_retail_samples_pass()
    {
        var clean = new StringWriter();
        CliApp.Run(["project", "compile", "--check", "--project", retail, "--format", "json"], new StringWriter(), clean);
        Assert.DoesNotContain("dashboards/", clean.ToString());
        Assert.DoesNotContain("reports/", clean.ToString());

        File.WriteAllText(Path.Combine(retail, "dashboards", "broken.yaml"), ViewText("dashboards", "retail-sales-today.yaml").Replace("sales.net_amount", "sales.nope_amount"));
        File.WriteAllText(Path.Combine(retail, "reports", "wrong-kind.yaml"), ViewText("dashboards", "retail-sales-today.yaml"));
        var error = new StringWriter();
        Assert.NotEqual(0, CliApp.Run(["project", "compile", "--check", "--project", retail], new StringWriter(), error));
        var text = error.ToString();
        Assert.Contains("dashboards/broken.yaml:", text);
        Assert.Contains("nope_amount", text);
        Assert.Contains("reports/wrong-kind.yaml", text);
        Assert.Contains(ViewsCodes.WrongKind, text);
    }

    [Fact]
    public void The_project_lists_its_document_files_by_kind_and_a_document_is_found_by_name_whatever_its_extension()
    {
        File.WriteAllText(Path.Combine(retail, "dashboards", "other.json"), "{}");
        File.WriteAllText(Path.Combine(retail, "dashboards", "notes.txt"), "not a document");
        var files = ProjectViews.Files(retail);
        Assert.Contains(files, f => f is { Kind: ViewKind.Dashboard, Name: "retail-sales-today", Path: "dashboards/retail-sales-today.yaml" });
        Assert.Contains(files, f => f is { Kind: ViewKind.Report, Name: "retail-sales-monthly", Path: "reports/retail-sales-monthly.yaml" });
        Assert.Equal("dashboards/other.json", ProjectViews.Find(retail, ViewKind.Dashboard, "other")!.Path);
        Assert.DoesNotContain(files, f => f.Name == "notes");
        Assert.Null(ProjectViews.Find(retail, ViewKind.Report, "other"));
    }

    [Fact]
    public async Task A_workspace_gives_the_catalog_of_a_model_to_its_owner_only()
    {
        using (var repo = new Repository(Repository.Init(retail)))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            repo.Commit("first", who, who);
        }
        using var made = await Send("ana", ServerRole.Author, HttpMethod.Post, "/api/v1/projects/retail/workspaces", """{"name":"edit"}""");
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var id = (string)JsonNode.Parse(await made.Content.ReadAsStringAsync())!["id"]!;
        var path = $"/api/v1/projects/retail/workspaces/{id}/models/retail_sales/catalog";

        using var got = await Send("ana", ServerRole.Author, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        var body = JsonNode.Parse(await got.Content.ReadAsStringAsync())!;
        Assert.Equal("retail_sales", (string?)body["model"]);
        Assert.NotNull(body["catalog"]!["measures"]!["sales.net_amount"]);
        Assert.NotNull(body["catalog"]!["dimensions"]);

        using (var someoneElse = await Send("bob", ServerRole.Author, HttpMethod.Get, path)) Assert.Equal(HttpStatusCode.Forbidden, someoneElse.StatusCode);
        using (var noModel = await Send("ana", ServerRole.Author, HttpMethod.Get, $"/api/v1/projects/retail/workspaces/{id}/models/nothing/catalog")) Assert.Equal(HttpStatusCode.NotFound, noModel.StatusCode);
        using (var noWorkspace = await Send("ana", ServerRole.Author, HttpMethod.Get, "/api/v1/projects/retail/workspaces/ana-none/models/retail_sales/catalog")) Assert.Equal(HttpStatusCode.NotFound, noWorkspace.StatusCode);
    }

    private async Task<HttpResponseMessage> Send(string who, ServerRole role, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, who, who, role)));
        return await http.SendAsync(request);
    }
}
