using System.Net;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace DbDataKit.Web.Tests;

public sealed class ManagementApiVersionTests
{
    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 4, false)]
    [InlineData(3, 5, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 2, false)]
    public void AConsoleSupportsItsOwnVersionAndTheOneBefore(int own, int server, bool supported)
    {
        Assert.Equal(supported, ManagementApi.Supports(server, own));
        Assert.Equal(supported, ManagementApi.Incompatibility(server, own) is null);
    }

    [Fact]
    public void TheMessageSaysWhichSideToUpgrade()
    {
        var ahead = ManagementApi.Incompatibility(3, own: 1)!;
        Assert.Contains("version 3", ahead);
        Assert.Contains("supports 1", ahead);
        Assert.EndsWith("Upgrade this console.", ahead);

        var behind = ManagementApi.Incompatibility(1, own: 3)!;
        Assert.Contains("supports 2 to 3", behind);
        Assert.EndsWith("Upgrade this server.", behind);
    }

    // ---- every call ----

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Reply(string? version)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (version is not null) response.Headers.TryAddWithoutValidation(ManagementApi.VersionHeader, version);
        return response;
    }

    [Fact]
    public async Task EveryCallStatesTheClientsVersionAndReportsTheServers()
    {
        var stub = new Stub(_ => Reply("2"));
        var seen = new List<int>();
        using var http = new HttpClient(new ManagementVersionHandler(stub, own: 3, observed: seen.Add));
        var response = await http.GetAsync("https://server.invalid/anything", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("3", stub.Seen!.Headers.GetValues(ManagementApi.VersionHeader).Single());
        Assert.Equal([2], seen);
    }

    [Fact]
    public async Task AServerTwoVersionsAheadIsRefusedWithTheUpgradeMessage()
    {
        using var http = new HttpClient(new ManagementVersionHandler(new Stub(_ => Reply("3")), own: 1));
        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() => http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken));
        Assert.Equal(3, error.ServerVersion);
        Assert.EndsWith("Upgrade this console.", error.Message);
    }

    [Fact]
    public async Task AServerTooOldIsRefusedAndAskedToUpgrade()
    {
        using var http = new HttpClient(new ManagementVersionHandler(new Stub(_ => Reply("1")), own: 3));
        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() => http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken));
        Assert.EndsWith("Upgrade this server.", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task AnAnswerWithoutAUsableVersionIsNotAServerThisConsoleCanManage(string? header)
    {
        using var http = new HttpClient(new ManagementVersionHandler(new Stub(_ => Reply(header)), own: 1));
        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() => http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken));
        Assert.Contains("did not report a management API version", error.Message);
        Assert.Null(error.ServerVersion);
    }

    [Fact]
    public async Task AnErrorResponseStillCarriesTheVersionAndIsPassedOn()
    {
        using var http = new HttpClient(new ManagementVersionHandler(new Stub(_ => { var r = Reply("1"); r.StatusCode = HttpStatusCode.Forbidden; return r; }), own: 1));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task AnAnswerWithoutAVersionAnd408IsASlowServersOwnTimeoutAndTheRequestIsSentOnceMore()
    {
        var calls = 0;
        var stub = new Stub(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.RequestTimeout) : Reply("1"));
        using var http = new HttpClient(new ManagementVersionHandler(stub, own: 1) { RetryAfterRequestTimeout = TimeSpan.Zero });
        using var body = new StringContent("{}");
        var response = await http.PutAsync("https://server.invalid/x", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ASecond408WithoutAVersionIsStillNotAServerThisConsoleCanManage()
    {
        var calls = 0;
        var stub = new Stub(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.RequestTimeout); });
        using var http = new HttpClient(new ManagementVersionHandler(stub, own: 1) { RetryAfterRequestTimeout = TimeSpan.Zero });
        await Assert.ThrowsAsync<IncompatibleServerException>(() => http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);   // one more try, not a loop
    }

    [Fact]
    public async Task A408WithAVersionIsPassedOnWithoutASecondTry()
    {
        var calls = 0;
        var stub = new Stub(_ => { calls++; var r = Reply("1"); r.StatusCode = HttpStatusCode.RequestTimeout; return r; });
        using var http = new HttpClient(new ManagementVersionHandler(stub, own: 1) { RetryAfterRequestTimeout = TimeSpan.Zero });
        Assert.Equal(HttpStatusCode.RequestTimeout, (await http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(1, calls);
    }

    // ---- a real server ----

    [Fact]
    public async Task ARealServerReportsItsVersionOnEveryResponseEvenANotFound()
    {
        await using var server = await TestServer.StartAsync();
        using var http = new HttpClient(PairingClient.Pinned(server.Fingerprint));
        var known = await http.GetAsync(new Uri(server.Url, ManagementApi.PairingPath), TestContext.Current.CancellationToken);
        Assert.Equal("1", known.Headers.GetValues(ManagementApi.VersionHeader).Single());

        using var raw = new HttpClient(new SocketsHttpHandler { SslOptions = new() { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
        var missing = await raw.GetAsync(new Uri(server.Url, "/nothing/here"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("1", missing.Headers.GetValues(ManagementApi.VersionHeader).Single());
    }

    [Fact]
    public async Task KestrelsOwnRequestTimeoutAnswerIsWrittenBeforeTheMiddlewareAndHasNoVersion()
    {
        // the premise of the retry above, shown on a real Kestrel: headers that arrive later than the header timeout (a stalled process does the same) are answered 408 by Kestrel, not by the pipeline
        using var certificate = TestServer.SelfSigned();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(1);
            o.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
        });
        await using var app = builder.Build();
        app.UseManagementApiVersion();
        app.MapGet("/ok", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        var port = new Uri(app.Urls.First()).Port;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("localhost");
        await tls.WriteAsync("GET /ok HTTP/1.1\r\nHost: localhost\r\n"u8.ToArray(), TestContext.Current.CancellationToken);   // no blank line: the headers never end
        using var reader = new StreamReader(tls);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.StartsWith("HTTP/1.1 408", text);
        Assert.DoesNotContain(ManagementApi.VersionHeader, text, StringComparison.OrdinalIgnoreCase);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Task<TestServer> ServerOfVersion(int version, Action<int>? posted = null) =>
        TestServer.StartAsync((app, _) =>
        {
            app.MapGet(ManagementApi.PairingPath, () => Results.Json(new { instanceId = TestServer.InstanceId, apiVersion = version }));
            app.MapPost(ManagementApi.PairingPath, () => { posted?.Invoke(version); return Results.Json(new { }); });
        });

    [Fact]
    public async Task PairingWithAServerTwoVersionsAheadIsRefusedBeforeAnythingIsSent()
    {
        var posts = 0;
        await using var server = await ServerOfVersion(3, _ => posts++);
        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() =>
            PairingClient.PairAsync(server.Url, "AAAAA-AAAAA-AAAAA-AAAAA", new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken));
        Assert.EndsWith("Upgrade this console.", error.Message);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task PairingTellsTheServerOfOneVersionAheadToUpgradeTheConsoleToo()
    {
        await using var server = await ServerOfVersion(ManagementApi.Version + 1);
        await Assert.ThrowsAsync<IncompatibleServerException>(() =>
            PairingClient.PairAsync(server.Url, "AAAAA-AAAAA-AAAAA-AAAAA", new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheVersionIsReportedAtPairing()
    {
        await using var server = await TestServer.StartAsync();
        var paired = await PairingClient.PairAsync(server.Url, server.Codes.Create(ServerRole.Viewer).Code, new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken);
        Assert.Equal(ManagementApi.Version, paired.ApiVersion);
    }
}
