using System.Diagnostics;
using System.Net.Http.Headers;
using DbDataKit.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace DbDataKit.Hosting.Tests;

/// <summary>Plan 22a GP28: a host with <c>telemetry: enabled</c> makes the request span and counts the request; off creates nothing; no secret, query text or value is ever an attribute.</summary>
public sealed class TelemetryHostTests
{
    private const string Token = "TOKEN-SENTINEL-8f3a";
    private const string QueryText = "SELECT-SENTINEL-secret_col FROM payroll";
    private const string RowValue = "ROW-VALUE-SENTINEL-4471";
    private const string Parameter = "PARAM-SENTINEL-7aa2";

    private static async Task<WebApplication> StartAsync(TelemetrySettings telemetry)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.UseDbDataHost(o =>
        {
            o.Product = "telemetry-test";
            o.Version = "1.2.3";
            o.Telemetry = telemetry;
        }, args: []);
        var app = builder.Build();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPost("/api/v1/query/{name}", (string name) => Results.Json(new { rows = new[] { RowValue }, name }));
        app.MapGet("/api/v1/boom", () => Results.Json(new { }, statusCode: 500));
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Off_registers_nothing_and_no_request_is_traced()
    {
        using var capture = TelemetryCapture.Start();
        await using var app = await StartAsync(TelemetrySettings.Off);
        Assert.Null(app.Services.GetService<TracerProvider>());
        Assert.False(SuiteTelemetry.Source.HasListeners());
        var client = app.GetTestClient();
        using var response = await client.PostAsync("/api/v1/query/orders", new StringContent("{}"));
        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(capture.Spans);
    }

    [Fact]
    public async Task On_a_request_is_a_span_and_a_counter_and_no_secret_leaves()
    {
        using var capture = TelemetryCapture.Start();
        await using var app = await StartAsync(new TelemetrySettings(Enabled: true, Endpoint: new Uri("http://127.0.0.1:1"), InstanceId: "t-1"));
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/query/orders?sql={Uri.EscapeDataString(QueryText)}&p={Parameter}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.TryAddWithoutValidation("X-DbDataKit-Token", Token);
        request.Content = new StringContent($$"""{"query": "{{QueryText}}", "parameters": ["{{Parameter}}"]}""");
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);
        using var health = await client.GetAsync("/health/live");   // left out of the telemetry
        using var failed = await client.GetAsync("/api/v1/boom");
        capture.Flush(app.Services);

        var span = Assert.Single(capture.Spans, s => (string?)s.GetTagItem("http.route") == "/api/v1/query/{name}");
        Assert.Equal("POST /api/v1/query/{name}", span.DisplayName);
        Assert.Equal("POST", span.GetTagItem("http.request.method"));
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
        Assert.Equal("telemetry-test", span.GetTagItem(HostTelemetry.ProductTag));
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.DoesNotContain(capture.Spans, s => ((string?)s.GetTagItem("http.route"))?.StartsWith("/health", StringComparison.Ordinal) == true);
        Assert.Equal(1, capture.Sum("dbdatakit.http.server.requests", ("http.route", "/api/v1/query/{name}"), (HostTelemetry.ProductTag, "telemetry-test")));
        Assert.Equal(1, capture.Count("dbdatakit.http.server.duration", ("http.route", "/api/v1/query/{name}"), (HostTelemetry.ProductTag, "telemetry-test")));
        var boom = Assert.Single(capture.Spans, s => (string?)s.GetTagItem("http.route") == "/api/v1/boom");
        Assert.Equal(ActivityStatusCode.Error, boom.Status);

        // the scrub: nothing a request carried is in an attribute, a span name or a label
        var everything = string.Join('\n', capture.AllAttributes());
        foreach (var sentinel in new[] { Token, QueryText, "secret_col", RowValue, Parameter, "orders", "payroll" })
            Assert.DoesNotContain(sentinel, everything);
    }

    [Fact]
    public void A_name_that_is_not_an_identifier_is_not_a_label()
    {
        Assert.Equal("runs.select", HostTelemetry.Name("runs.select"));
        Assert.Equal("unnamed", HostTelemetry.Name("SELECT * FROM t WHERE a = 'x'"));
        Assert.Equal("unnamed", HostTelemetry.Name(null));
        Assert.Equal("unnamed", HostTelemetry.Name(new string('a', 65)));
    }

    [Fact]
    public async Task A_denied_attribute_set_by_code_is_removed_before_export()
    {
        using var capture = TelemetryCapture.Start();
        await using var app = await StartAsync(new TelemetrySettings(Enabled: true, Endpoint: new Uri("http://127.0.0.1:1")));
        using (var span = SuiteTelemetry.Source.StartActivity("manual"))
        {
            Assert.NotNull(span);
            span!.SetTag("db.statement", QueryText);
            span.SetTag("auth.token", Token);
            span.SetTag("dbdatakit.name", "kept");
        }

        capture.Flush(app.Services);
        var manual = Assert.Single(capture.Spans, s => s.OperationName == "manual");
        Assert.Null(manual.GetTagItem("db.statement"));
        Assert.Null(manual.GetTagItem("auth.token"));
        Assert.Equal("kept", manual.GetTagItem("dbdatakit.name"));
    }

    [Fact]
    public async Task A_run_counts_when_it_starts_and_when_it_ends_by_outcome()
    {
        using var capture = TelemetryCapture.Start();
        await using var app = await StartAsync(new TelemetrySettings(Enabled: true, Endpoint: new Uri("http://127.0.0.1:1")));
        HostTelemetry.StartRun("run-test", "replication", "orders").Succeeded();
        HostTelemetry.StartRun("run-test", "replication").Failed(new InvalidOperationException("password=hunter2"));
        HostTelemetry.StartRun("run-test", "replication").Cancelled();
        HostTelemetry.TaskStarted("run-test", "nightly-load");
        capture.Flush(app.Services);
        Assert.Equal(3, capture.Sum("dbdatakit.run.starts", (HostTelemetry.ProductTag, "run-test")));
        Assert.Equal(1, capture.Sum("dbdatakit.runs", (HostTelemetry.ProductTag, "run-test"), (HostTelemetry.OutcomeTag, "failed")));
        Assert.Equal(1, capture.Sum("dbdatakit.runs", (HostTelemetry.ProductTag, "run-test"), (HostTelemetry.OutcomeTag, "succeeded")));
        Assert.Equal(3, capture.Count("dbdatakit.run.duration", (HostTelemetry.ProductTag, "run-test")));
        Assert.Equal(1, capture.Sum("dbdatakit.runner.task_starts", (HostTelemetry.NameTag, "nightly-load")));
        var failed = Assert.Single(capture.Spans, s => (string?)s.GetTagItem(HostTelemetry.OutcomeTag) == "failed");
        Assert.Equal("System.InvalidOperationException", failed.GetTagItem("error.type"));
        Assert.DoesNotContain("hunter2", string.Join('\n', capture.AllAttributes()));
    }
}
