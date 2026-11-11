using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>The setup checklist's skipped items (plan 23a FQ21): kept per install in the <c>setup</c> module, changed by administrators only, and nothing but a skip is stored (what is finished is read from what the install has).</summary>
public sealed class ConsoleSetupItemsTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-setup-" + Guid.NewGuid().ToString("N")[..8]);
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

    private string SessionFor(UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana-" + Guid.NewGuid().ToString("N")[..6], null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.StartsWith('{') ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task Every_item_is_listed_in_order_and_none_is_skipped_on_a_new_install()
    {
        var (status, body) = await Send(HttpMethod.Get, "/api/setup/items", SessionFor(UserRole.Viewer));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(SetupItems.Known, body!["items"]!.AsArray().Select(i => (string)i!["item"]!));
        Assert.All(body["items"]!.AsArray(), i => Assert.False((bool)i!["skipped"]!));
    }

    [Fact]
    public async Task An_administrator_skips_an_item_and_puts_it_back_and_a_viewer_cannot_and_an_unknown_item_is_refused()
    {
        var admin = SessionFor(UserRole.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Put, "/api/setup/items/connections", admin, new { skipped = true })).Status);
        var skipped = (await Send(HttpMethod.Get, "/api/setup/items", admin)).Body!["items"]!.AsArray().Single(i => (string)i!["item"]! == "connections")!;
        Assert.True((bool)skipped["skipped"]!);
        Assert.NotNull(skipped["skipped_at"]);

        // skipping again keeps it skipped; putting it back clears it
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Put, "/api/setup/items/connections", admin, new { skipped = true })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Put, "/api/setup/items/connections", admin, new { skipped = false })).Status);
        Assert.All((await Send(HttpMethod.Get, "/api/setup/items", admin)).Body!["items"]!.AsArray(), i => Assert.False((bool)i!["skipped"]!));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, "/api/setup/items/connections", SessionFor(UserRole.Viewer), new { skipped = true })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, "/api/setup/items/not-an-item", admin, new { skipped = true })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/setup/items", null)).Status);
    }

    [Fact]
    public void The_skips_are_kept_in_the_setup_module_of_the_state_database_and_only_skips_are_kept()
    {
        var items = app.Services.GetRequiredService<SetupItems>();
        items.Skip("git-remotes", "someone");
        items.Skip("git-remotes", "someone else");                // again: the first stays

        Assert.Equal(["git-remotes"], items.Skipped().Keys);
        Assert.Throws<ArgumentException>(() => items.Skip("nothing", null));
        Assert.True(items.Unskip("git-remotes"));
        Assert.False(items.Unskip("git-remotes"));
        Assert.Contains(ConsoleState.Modules, m => m.Name == SetupModule.Name);
    }
}
