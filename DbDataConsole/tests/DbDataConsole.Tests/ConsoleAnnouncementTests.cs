using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>A server announces itself on the console's private port, waits as pending, and is paired only when an administrator confirms it (plan 13a, L03).</summary>
public sealed class ConsoleAnnouncementTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer server = null!;
    private Uri announce = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        announce = new Uri($"http://127.0.0.1:{port}");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0"), null, announce), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("https"))) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private HttpRequestMessage AsAdmin(HttpMethod method, string path)
    {
        var session = app.Services.GetRequiredService<SessionStore>().Create(app.Services.GetRequiredService<UserStore>().CreateUser("An admin", null, UserRole.Admin).Id).Id;
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return request;
    }

    private ConsoleAnnouncer Announcer() => new("dbdataproxy", PairableServer.InstanceId, "Test proxy", server.Url, announce, server.Clients, server.Codes);

    [Fact]
    public async Task An_announced_server_waits_as_pending_and_is_paired_only_when_an_administrator_confirms_it()
    {
        using var client = new HttpClient();
        Assert.True(await Announcer().AnnounceOnceAsync(client));

        var listed = JsonNode.Parse(await (await http.SendAsync(AsAdmin(HttpMethod.Get, "/api/instances/pending"))).Content.ReadAsStringAsync())!.AsArray();
        var row = Assert.Single(listed)!;
        Assert.Equal("Test proxy", (string)row["name"]!);
        Assert.Equal("dbdataproxy", (string)row["product"]!);
        Assert.DoesNotContain("code", row.AsObject().Select(p => p.Key));
        Assert.Empty(server.Clients.All());                                     // announcing paired nothing
        Assert.Empty(app.Services.GetRequiredService<InstanceRegistry>().All());

        var confirmed = await http.SendAsync(AsAdmin(HttpMethod.Post, $"/api/instances/pending/{PairableServer.InstanceId}/confirm"));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("dbdatakit console", Assert.Single(server.Clients.All()).Name);
        Assert.Equal(server.Fingerprint, app.Services.GetRequiredService<InstanceRegistry>().All().Single().CertificateFingerprint);
        Assert.Empty(app.Services.GetRequiredService<PendingInstances>().All());

        // once a console is paired the server stops announcing
        Assert.False(await Announcer().AnnounceOnceAsync(client));
    }

    [Fact]
    public async Task The_private_port_serves_announcements_only_and_the_public_one_never_takes_them()
    {
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(announce, "/api/instances"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(announce, "/announce"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/announce", new { })).StatusCode);

        var bad = await client.PostAsJsonAsync(new Uri(announce, "/announce"), new Announcement("dbdataproxy", "x", "Plain", "http://insecure", "CODE", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var unknown = await client.PostAsJsonAsync(new Uri(announce, "/announce"), new Announcement("something-else", "x", "Other", "https://x:1", "CODE", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Empty(app.Services.GetRequiredService<PendingInstances>().All());
    }

    [Fact]
    public async Task Only_an_administrator_sees_or_confirms_a_waiting_server_and_a_rejected_one_is_forgotten()
    {
        using var client = new HttpClient();
        await Announcer().AnnounceOnceAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/instances/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync($"/api/instances/pending/{PairableServer.InstanceId}/confirm", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(AsAdmin(HttpMethod.Delete, $"/api/instances/pending/{PairableServer.InstanceId}"))).StatusCode);
        Assert.Empty(server.Clients.All());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(AsAdmin(HttpMethod.Post, $"/api/instances/pending/{PairableServer.InstanceId}/confirm"))).StatusCode);
    }
}
