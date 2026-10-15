using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace DbData.Hosting;

public static class DbDataHostExtensions
{
    /// <summary>
    /// Standard host wiring: console vs Windows service vs systemd, drain on stop, request limits, health/version
    /// state. Returns the chosen mode. Call <see cref="UseDbDataPipeline"/> and <see cref="MapDbDataEndpoints"/> on the app.
    /// </summary>
    public static HostMode UseDbDataHost(this WebApplicationBuilder builder, Action<DbDataHostOptions> configure, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DbDataHostOptions();
        configure(options);
        options.Validate();

        var mode = options.ModeOverride ?? HostModeDetector.Detect(
            args ?? Environment.GetCommandLineArgs().Skip(1).ToArray(),
            OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService(),
            SystemdHelpers.IsSystemdService());

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton<IDrainCoordinator, DrainCoordinator>();
        services.AddHostedService<DrainOnStopService>();
        services.Configure<HostOptions>(h => h.ShutdownTimeout = options.DrainTimeout + TimeSpan.FromSeconds(15));
        services.AddRequestTimeouts(o => o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy { Timeout = options.Limits.RequestTimeout });

        if (options.ForwardedHeaders.Enabled)
        {
            services.Configure<ForwardedHeadersOptions>(o => ApplyForwardedHeaders(options.ForwardedHeaders, o));
        }

        switch (mode)
        {
            case HostMode.WindowsService:
                services.AddWindowsService(o => o.ServiceName = options.ServiceName ?? options.Product);
                break;
            case HostMode.Systemd:
                services.AddSystemd();
                break;
        }

        builder.WebHost.ConfigureKestrel(k =>
        {
            var l = k.Limits;
            l.MaxRequestBodySize = options.Limits.MaxRequestBodyBytes;
            l.RequestHeadersTimeout = options.Limits.RequestHeadersTimeout;
            l.KeepAliveTimeout = options.Limits.KeepAliveTimeout;
            l.MaxConcurrentConnections = options.Limits.MaxConcurrentRequests * 4;
        });

        return mode;
    }

    /// <summary>Forwarded headers (only if configured), request timeouts, concurrency limit, drain.</summary>
    public static IApplicationBuilder UseDbDataPipeline(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<DbDataHostOptions>();
        if (options.ForwardedHeaders.Enabled)
        {
            app.UseForwardedHeaders();
        }

        app.UseRequestTimeouts();
        app.UseMiddleware<ConcurrencyLimitMiddleware>();
        app.UseMiddleware<DrainMiddleware>();
        return app;
    }

    /// <summary>Maps <c>/health</c>, <c>/health/live</c>, <c>/health/ready</c> and <c>/version</c>.</summary>
    public static IEndpointRouteBuilder MapDbDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Json(new { live = true })).DisableRequestTimeout();

        endpoints.MapGet("/health", async (HttpContext ctx) =>
        {
            var report = await Evaluate(ctx);
            return Results.Json(report, statusCode: report.Ready ? 200 : 503);
        });

        endpoints.MapGet("/health/ready", async (HttpContext ctx) =>
        {
            var report = await Evaluate(ctx);
            return Results.Json(report, statusCode: report.Ready ? 200 : 503);
        });

        endpoints.MapGet("/version", async (HttpContext ctx) =>
        {
            var report = await Evaluate(ctx);
            var info = ctx.RequestServices.GetRequiredService<DbDataHostOptions>().ToVersionInfo(report.Ready);
            return Results.Json(info);
        });

        return endpoints;
    }

    public static void ApplyForwardedHeaders(ForwardedHeadersPolicy policy, ForwardedHeadersOptions target)
    {
        target.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        target.ForwardLimit = policy.ForwardLimit;
        target.KnownProxies.Clear();
        target.KnownIPNetworks.Clear();
        foreach (var p in policy.TrustedProxies) target.KnownProxies.Add(p);
        foreach (var (prefix, len) in policy.TrustedNetworks) target.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, len));
    }

    private static Task<HealthReport> Evaluate(HttpContext ctx) =>
        HealthEvaluator.EvaluateAsync(
            ctx.RequestServices.GetServices<IReadinessProbe>(),
            ctx.RequestServices.GetRequiredService<IDrainCoordinator>(),
            ctx.RequestAborted);

    private sealed class DrainOnStopService(
        IHostApplicationLifetime lifetime, IDrainCoordinator drain, DbDataHostOptions options) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // ApplicationStopping fires before Kestrel stops accepting; blocking here is intentional and bounded.
            lifetime.ApplicationStopping.Register(() =>
                drain.DrainAsync(options.DrainTimeout).GetAwaiter().GetResult());
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
