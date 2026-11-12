using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The backlog the console hosts (plan 27a BK01, BK03, BK04): the API over <c>DbDataKit.Backlog</c> as the signed-in person. An administrator works as a lead and a viewer reads: a viewer cannot move an item, a rule the library refuses is an answer with its
/// reason (422), a stale edit is 409, and the history names the person who changed the item.
/// </summary>
public sealed class ConsoleBacklogTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-backlog-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SessionFor(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.StartsWith('{') || text.StartsWith('[') ? JsonNode.Parse(text) : null);
    }

    private async Task<string> Item(string admin, string title, string type = "story", string project = "DATA") =>
        (string)(await Send(HttpMethod.Post, $"/api/backlog/projects/{project}/items", admin, new { type, title })).Body!["id"]!;

    [Fact]
    public async Task The_workflow_says_what_the_signed_in_person_may_do_and_nothing_is_open_to_a_stranger()
    {
        var lead = (await Send(HttpMethod.Get, "/api/backlog/workflow", SessionFor("Ana", UserRole.Admin))).Body!;
        var viewer = (await Send(HttpMethod.Get, "/api/backlog/workflow", SessionFor("Vic", UserRole.Viewer))).Body!;

        Assert.Equal(("lead", "viewer", "backlog"), ((string)lead["role"]!, (string)viewer["role"]!, (string)lead["initial"]!));
        Assert.Equal(["backlog", "ready", "in-progress", "review", "done", "cancelled"], lead["states"]!.AsArray().Select(s => (string)s!["id"]!));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/backlog/workflow", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/backlog/projects", null)).Status);
    }

    [Fact]
    public async Task A_viewer_reads_and_cannot_move_make_or_comment_and_the_refusal_says_why()
    {
        var admin = SessionFor("Ana", UserRole.Admin);
        var viewer = SessionFor("Vic", UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/backlog/projects", admin, new { key = "DATA", name = "Data platform" })).Status);
        var item = await Item(admin, "Load orders");

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/backlog/projects", viewer)).Status);
        Assert.Single((await Send(HttpMethod.Get, "/api/backlog/projects/DATA/items", viewer)).Body!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/backlog/items/{item}", viewer)).Status);

        var moved = await Send(HttpMethod.Post, $"/api/backlog/items/{item}/transition", viewer, new { to = "ready" });
        Assert.Equal(HttpStatusCode.Forbidden, moved.Status);
        Assert.Contains("needs the role of a member", (string)moved.Body!["error"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/backlog/projects/DATA/items", viewer, new { type = "task", title = "Nope" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/backlog/items/{item}/comments", viewer, new { body = "Hi" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/backlog/projects", viewer, new { key = "OPS", name = "Operations" })).Status);
        Assert.Equal("backlog", (string)(await Send(HttpMethod.Get, $"/api/backlog/items/{item}", viewer)).Body!["item"]!["state"]!);
    }

    [Fact]
    public async Task A_rule_of_the_workflow_is_422_with_its_reason_a_stale_edit_is_409_and_a_missing_thing_is_404()
    {
        var admin = SessionFor("Ana", UserRole.Admin);
        await Send(HttpMethod.Post, "/api/backlog/projects", admin, new { key = "DATA", name = "Data platform" });
        var item = await Item(admin, "Load orders");

        var skip = await Send(HttpMethod.Post, $"/api/backlog/items/{item}/transition", admin, new { to = "review" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, skip.Status);
        Assert.Contains("no move from 'backlog' to 'review'", (string)skip.Body!["error"]!);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/backlog/items/{item}/transition", admin, new { to = "ready" })).Status);
        var needs = await Send(HttpMethod.Post, $"/api/backlog/items/{item}/transition", admin, new { to = "in-progress" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, needs.Status);
        Assert.Contains("an assignee", (string)needs.Body!["error"]!);

        var read = (await Send(HttpMethod.Get, $"/api/backlog/items/{item}", admin)).Body!["item"]!;
        var version = (long)read["version"]!;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Patch, $"/api/backlog/items/{item}", admin, new { title = "Load the orders", version })).Status);
        var stale = await Send(HttpMethod.Patch, $"/api/backlog/items/{item}", admin, new { title = "Too late", version });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Contains("DATA-1", (string)stale.Body!["error"]!);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/backlog/items/{Guid.NewGuid()}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/backlog/projects/NOPE/items", admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, "/api/backlog/projects", admin, new { key = "DATA", name = "Again" })).Status);
    }

    [Fact]
    public async Task The_history_names_the_person_and_a_sprint_is_planned_started_and_closed_with_the_open_items_carried_on()
    {
        var admin = SessionFor("Ana Lovelace", UserRole.Admin);
        await Send(HttpMethod.Post, "/api/backlog/projects", admin, new { key = "DATA", name = "Data platform" });
        var done = await Item(admin, "Finished");
        var open = await Item(admin, "Still open");
        var sprint = (string)(await Send(HttpMethod.Post, "/api/backlog/projects/DATA/sprints", admin, new { name = "Sprint 1", goal = "Orders" })).Body!["id"]!;
        var next = (string)(await Send(HttpMethod.Post, "/api/backlog/projects/DATA/sprints", admin, new { name = "Sprint 2" })).Body!["id"]!;
        foreach (var id in new[] { done, open }) Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/api/backlog/items/{id}/sprint", admin, new { sprint })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/backlog/sprints/{sprint}/start", admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/backlog/sprints/{next}/start", admin)).Status);

        await Send(HttpMethod.Patch, $"/api/backlog/items/{done}", admin, new { assignee = "ana" });
        foreach (var to in new[] { "ready", "in-progress" }) await Send(HttpMethod.Post, $"/api/backlog/items/{done}/transition", admin, new { to });
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/backlog/items/{done}/transition", admin, new { to = "done", resolution = "Shipped" })).Status);

        var closed = (await Send(HttpMethod.Post, $"/api/backlog/sprints/{sprint}/close", admin, new { carryTo = next })).Body!;
        Assert.Equal(("closed", 1, 1), ((string)closed["sprint"]!["state"]!, (int)closed["summary"]!["done"]!, (int)closed["summary"]!["moved"]!));
        Assert.Equal(next, (string)(await Send(HttpMethod.Get, $"/api/backlog/items/{open}", admin)).Body!["item"]!["sprint"]!);

        var history = (await Send(HttpMethod.Get, $"/api/backlog/items/{done}", admin)).Body!["history"]!.AsArray();
        Assert.Equal(["created", "sprint", "field", "transition", "transition", "transition", "field"], history.Select(h => (string)h!["kind"]!));
        Assert.All(history, h => Assert.Equal("Ana Lovelace", (string)h!["actorName"]!));
    }

    [Fact]
    public async Task An_attachment_is_uploaded_downloaded_and_removed_and_an_import_reports_the_line_of_a_bad_file()
    {
        var admin = SessionFor("Ana", UserRole.Admin);
        await Send(HttpMethod.Post, "/api/backlog/projects", admin, new { key = "DATA", name = "Data platform" });
        var item = await Item(admin, "Spec");
        var bytes = new byte[3000];
        new Random(5).NextBytes(bytes);

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        form.Add(part, "file", "spec.pdf");
        var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/backlog/items/{item}/attachments") { Content = form };
        upload.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={admin}");
        var uploaded = await http.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var attachment = (string)JsonNode.Parse(await uploaded.Content.ReadAsStringAsync())!["id"]!;

        var download = new HttpRequestMessage(HttpMethod.Get, $"/api/backlog/attachments/{attachment}");
        download.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={admin}");
        var got = await http.SendAsync(download);
        Assert.Equal(("application/pdf", "spec.pdf"), (got.Content.Headers.ContentType!.MediaType, got.Content.Headers.ContentDisposition!.FileName));
        Assert.Equal(bytes, await got.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/backlog/attachments/{attachment}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/backlog/attachments/{attachment}", admin)).Status);

        var bad = await Send(HttpMethod.Post, "/api/backlog/projects/DATA/import", admin, new { text = "schema: work-item/1\nproject: DATA\nitems:\n  - { id: a, type: storey, title: T }\n" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        var problem = bad.Body!["diagnostics"]!.AsArray().Single()!;
        Assert.Equal((4, "DBD-BKL-011"), ((int)problem["line"]!, (string)problem["code"]!));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, "/api/backlog/projects/DATA/import", admin, new { text = "schema: work-item/1\nproject: OTHER\nitems: []\n" })).Status);

        var good = await Send(HttpMethod.Post, "/api/backlog/projects/DATA/import", admin, new { text = "schema: work-item/1\nproject: DATA\nitems:\n  - { id: a, type: story, title: From a file }\n" });
        Assert.Equal(1, (int)good.Body!["made"]!);
        var export = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/backlog/projects/DATA/export") { Headers = { { "Cookie", $"{ConsoleHost.Auth.SessionCookie}={admin}" } } });
        Assert.Contains("title: \"From a file\"", await export.Content.ReadAsStringAsync());
    }

    [Fact]
    public void The_console_state_holds_the_backlog_module_and_its_payloads()
    {
        Assert.Contains(ConsoleState.Modules, m => m.Name == BacklogModule.Name);
        var names = app.Services.GetRequiredService<StateDatabase>().RegisteredModules().Select(m => m.Module).ToList();
        Assert.Contains("backlog", names);
        Assert.Contains("payloads", names);
    }
}
