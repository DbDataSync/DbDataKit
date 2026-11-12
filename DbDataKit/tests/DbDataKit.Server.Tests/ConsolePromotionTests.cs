using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Promotion between two runner groups from the console (plan 30 PB06): <c>POST api/promotions</c> runs <c>DefinitionPromoter</c> over the relay as the signed-in person. The groups are fakes of the shared publish API with the roles a real runner asks for
/// (<see cref="FakeRunnerGroup"/>); what the promoter itself does (files by hash, the signature, the refusals) is <c>PromotionTests</c> in <c>DbDataKit.State.Api.Tests</c>, and the real runner's answers are <c>PublishPromotionTests</c> in <c>DbDataRunner.Tests</c>.
/// </summary>
public sealed class ConsolePromotionTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeRunnerGroup test = new();
    private readonly FakeRunnerGroup prod = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer testServer = null!;
    private PairableServer prodServer = null!;
    private string admin = null!;
    private string viewer = null!;

    private static readonly byte[] Dll = [1, 2, 3, 4, 5];
    private static readonly byte[] Notes = "notes"u8.ToArray();

    public async ValueTask InitializeAsync()
    {
        testServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-test", map: test.Map);
        prodServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-prod", map: prod.Map);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = Session(UserRole.Admin);
        viewer = Session(UserRole.Viewer);
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(testServer.Url, testServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Test runner", default);
        await instances.PairAsync(prodServer.Url, prodServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Prod runner", default);
        test.Versions[4] = ("manifest-v4", [("app.dll", Dll), ("notes.txt", Notes)], "sig-v4");
        test.Active["test"] = 4;
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await testServer.DisposeAsync();
        await prodServer.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Session(UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Promote(string session, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotions") { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private static object Body(string from = "runner-test", string to = "runner-prod", object? extra = null) =>
        new Dictionary<string, object?> { ["from"] = from, ["to"] = to, ["name"] = "orders", ["fromChannel"] = "test", ["toChannel"] = "prod", ["bindings"] = extra };

    [Fact]
    public async Task A_version_moves_from_one_runner_to_another_as_the_signed_in_person_and_a_second_promotion_sends_nothing()
    {
        var first = await Promote(admin, Body(extra: new { source = "erp-prod" }));
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(4, (int)first.Body!["sourceVersion"]!);
        Assert.Equal(1, (int)first.Body["targetVersion"]!);
        Assert.True((bool)first.Body["changed"]!);
        Assert.Equal(2, (int)first.Body["sent"]!);
        Assert.Equal("sig-v4", (string)first.Body["signature"]!);
        Assert.Equal(["source"], first.Body["bound"]!.AsArray().Select(b => (string)b!));
        Assert.Equal(Dll, prod.Files[FakeRunnerGroup.Sha(Dll)]);
        Assert.Equal("manifest-v4", prod.Published["prod"].Manifest);
        Assert.Equal("erp-prod", (string)prod.Bindings["prod"]["slots"]!["source"]!);

        // each group was asked by the person who signed in to the console, not by the console
        Assert.All(test.Calls.Concat(prod.Calls), c => Assert.EndsWith("by Admin person", c));
        Assert.Contains(prod.Calls, c => c.StartsWith("PUT definitions/integration/orders "));
        Assert.Contains(test.Calls, c => c.StartsWith("GET artifacts/"));

        var again = await Promote(admin, Body());
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(0, (int)again.Body!["sent"]!);
        Assert.Equal(2, (int)again.Body["skipped"]!);
        Assert.False((bool)again.Body["changed"]!);
        Assert.Equal(2, test.Calls.Count(c => c.StartsWith("GET artifacts/")));   // the files were read from the source once
    }

    [Fact]
    public async Task A_version_may_be_named_and_a_channel_that_serves_nothing_is_refused_in_the_sources_words()
    {
        test.Versions[3] = ("manifest-v3", [], null);
        var named = await Promote(admin, new { from = "runner-test", to = "runner-prod", name = "orders", fromChannel = "test", toChannel = "prod", version = 3, mode = "serve" });
        Assert.Equal(HttpStatusCode.OK, named.Status);
        Assert.Equal(3, (int)named.Body!["sourceVersion"]!);
        Assert.Equal("serve", (string)named.Body["mode"]!);

        var none = await Promote(admin, new { from = "runner-test", to = "runner-prod", name = "orders", fromChannel = "dev", toChannel = "prod" });
        Assert.Equal(HttpStatusCode.Conflict, none.Status);
        Assert.Contains("no active version in channel 'dev' of Test runner", (string)none.Body!["error"]!);
    }

    [Fact]
    public async Task The_target_groups_refusal_is_shown_in_its_words_naming_the_group()
    {
        prod.Refuse = "The slot 'source' of orders is not bound in prod.";
        var refused = await Promote(admin, Body());
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("refused", (string)refused.Body!["code"]!);
        Assert.Contains("Prod runner refused", (string)refused.Body["error"]!);
        Assert.Contains("The slot 'source' of orders is not bound in prod.", (string)refused.Body["error"]!);
    }

    [Fact]
    public async Task A_viewer_cannot_promote_and_a_server_the_console_does_not_know_or_a_kind_it_does_not_promote_is_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Promote(viewer, Body())).Status);
        Assert.Empty(test.Calls);

        var unknown = await Promote(admin, Body(to: "nowhere"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Contains("No server 'nowhere' is registered.", (string)unknown.Body!["error"]!);

        var kind = await Promote(admin, new { from = "runner-test", to = "runner-prod", name = "orders", fromChannel = "test", toChannel = "prod", kind = "dashboard" });
        Assert.Equal(HttpStatusCode.BadRequest, kind.Status);
        var blank = await Promote(admin, new { from = "runner-test", to = "runner-prod", name = "orders", fromChannel = "test", toChannel = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.Status);
        Assert.Empty(test.Calls);
    }

    [Fact]
    public async Task A_target_that_lets_this_console_grant_reading_only_is_refused_before_the_runner_is_asked_naming_the_role()
    {
        // pair the target again so that the console may grant it nothing but reading: the person holds what both allow
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(prodServer.Url, prodServer.NewCodeWith(ServerRole.Viewer), "Prod runner", default);
        var refused = await Promote(admin, Body());
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("This needs the admin (or publisher) role on Prod runner, and you hold viewer there.", (string)refused.Body!["error"]!);
        Assert.DoesNotContain(prod.Calls, c => c.StartsWith("PUT"));
    }
}
