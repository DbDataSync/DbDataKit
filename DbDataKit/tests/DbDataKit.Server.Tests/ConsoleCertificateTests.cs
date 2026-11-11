using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>UI10: the console describes the certificate it presents at <c>GET /api/v1/certificate</c>, as every server does, for a person who is signed in.</summary>
public sealed class ConsoleCertificateTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-cert-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task A_signed_in_person_reads_the_certificate_and_nobody_else_does()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/certificate")).StatusCode);

        var session = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Vic", null, UserRole.Viewer).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/certificate");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var certificate = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True((bool)certificate["present"]!);
        Assert.Equal(ConsoleHost.Certificate(directory).Thumbprint, (string)certificate["thumbprint"]!);
        Assert.True((int)certificate["days_left"]! > 30);
    }
}
