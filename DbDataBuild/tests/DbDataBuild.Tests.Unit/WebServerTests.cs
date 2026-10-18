using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Web;

namespace DbDataBuild.Tests.Unit;

/// <summary>The read-only web interface: a real loopback server over a template project. What it answers, and (more of it) what it refuses.</summary>
public sealed class WebServerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-web-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly WebServer server;
    private readonly HttpClient http = new();

    public WebServerTests()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        Directory.CreateDirectory(Path.Combine(dir, "plans"));
        File.WriteAllText(Path.Combine(dir, "plans", "p.plan.yml"), "secret: statements\n");
        File.WriteAllText(Path.Combine(dir, ".env"), "DBDATABUILD_SQLSERVER_WRITE=secret\n");
        server = new WebServer(dir, new TuiCommand.CliHost(_ => null));
        server.StartAsync();
    }

    public void Dispose() { server.Dispose(); http.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string Url(string path) => $"http://127.0.0.1:{server.Port}{path}";

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null, bool token = true, string? host = null, string? origin = null)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (token) request.Headers.Add("X-DDB-Token", server.Token);
        if (host != null) request.Headers.Host = host;
        if (origin != null) request.Headers.Add("Origin", origin);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        return await http.SendAsync(request);
    }

    private Task<HttpResponseMessage> Run(string command, string arguments = "{}", bool token = true, string? host = null, string? origin = null) =>
        Send(HttpMethod.Post, "/api/run", $"{{\"command\":\"{command}\",\"arguments\":{arguments}}}", token, host, origin);

    [Fact]
    public async Task The_page_needs_the_token_in_the_address_and_carries_it_for_its_calls()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(Url("/"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(Url("/?token=wrong"))).StatusCode);
        var ok = await http.GetAsync(Url("/?token=" + server.Token));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var page = await ok.Content.ReadAsStringAsync();
        Assert.Contains($"content=\"{server.Token}\"", page);
        Assert.DoesNotContain("__TOKEN__", page);
        Assert.Contains("default-src 'none'", ok.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(ok.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_read_only_command_gives_its_document_and_everything_else_is_refused()
    {
        var ok = await Run("project_compile");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var reply = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!;
        Assert.Equal("project compile", (string)reply["document"]!["command"]!);

        foreach (var command in new[] { "connection_refresh", "connection_init", "connection_seed", "project_seed", "project_create", "project_model_update", "ui_terminal", "ui_mcp", "ui_web" })
            Assert.Equal(HttpStatusCode.NotFound, (await Run(command)).StatusCode);
        // render can be run, but not with --write; a path outside the project and the project option are not inputs
        Assert.Equal(HttpStatusCode.BadRequest, (await Run("project_compile", "{\"check\":true}")).StatusCode);       // held to --check already: it is not an input
        Assert.Equal(HttpStatusCode.BadRequest, (await Run("project_compile", "{\"write\":true}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Run("project_compile", "{\"project\":\"/\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Run("project_compile", "{\"models\":[\"../x\"]}")).StatusCode);
    }

    [Fact]
    public async Task The_lowered_and_rendered_text_comes_from_render_content_and_nothing_is_written()
    {
        var reply = JsonNode.Parse(await (await Run("project_compile", "{\"content\":true}")).Content.ReadAsStringAsync())!;
        var files = reply["document"]!["data"]!["files"]!.AsArray();
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.False(string.IsNullOrWhiteSpace((string?)f!["content"])));
        Assert.Contains(files, f => ((string)f!["path"]!).StartsWith("rendered/lowered/"));
        Assert.False(Directory.Exists(Path.Combine(dir, "rendered")));       // the page can see the scripts without `render --write`
        var plain = JsonNode.Parse(await (await Run("project_compile")).Content.ReadAsStringAsync())!;
        Assert.Null(plain["document"]!["data"]!["files"]![0]!["content"]);
    }

    [Fact]
    public async Task Sample_and_diff_can_be_run_and_a_person_may_ask_for_values_which_a_model_never_may()
    {
        var sample = JsonNode.Parse(await (await Run("project_sample", "{\"models\":[\"marts.customers\"],\"limit\":3}")).Content.ReadAsStringAsync())!;
        Assert.Equal(0, (int)sample["exit"]!);
        Assert.Contains(sample["document"]!["data"]!["tables"]!.AsArray(), t => (string)t!["name"]! == "marts.customers");
        // diff reads a target: here there is no login, and the answer is the command's own finding, not a refusal of the page
        var diff = await Run("connection_compare", "{\"table\":\"marts.customers\",\"against_schema\":\"dev\",\"show_values\":true}");
        Assert.Equal(HttpStatusCode.OK, diff.StatusCode);
        Assert.Equal("connection compare", (string)JsonNode.Parse(await diff.Content.ReadAsStringAsync())!["document"]!["command"]!);
    }

    [Fact]
    public async Task A_request_without_the_token_for_another_host_or_from_another_origin_is_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Run("project_compile", token: false)).StatusCode);
        // DNS rebinding: the name differs, the address is ours. The listener itself answers 404 for a Host that matches none of its prefixes; the check in the router (421) is the second line
        Assert.Contains((await Run("project_compile", host: "evil.example")).StatusCode, new[] { HttpStatusCode.NotFound, (HttpStatusCode)421 });
        Assert.Equal(HttpStatusCode.Forbidden, (await Run("project_compile", origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Run("project_compile", origin: $"http://127.0.0.1:{server.Port}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/file?path=dbdatabuild.yml", token: false)).StatusCode);
        var wrongType = new HttpRequestMessage(HttpMethod.Post, Url("/api/run")) { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        wrongType.Headers.Add("X-DDB-Token", server.Token);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await http.SendAsync(wrongType)).StatusCode);
    }

    [Fact]
    public async Task Files_are_read_from_what_the_project_is_made_of_and_from_nothing_else()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/file?path=dbdatabuild.yml")).StatusCode);
        var model = Directory.EnumerateFiles(Path.Combine(dir, "models"), "*.sql", SearchOption.AllDirectories).First();
        var relative = Path.GetRelativePath(dir, model).Replace('\\', '/');
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(relative))).StatusCode);
        foreach (var path in new[] { "plans/p.plan.yml", ".env", "../outside.sql", "models/../.env", "/etc/passwd", "models", "models/", "models/x.exe", "DbDataBuild.csproj", "" })
            Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/file?path=" + Uri.EscapeDataString(path))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/file?path=models/none.sql")).StatusCode);
    }

    [Fact]
    public void A_link_inside_the_project_cannot_lead_out_of_it()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ddb-web-out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.sql"), "select 'secret'");
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(dir, "models", "leak"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }       // a platform that does not allow links here has nothing to test
            Assert.Equal(403, server_ReadFile("models/leak/secret.sql"));
        }
        finally { Directory.Delete(outside, true); }
    }

    private int server_ReadFile(string path)
    {
        using var s = new WebServer(dir, new TuiCommand.CliHost(_ => null));
        return s.ReadFile(path).Item1;
    }

    [Fact]
    public async Task Only_one_command_runs_at_a_time_and_each_gets_its_own_answer()
    {
        var answers = await Task.WhenAll(Run("project_compile"), Run("project_show_loads"), Run("project_tests_run"), Run("help_matrix"));
        var names = new List<string>();
        foreach (var a in answers) { Assert.Equal(HttpStatusCode.OK, a.StatusCode); names.Add((string)JsonNode.Parse(await a.Content.ReadAsStringAsync())!["document"]!["command"]!); }
        Assert.Equal(["project compile", "project show loads", "project tests run", "help matrix"], names);
    }

    [Fact]
    public void The_command_refuses_a_json_format_a_missing_project_and_a_bad_port()
    {
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "web", "--project", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "web", "--project", Path.Combine(dir, "nope")], new StringWriter(), new StringWriter()));
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "web", "--project", dir, "--port", "70000"], new StringWriter(), new StringWriter()));
    }
}
