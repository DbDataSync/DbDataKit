using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a KN19 on a real console: the Knowledge page's suggestions. A suggestion is based on the console's index (until it is built there is nothing to base one on), is written by anyone signed in, is seen by the person who made it (and
/// by an administrator when the item belongs to the administrators), and is withdrawn by its person; a text that does not read is refused with the reader's diagnostics.
/// </summary>
public sealed class ConsoleKnowledgeSuggestionsTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-suggest-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string vic = "";
    private string wes = "";

    private const string Revenue = "---\nknowledge: 1\ntitle: Revenue\nrole: reference\nowner: finance\nabout:\n  - install: true\n---\n# Revenue\n\nRevenue is net revenue.\n";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(directory, "knowledge"));
        File.WriteAllText(Path.Combine(directory, "knowledge", "revenue.md"), Revenue);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: _ => { });
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
        vic = SignIn("Vic", UserRole.Viewer);
        wes = SignIn("Wes", UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private static int Count(JsonNode? body) => ((JsonArray)body!["suggestions"]!).Count;

    private static string Changed => Revenue.Replace("net revenue.", "net revenue after returns.", StringComparison.Ordinal);

    [Fact]
    public async Task A_suggestion_waits_for_the_index_then_is_written_by_anyone_seen_by_its_person_and_withdrawn_by_them()
    {
        // before the index is built there is nothing to base a suggestion on, and nothing is created by asking
        var (before, list) = await Call(HttpMethod.Get, "/api/knowledge/suggestions", vic);
        Assert.Equal(HttpStatusCode.OK, before);
        Assert.False((bool)list!["built"]!);
        var (refused, why) = await Call(HttpMethod.Post, "/api/knowledge/suggestions", vic, new SuggestBody("project:revenue.md", false, null, Changed, "returns too"));
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("index is not built", (string)why!["error"]!, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/knowledge/index/build", admin)).Status);

        // a viewer suggests: the record carries who, why, the item and what it was based on
        var (made, row) = await Call(HttpMethod.Post, "/api/knowledge/suggestions", vic, new SuggestBody("project:revenue.md", false, null, Changed, "returns too"));
        Assert.True(made == HttpStatusCode.OK, row?.ToJsonString());
        var id = (string)row!["id"]!;
        Assert.Equal(("open", "change", "Vic".Length > 0, "person", "finance"), ((string)row["state"]!, (string)row["kind"]!, true, (string)row["via"]!, (string)row["owner"]!));
        Assert.Equal(Changed, (string)row["proposed"]!);
        Assert.NotNull(row["baseVersion"]);

        // they see their own; another viewer sees none and cannot read it; the owner is the finance group, which no console user is in
        Assert.Equal(1, Count((await Call(HttpMethod.Get, "/api/knowledge/suggestions", vic)).Body));
        Assert.Equal(0, Count((await Call(HttpMethod.Get, "/api/knowledge/suggestions", wes)).Body));
        Assert.Equal(0, Count((await Call(HttpMethod.Get, "/api/knowledge/suggestions", admin)).Body));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/knowledge/suggestions/{id}", vic)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/knowledge/suggestions/{id}", wes)).Status);

        // the item moves on (its file is edited and the index brought in step): the suggestion says so
        File.WriteAllText(Path.Combine(directory, "knowledge", "revenue.md"), Revenue.Replace("Revenue is net revenue.", "Revenue is net revenue, VAT excluded.", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/knowledge/index/build", admin)).Status);
        Assert.True((bool)(await Call(HttpMethod.Get, $"/api/knowledge/suggestions/{id}", vic)).Body!["moved"]!);

        // only the person withdraws, and once
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/knowledge/suggestions/{id}/withdraw", wes)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/knowledge/suggestions/{id}/withdraw", vic)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/knowledge/suggestions/{id}/withdraw", vic)).Status);
        Assert.Equal("withdrawn", (string)(await Call(HttpMethod.Get, $"/api/knowledge/suggestions/{id}", vic)).Body!["state"]!);
        Assert.Equal(0, Count((await Call(HttpMethod.Get, "/api/knowledge/suggestions?state=open", vic)).Body));
    }

    [Fact]
    public async Task A_text_that_does_not_read_and_an_item_nobody_has_are_refused_in_words()
    {
        await Call(HttpMethod.Post, "/api/knowledge/index/build", admin);

        var (broken, body) = await Call(HttpMethod.Post, "/api/knowledge/suggestions", vic, new SuggestBody("project:revenue.md", false, null, Changed.Replace("role: reference", "role: policy", StringComparison.Ordinal), "x"));
        var (missing, why) = await Call(HttpMethod.Post, "/api/knowledge/suggestions", vic, new SuggestBody("project:nothing.md", false, null, Changed, "x"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, broken);
        Assert.Contains("does not read", (string)body!["error"]!, StringComparison.Ordinal);
        Assert.Equal(4, (int)body["diagnostics"]![0]!["line"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing);
        Assert.Contains("no item 'project:nothing.md'", (string)why!["error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_item_is_suggested_for_a_project_by_a_viewer()
    {
        await Call(HttpMethod.Post, "/api/knowledge/index/build", admin);

        var (made, row) = await Call(HttpMethod.Post, "/api/knowledge/suggestions", wes, new SuggestBody("guides/refunds.md", true, "project", "---\nknowledge: 1\nrole: reference\ntitle: Refunds\nowner: finance\n---\nRefunds go back to the card.\n", "Nobody wrote it down"));

        Assert.True(made == HttpStatusCode.OK, row?.ToJsonString());
        Assert.Equal(("new", "guides/refunds.md", "project"), ((string)row!["kind"]!, (string)row["item"]!, (string)row["project"]!));
    }
}
