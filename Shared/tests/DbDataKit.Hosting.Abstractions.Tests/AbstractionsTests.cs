using System.Net;

namespace DbDataKit.Hosting.Abstractions.Tests;

public class HostModeTests
{
    [Theory]
    [InlineData(false, false, false, HostMode.Console)]
    [InlineData(false, true, false, HostMode.WindowsService)]
    [InlineData(false, false, true, HostMode.Systemd)]
    [InlineData(true, true, false, HostMode.Console)]
    public void Detects(bool consoleFlag, bool win, bool systemd, HostMode expected)
    {
        string[] args = consoleFlag ? ["--console"] : [];
        Assert.Equal(expected, HostModeDetector.Detect(args, win, systemd));
    }
}

public class DrainTests
{
    [Fact]
    public async Task Drain_returns_true_when_idle()
    {
        var d = new DrainCoordinator();
        Assert.True(await d.DrainAsync(TimeSpan.FromSeconds(1)));
        Assert.True(d.IsDraining);
    }

    [Fact]
    public async Task Drain_waits_for_work_then_completes()
    {
        var d = new DrainCoordinator();
        var work = d.TrackWork();
        var drain = d.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.False(drain.IsCompleted);
        work.Dispose();
        Assert.True(await drain);
    }

    [Fact]
    public async Task Drain_times_out_with_stuck_work()
    {
        var d = new DrainCoordinator();
        using var work = d.TrackWork();
        Assert.False(await d.DrainAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, d.ActiveWork);
    }

    [Fact]
    public async Task New_work_refused_while_draining_unless_allowed()
    {
        var d = new DrainCoordinator();
        await d.DrainAsync(TimeSpan.Zero);
        Assert.Throws<InvalidOperationException>(() => d.TrackWork());
        d.TrackWork(allowWhileDraining: true).Dispose();
        d.Resume();
        d.TrackWork().Dispose();
    }

    [Fact]
    public void Double_dispose_releases_once()
    {
        var d = new DrainCoordinator();
        var w = d.TrackWork();
        w.Dispose();
        w.Dispose();
        Assert.Equal(0, d.ActiveWork);
    }
}

public class ModelTests
{
    [Fact]
    public void Health_report_aggregates_worst_and_draining_blocks_ready()
    {
        var r = HealthReport.From([new("a", HealthStatus.Healthy), new("b", HealthStatus.Degraded)], draining: false);
        Assert.Equal(HealthStatus.Degraded, r.Status);
        Assert.True(r.Ready);
        Assert.False(HealthReport.From([], draining: true).Ready);
        Assert.False(HealthReport.From([new("x", HealthStatus.Unhealthy)], false).Ready);
    }

    [Fact]
    public void Limits_have_safe_defaults_and_validate()
    {
        var l = new RequestLimitOptions();
        l.Validate();
        Assert.True(l.MaxRequestBodyBytes <= 16 * 1024 * 1024);
        l.MaxConcurrentRequests = 0;
        Assert.Throws<ArgumentOutOfRangeException>(l.Validate);
    }

    [Fact]
    public void Forwarded_policy_parses_and_refuses_trust_everything()
    {
        var p = ForwardedHeadersPolicy.Parse(["127.0.0.1", "10.0.0.0/24", " "]);
        Assert.True(p.Enabled);
        Assert.Contains(IPAddress.Loopback, p.TrustedProxies);
        Assert.Single(p.TrustedNetworks);
        Assert.False(new ForwardedHeadersPolicy().Enabled);
        Assert.Throws<FormatException>(() => ForwardedHeadersPolicy.Parse(["0.0.0.0/0"]));
        Assert.Throws<FormatException>(() => ForwardedHeadersPolicy.Parse(["10.0.0.0/33"]));
    }

    [Fact]
    public async Task Health_probe_client_maps_status()
    {
        var ok = new StubHandler(HttpStatusCode.OK);
        var bad = new StubHandler(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(0, await HealthProbeClient.RunAsync(new Uri("http://x/health"), TimeSpan.FromSeconds(1), ok));
        Assert.Equal(1, await HealthProbeClient.RunAsync(new Uri("http://x/health"), TimeSpan.FromSeconds(1), bad));
    }

    [Fact]
    public async Task Healthcheck_flag_is_recognised_only_when_present()
    {
        Assert.Null(await HealthProbeClient.TryHandleAsync(["--urls", "x"]));
        Assert.Equal(2, await HealthProbeClient.TryHandleAsync(["--healthcheck", "not a url"]));
        Assert.Equal(1, await HealthProbeClient.TryHandleAsync(["--healthcheck", "http://127.0.0.1:1/health"]));
    }

    [Fact]
    public async Task Health_probe_can_require_http2_and_uses_the_product_default_url()
    {
        var seen = new CapturingHandler();
        var options = new HealthProbeOptions { HttpVersion = HttpVersion.Version20, DefaultUrl = "https://127.0.0.1:8815/health/live" };
        Assert.Equal(0, await HealthProbeClient.RunAsync(new Uri("https://x/health"), TimeSpan.FromSeconds(1), seen, options));
        Assert.Equal(HttpVersion.Version20, seen.Request!.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, seen.Request.VersionPolicy);

        // --healthcheck with no URL uses the product default (which here refuses the connection: exit 1, not usage 2).
        Assert.Equal(1, await HealthProbeClient.TryHandleAsync(["--healthcheck"], new HealthProbeOptions { DefaultUrl = "http://127.0.0.1:1/health/live" }));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class StubHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(code));
    }
}
