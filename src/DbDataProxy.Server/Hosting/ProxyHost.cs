using System.Net;
using System.Security.Cryptography.X509Certificates;
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
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddSystemd();

        builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection(ProxyOptions.SectionName));
        configure?.Invoke(builder);

        var options = builder.Configuration.GetSection(ProxyOptions.SectionName).Get<ProxyOptions>() ?? new ProxyOptions();
        ConfigureServices(builder.Services, options);
        ConfigureKestrel(builder, options);

        var app = builder.Build();
        app.MapFlightEndpoint();
        app.MapGet("/", () => "DbDataProxy Arrow Flight SQL endpoint. Connect with a Flight SQL client.");
        return app;
    }

    private static void ConfigureServices(IServiceCollection services, ProxyOptions options)
    {
        if (options.Compression.Mode == CompressionMode.Ipc || options.DataSources.Values.Any(d => d.Compression?.Mode == CompressionMode.Ipc))
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
        X509Certificate2? certificate = null;

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
                certificate ??= CertificateProvider.Load(options, dataDirectory, loggerFactory.CreateLogger("Tls"));
                listen.UseHttps(certificate!);
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
