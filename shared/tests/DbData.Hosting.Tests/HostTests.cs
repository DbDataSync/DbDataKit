using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DbData.Hosting.Tests;

public class HostTests
{
    private sealed class Probe(string name, HealthStatus status) : IReadinessProbe
    {
        public string Name => name;
        public Task<ComponentHealth> CheckAsync(CancellationToken ct) => Task.FromResult(new ComponentHealth(name, status));
    }

    private sealed class Throwing : IReadinessProbe
    {
        public string Name => "boom";
        public Task<ComponentHealth> CheckAsync(CancellationToken ct) => throw new InvalidOperationException("secret detail");
    }

    private static async Task<WebApplication> StartAsync(Action<DbDataHostOptions>? configure = null, params IReadinessProbe[] probes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var mode = builder.UseDbDataHost(o =>
        {
            o.Product = "TestProduct";
            o.Version = "1.2.3";
            o.Commit = "abc123";
            o.SchemaVersions["config"] = "3";
            o.DrainTimeout = TimeSpan.FromSeconds(2);
            configure?.Invoke(o);
        }, args: []);
        Assert.Equal(HostMode.Console, mode);
        foreach (var p in probes) builder.Services.AddSingleton(p);

        var app = builder.Build();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapGet("/work", () => "ok");
        app.MapPost("/work", () => "changed");
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Version_reports_product_commit_and_schema_versions()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var json = await client.GetFromJsonAsync<JsonElement>("/version");
        Assert.Equal("TestProduct", json.GetProperty("product").GetString());
        Assert.Equal("1.2.3", json.GetProperty("version").GetString());
        Assert.Equal("abc123", json.GetProperty("commit").GetString());
        Assert.Equal("3", json.GetProperty("schemaVersions").GetProperty("config").GetString());
        Assert.True(json.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Health_is_503_when_a_probe_is_unhealthy_and_does_not_leak_exception_text()
    {
        await using var app = await StartAsync(null, new Probe("db", HealthStatus.Healthy), new Throwing());
        var client = app.GetTestClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret detail", body);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Draining_rejects_mutations_but_keeps_reads_and_health()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/work", null)).StatusCode);

        await app.Services.GetRequiredService<IDrainCoordinator>().DrainAsync(TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/work", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/work")).StatusCode);
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode); // not ready while draining
    }

    [Fact]
    public async Task Drain_all_mode_gives_503_with_retry_after()
    {
        await using var app = await StartAsync(o => o.DrainRejection = DrainRejection.All);
        await app.Services.GetRequiredService<IDrainCoordinator>().DrainAsync(TimeSpan.Zero);
        var response = await app.GetTestClient().GetAsync("/work");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Concurrency_limit_sheds_load()
    {
        var gate = new TaskCompletionSource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.UseDbDataHost(o => { o.Product = "P"; o.Limits.MaxConcurrentRequests = 1; }, args: []);
        var app = builder.Build();
        app.UseDbDataPipeline();
        app.MapGet("/slow", async () => { await gate.Task; return "done"; });
        await app.StartAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            var first = client.GetAsync("/slow");
            await Task.Delay(200);
            var second = await client.GetAsync("/slow");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            gate.SetResult();
            Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        }
    }

    [Fact]
    public void Options_require_product()
    {
        var builder = WebApplication.CreateBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.UseDbDataHost(_ => { }, args: []));
    }

    [Fact]
    public void Forwarded_headers_apply_only_listed_proxies()
    {
        var policy = ForwardedHeadersPolicy.Parse(["10.1.1.1", "192.168.0.0/16"]);
        var target = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        DbDataHostExtensions.ApplyForwardedHeaders(policy, target);
        Assert.Single(target.KnownProxies);
        Assert.Single(target.KnownIPNetworks);
        Assert.Equal(1, target.ForwardLimit);
    }
}
