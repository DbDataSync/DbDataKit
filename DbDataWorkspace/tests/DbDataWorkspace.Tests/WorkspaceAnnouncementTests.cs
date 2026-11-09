using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The Workspace server offers itself to a console that is waiting (plan 13a, L03; plan 22a GP31): with the console's announce address in the environment it posts an announcement naming itself <c>dbdataworkspace</c>, and the single-use
/// code in it lets the console pair, as an administrator confirming it there would.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceAnnouncementTests
{
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    [Fact]
    public async Task A_server_with_no_console_announces_itself_and_the_code_in_it_pairs()
    {
        var received = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var console = builder.Build();
        console.MapPost(ConsoleAnnouncer.Path, async (HttpContext context) =>
        {
            received.TrySetResult((await JsonSerializer.DeserializeAsync<JsonNode>(context.Request.Body))!);
            return Results.Ok();
        });
        await console.StartAsync();
        var announceUrl = console.Urls.First();

        var directory = Directory.CreateTempSubdirectory("ddw-announce-").FullName;
        WebApplication? server = null;
        try
        {
            var previous = (Environment.GetEnvironmentVariable(ConsoleAnnouncer.UrlVariable), Environment.GetEnvironmentVariable(ConsoleAnnouncer.NameVariable));
            Environment.SetEnvironmentVariable(ConsoleAnnouncer.UrlVariable, announceUrl);
            Environment.SetEnvironmentVariable(ConsoleAnnouncer.NameVariable, "Workspaces on the lab box");
            try { server = WorkspaceServer.Build(new WorkspaceSettings(directory, new Uri($"https://127.0.0.1:{FreePort()}")), WorkspaceServer.Certificate(directory), _ => { }); }
            finally
            {
                Environment.SetEnvironmentVariable(ConsoleAnnouncer.UrlVariable, previous.Item1);
                Environment.SetEnvironmentVariable(ConsoleAnnouncer.NameVariable, previous.Item2);
            }
            await server.StartAsync();

            var announcement = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("dbdataworkspace", (string)announcement["product"]!);
            Assert.Equal("Workspaces on the lab box", (string)announcement["name"]!);
            Assert.Equal(WorkspaceServer.InstanceId(directory), (string)announcement["instanceId"]!);
            Assert.StartsWith("workspace-", (string)announcement["instanceId"]!);

            // the code is the server's own, single use: it pairs a console, which is then told apart from an announcement by the server's clients
            var console1 = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
            var url = new Uri(server.Urls.First());
            var paired = await PairingClient.PairAsync(url, (string)announcement["code"]!, console1, ClientKind.Console, "the confirming console");
            Assert.Equal((string)announcement["instanceId"]!, paired.InstanceId);
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/info");
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console1.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer)));
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);
            await Assert.ThrowsAnyAsync<Exception>(() => PairingClient.PairAsync(url, (string)announcement["code"]!, new ConsoleIdentity("console-2", ConsoleSigningKey.Generate()), ClientKind.Console, "a second use"));
        }
        finally
        {
            if (server is not null) { await server.StopAsync(); await server.DisposeAsync(); }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
