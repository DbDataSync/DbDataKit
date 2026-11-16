using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The Build, Sync and Store servers were deleted (plan 33 SC23) and the break is not silent: an announcement from one is refused with the sentence naming where its features went, and a server of one still paired is answered with that
/// sentence by the relay (410), which sends it nothing.
/// </summary>
public sealed class RemovedProductTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-removed-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<string> reached = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer sync = null!;

    public async ValueTask InitializeAsync()
    {
        sync = await PairableServer.StartAsync(product: RemovedProducts.Sync, instanceId: "sync-1", map: (server, _, _) =>
            server.MapGet("/api/replications", () => { reached.Enqueue("/api/replications"); return Results.Json(Array.Empty<string>()); }));
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        // a server paired before the upgrade: the registry keeps it until a person forgets it
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(sync.Url, sync.NewCodeWith(ServerRole.Admin), "Old sync", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await sync.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private static Announcement Announced(string product) => new(product, "abc-123", "old", 8470, null, "https://old.example:8470", "ABCDE-FGHIJ", DateTimeOffset.UtcNow.AddMinutes(5));

    [Theory]
    [InlineData(RemovedProducts.Build, "The Build server was deleted")]
    [InlineData(RemovedProducts.Sync, "The Sync server was deleted")]
    [InlineData(RemovedProducts.Store, "The Store server was deleted")]
    public void An_announcement_from_a_deleted_server_is_refused_with_where_its_features_went(string product, string sentence)
    {
        var path = Path.Combine(directory, "pending-" + product + ".json");
        var why = new PendingInstances(path).Announce(Announced(product), null);
        Assert.StartsWith(sentence, why);
        Assert.Contains("Workspace", why);
        Assert.DoesNotContain(product, ServerProducts.Announced);
    }

    [Fact]
    public void The_four_services_but_the_console_are_the_products_announced() =>
        Assert.Equal([ServerProducts.Gateway, ServerProducts.Runner, ServerProducts.Workspace], ServerProducts.Announced);

    [Fact]
    public async Task A_paired_server_of_a_deleted_product_is_answered_with_the_sentence_and_is_sent_nothing()
    {
        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada", null, UserRole.Admin).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Get, "/relay/sync-1/api/replications");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(RemovedProducts.Message(RemovedProducts.Sync), (string)body["error"]!);
        Assert.Empty(reached);
    }

    [Fact]
    public void Only_the_deleted_products_have_a_sentence()
    {
        foreach (var product in new[] { ServerProducts.Gateway, ServerProducts.Runner, ServerProducts.Workspace, "dbdatakit", null })
            Assert.Null(RemovedProducts.Message(product));
    }
}
