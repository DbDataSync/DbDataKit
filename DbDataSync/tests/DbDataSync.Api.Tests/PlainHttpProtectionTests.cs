using System.Net;
using DbDataSync.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace DbDataSync.Api.Tests;

public sealed class PlainHttpProtectionTests
{
    private static ApiOptions Options(string[]? trustedProxies = null, bool allowInsecure = false) => new()
    {
        RepoRoot = "unused",
        StateDbPath = "unused",
        TaskRunnerDllPath = "unused",
        CliDllPath = "unused",
        TrustedProxies = trustedProxies ?? [],
        AllowInsecureHttp = allowInsecure,
    };

    /// <summary>A server with only the guard in front of two routes. The client's address is whatever the request's
    /// <c>X-Test-Remote</c> header says, standing in for the connection's remote address.</summary>
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(ApiOptions options, bool https = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Remote", out var remote))
                context.Connection.RemoteIpAddress = IPAddress.Parse(remote.ToString());
            return next();
        });
        PlainHttpProtection.Apply(app, options);
        app.MapGet("/", (HttpContext context) => $"ok {context.Connection.RemoteIpAddress}");
        app.MapGet("/api/health", () => "healthy");
        await app.StartAsync();
        var client = app.GetTestClient();
        if (https)
            client.BaseAddress = new Uri("https://dbdatasync.example");
        return (App: app, Client: client);
    }

    private static HttpRequestMessage Get(string path, string remote, string? forwardedProto = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Remote", remote);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    [Fact]
    public async Task PlainHttpFromANonLocalClient_IsRefusedAndTheMessageNamesTheFix()
    {
        var (app, client) = await StartAsync(Options());
        await using var _ = app;

        var response = await client.SendAsync(Get("/", "192.168.1.20"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TrustedProxies", body);
        Assert.Contains("AllowInsecureHttp", body);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task PlainHttpFromALoopbackClient_IsServed(string remote)
    {
        var (app, client) = await StartAsync(Options());
        await using var _ = app;

        var response = await client.SendAsync(Get("/", remote));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HttpsFromANonLocalClient_IsServed()
    {
        var (app, client) = await StartAsync(Options(), https: true);
        await using var _ = app;

        var response = await client.SendAsync(Get("/", "192.168.1.20"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheHealthEndpoint_IsServedOverPlainHttpToAnyone()
    {
        var (app, client) = await StartAsync(Options());
        await using var _ = app;

        var response = await client.SendAsync(Get("/api/health", "192.168.1.20"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ATrustedProxyThatSaysHttps_IsBelieved_AndTheForwardedClientIsSeen()
    {
        var (app, client) = await StartAsync(Options(trustedProxies: ["10.0.0.0/8"]));
        await using var _ = app;

        var response = await client.SendAsync(Get("/", "10.1.2.3", forwardedProto: "https", forwardedFor: "203.0.113.9"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok 203.0.113.9", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATrustedProxyThatSaysHttp_IsStillRefused()
    {
        var (app, client) = await StartAsync(Options(trustedProxies: ["10.1.2.3"]));
        await using var _ = app;

        var response = await client.SendAsync(Get("/", "10.1.2.3", forwardedProto: "http", forwardedFor: "203.0.113.9"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeadersFromAnUntrustedAddress_AreIgnored()
    {
        var (app, client) = await StartAsync(Options(trustedProxies: ["10.0.0.0/8"]));
        await using var _ = app;

        var response = await client.SendAsync(Get("/", "192.168.1.20", forwardedProto: "https", forwardedFor: "127.0.0.1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AllowInsecureHttp_ServesPlainHttpToAnyone()
    {
        var (app, client) = await StartAsync(Options(allowInsecure: true));
        await using var _ = app;

        var response = await client.SendAsync(Get("/", "192.168.1.20"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWithNoKnownClientAddress_IsServed()
    {
        // In-memory test servers and unix sockets have no remote address; nothing remote is being asked for.
        var (app, client) = await StartAsync(Options());
        await using var _ = app;

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/99")]
    public void ATrustedProxyEntryThatIsNeitherAnAddressNorARange_IsRejectedAtStartup(string entry)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PlainHttpProtection.ForwardedHeaders([entry]));

        Assert.Contains(entry, ex.Message);
    }

    [Fact]
    public void StartupWarning_PlainHttpOffLoopbackWithNothingSaidAboutIt_Warns()
    {
        Assert.NotNull(PlainHttpProtection.StartupWarning(Options(), ["http://0.0.0.0:8080"]));
        Assert.NotNull(PlainHttpProtection.StartupWarning(Options(), ["http://+:5080"]));
    }

    [Fact]
    public void StartupWarning_NothingToWarnAbout_IsNull()
    {
        Assert.Null(PlainHttpProtection.StartupWarning(Options(), ["http://localhost:5080"]));
        Assert.Null(PlainHttpProtection.StartupWarning(Options(), ["https://0.0.0.0:8443"]));
        Assert.Null(PlainHttpProtection.StartupWarning(Options(trustedProxies: ["10.0.0.0/8"]), ["http://0.0.0.0:8080"]));
        Assert.Null(PlainHttpProtection.StartupWarning(Options(allowInsecure: true), ["http://0.0.0.0:8080"]));
    }
}
