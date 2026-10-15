using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting;
using CertificateReadinessProbe = DbDataKit.Hosting.Certificates.CertificateReadinessProbe;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Flight;
using DbDataProxy.Server.Tls;
using Grpc.Net.Compression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Hosting;

public static class ProxyHost
{
    public const string ServiceName = "DbDataProxy";

    /// <summary>Builds the proxy host. <paramref name="configure"/> lets tests and embedders adjust it before it is built.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // A Windows service starts in System32; settings and the worker are found relative to the executable.
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration.AddEnvironmentVariables("DBPROXY_");
        builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection(ProxyOptions.SectionName));
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(ProxyOptions.SectionName).Get<ProxyOptions>() ?? new ProxyOptions();
        UseSharedHost(builder, options, args);
        ConfigureServices(builder.Services, options);
        ConfigureKestrel(builder, options);

        var app = builder.Build();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapFlightEndpoint();
        app.MapGet("/", () => "DbDataProxy Arrow Flight SQL endpoint. Connect with a Flight SQL client.");
        return app;
    }

    /// <summary>
    /// Service/systemd integration, drain on stop, /health and /version, and request limits come from the shared host.
    /// The limits are set so that nothing a client could do before is refused: a Flight stream may run for hours and
    /// bodies keep Kestrel's 30 MB default; only the in-flight request cap (default 1000) is new.
    /// </summary>
    private static void UseSharedHost(WebApplicationBuilder builder, ProxyOptions options, string[] args) =>
        builder.UseDbDataHost(host =>
        {
            host.Product = ServiceName;
            host.Version = Version;
            host.DrainTimeout = TimeSpan.FromSeconds(Math.Max(0, options.Hosting.DrainSeconds));
            host.Limits.MaxConcurrentRequests = Math.Max(1, options.Hosting.MaxConcurrentRequests);
            host.Limits.RequestTimeout = TimeSpan.FromDays(7);
            host.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            host.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(130);
            host.Limits.MaxRequestBodyBytes = 30_000_000;
            host.Limits.MaxMessageBytes = 64 * 1024 * 1024;
        }, args);

    public static string Version { get; } =
        (typeof(ProxyHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static void ConfigureServices(IServiceCollection services, ProxyOptions options)
    {
        if (options.Compression.Mode == CompressionMode.Arrow || options.DataSources.Values.Any(d => d.Compression?.Mode == CompressionMode.Arrow))
            FlightIpcCompression.EnsureSupported();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SessionStore>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<PendingQueryStore>();
        services.AddSingleton<PreparedStatementStore>();
        services.AddSingleton<IBasicCredentialValidator, LocalUserValidator>();
        services.AddSingleton<IWindowsLogon, NativeWindowsLogon>();
        services.AddSingleton<IBasicCredentialValidator, WindowsAccountValidator>();
        services.AddSingleton<IBasicCredentialValidator, PassThroughValidator>();
        services.AddSingleton<ChainedCredentialValidator>();
        services.AddSingleton<IJwtValidator, JwtBearerValidator>();
        services.AddSingleton<RequestAuthenticator>();

        services.AddSingleton<IDbProvider, OdbcProvider>();
        services.AddSingleton<IDbProvider, SqlServerProvider>();
        services.AddSingleton<IDbProvider, DuckDbProvider>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<WorkerProcessHost>();
            services.AddSingleton<IWorkerExecutorFactory>(sp => sp.GetRequiredService<WorkerProcessHost>());
        }
        services.AddSingleton<DataSourceCatalog>();

        services.AddHostedService<MaintenanceService>();

        services
            .AddGrpc(o =>
            {
                o.Interceptors.Add<AuthInterceptor>();
                o.MaxReceiveMessageSize = 64 * 1024 * 1024;
                o.ResponseCompressionAlgorithm = "gzip";
                o.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Fastest;
                o.CompressionProviders = [new GzipCompressionProvider(System.IO.Compression.CompressionLevel.Fastest)];
            })
            .AddFlightServer<ProxyFlightServer>();
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, ProxyOptions options)
    {
        string dataDirectory = ResolveDataDirectory(options);
        var holder = new TlsCertificateHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton<IReadinessProbe>(new CertificateReadinessProbe(() => holder.Current, CertificateProvider.RenewBeforeDays));
        builder.Services.AddHostedService(sp => new CertificateRotationService(
            holder, options, dataDirectory, sp.GetRequiredService<ILogger<CertificateRotationService>>()));

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Flight streams large result sets; do not cap or time out the response body.
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);

            var address = IPAddress.Parse(options.Listen.Address);
            kestrel.Listen(address, options.Listen.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (options.Tls.Mode == TlsMode.None) return;

                using var loggerFactory = LoggerFactory.Create(l => l.AddSimpleConsole());
                if (holder.Current is null) holder.Set(CertificateProvider.Load(options, dataDirectory, loggerFactory.CreateLogger("Tls")));
                listen.UseHttps(https =>
                {
                    // Looked up per handshake so a rotated certificate is served without a restart.
                    https.ServerCertificateSelector = (_, _) => holder.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
        });
    }

    public static string ResolveDataDirectory(ProxyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DataDirectory)) return Path.GetFullPath(options.DataDirectory);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(string.IsNullOrEmpty(common) ? AppContext.BaseDirectory : common, ServiceName);
    }
}

/// <summary>Housekeeping: drops expired sessions and disposes query results nobody fetched.</summary>
public sealed class MaintenanceService(SessionStore sessions, LoginThrottle throttle, PendingQueryStore pending, PreparedStatementStore prepared, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    sessions.Sweep();
                    throttle.Sweep();
                    prepared.Sweep();
                    await pending.SweepAsync();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Maintenance sweep failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await pending.DisposeAsync();
    }
}
