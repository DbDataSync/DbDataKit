using DbDataKit.Serving;
using DbDataKit.Serving.FlightSql;
using DbDataKit.Serving.Semantic;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting;
using CertificateReadinessProbe = DbDataKit.Hosting.Certificates.CertificateReadinessProbe;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Serving;
using DbDataProxy.Server.Management;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.Tls;
using Grpc.Net.Compression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using DbDataKit.Secrets;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Hosting;

public static class ProxyHost
{
    public const string ServiceName = "DbDataProxy";

    /// <summary>The name the Windows service is registered under by default: lower case like the executable (the service control manager compares names without case, so an existing registration is updated in place).
    /// <see cref="ServiceName"/> stays the product's name for its data directory.</summary>
    public const string RegistrationName = "dbdataproxy";

    /// <summary>
    /// The web application builder with the proxy's configuration sources (the data directory's config folder, DBPROXY_ environment variables, command line).
    /// A configuration file that cannot be parsed surfaces as <see cref="ProxyConfigException"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        WebApplicationBuilder builder;
        try
        {
            builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                // A Windows service starts in System32; settings and the worker are found relative to the executable.
                ContentRootPath = AppContext.BaseDirectory,
            });
            // The settings live in the data directory's config folder (Configuration/ProxyConfigFile.cs). The file of earlier releases is refused.
            var dataDirectory = ProxyConfigFile.ResolveDataDirectory(args);
            if (ProxyConfigFile.LegacyFileMessage(dataDirectory) is { } legacy) throw new ProxyConfigException(legacy, []);
            ProxyConfigFile.AddDataDirectory(builder.Configuration, dataDirectory, args);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            throw new ProxyConfigException(DescribeLoadFailure(ex), []);
        }
        // Quieter defaults for the framework's chattiest categories, unless the settings say otherwise (they used to be shipped in appsettings.json).
        foreach (var (category, level) in new[] { ("Microsoft.AspNetCore", LogLevel.Warning), ("Grpc", LogLevel.Warning) })
            if (builder.Configuration[$"Logging:LogLevel:{category}"] is null) builder.Logging.AddFilter(category, level);
        return builder;
    }

    private static string DescribeLoadFailure(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return "The configuration could not be read: " + string.Join(" ", parts.Distinct());
    }

    /// <summary>
    /// Builds the proxy host. <paramref name="configure"/> lets tests and embedders adjust it before it is built.
    /// <paramref name="overrideServices"/> runs after the proxy's own registrations, so a test can replace one (for example the provider registry).
    /// Throws <see cref="ProxyConfigException"/> when the effective configuration is not valid (the command line exits with code 2).
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, Action<IServiceCollection>? overrideServices = null)
    {
        var builder = CreateBuilder(args);
        var loadGuard = ProxyConfigLoadGuard.Install(builder.Configuration);
        configure?.Invoke(builder);

        // Strict startup validation: unknown keys, types, ranges and cross-references are errors. Later edits of a running service are
        // validated the same way, and a rejected edit keeps the last good configuration (ProxyOptionsMonitor).
        var configRoot = (IConfigurationRoot)builder.Configuration;
        var loaded = ProxyConfig.LoadOrThrow(configRoot);
        builder.Services.AddSingleton<IOptionsMonitor<ProxyOptions>>(sp =>
            new ProxyOptionsMonitor(configRoot, loaded.Value!, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataProxy.Config"), loadGuard));

        // One redactor for the process: every resolved secret is registered with it, every logger and every client-facing
        // error text passes through it.
        var redactor = new Redactor();
        builder.Services.AddSingleton(redactor);
        builder.Services.AddSingleton<IRedactor>(redactor);
        builder.Services.AddSingleton<SecretsRuntime>();
        RedactLogging(builder.Services, redactor);

        var options = loaded.Value!;
        UseSharedHost(builder, options, args);
        ConfigureServices(builder.Services, options);
        ConfigureKestrel(builder, options);
        overrideServices?.Invoke(builder.Services); // a seam for tests: replace a registration made above (the last one wins)

        var app = builder.Build();
        if (loaded.Diagnostics.Count > 0)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataProxy.Config").LogWarning("{Details}", ProxyConfig.Format(loaded.Diagnostics));
        // Resolves every referenced secret (registering it for redaction), warns about literals, and fails when
        // Proxy:RequireReferencedSecrets is on and one is found.
        app.Services.GetRequiredService<SecretsRuntime>().ValidateStartup(app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue);
        ConnectionStartupChecks.WarnAboutTlsOptOuts(
            app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue, app.Services.GetRequiredService<SecretsRuntime>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataProxy.Connections"));
        // The management API has a listener of its own (plan 13, P03); the guard keeps it off the Flight port and the Flight RPCs off it.
        ProxyManagementHost.Guard(app);
        app.UseDbDataPipeline();
        ProxyManagementHost.Map(app, options, ResolveDataDirectory(options), app.Services.GetRequiredService<TlsCertificateHolder>(), configRoot);
        app.MapDbDataEndpoints();
        app.MapFlightEndpoint();
        app.MapGet("/", () => "DbDataProxy Arrow Flight SQL endpoint. Connect with a Flight SQL client.");
        return app;
    }

    /// <summary>Replaces the logger factory with one whose loggers scrub secrets from message and exception text.</summary>
    private static void RedactLogging(IServiceCollection services, IRedactor redactor)
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(ILoggerFactory) && d.ImplementationType is not null)
            ?? throw new InvalidOperationException("Logging is not configured.");
        services.Remove(original);
        services.AddSingleton<ILoggerFactory>(sp =>
            new RedactingLoggerFactory((ILoggerFactory)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), redactor));
    }
    /// <summary>
    /// Service/systemd integration, drain on stop, /health and /version, and request limits come from the shared host.
    /// The limits are set so that nothing a client could do before is refused: a Flight stream may run for hours and
    /// bodies keep Kestrel's 30 MB default; only the in-flight request cap (default 1000) is new.
    /// </summary>
    private static void UseSharedHost(WebApplicationBuilder builder, ProxyOptions options, string[] args) =>
        builder.UseDbDataHost(host =>
        {
            host.Product = "dbdataproxy";
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
        services.AddSingleton<IBasicCredentialValidator, LocalUserValidator>();
        services.AddSingleton<IWindowsLogon, NativeWindowsLogon>();
        services.AddSingleton<IBasicCredentialValidator, WindowsAccountValidator>();
        services.AddSingleton<IBasicCredentialValidator, PassThroughValidator>();
        services.AddSingleton<ChainedCredentialValidator>();
        services.AddSingleton<IJwtValidator, JwtBearerValidator>();
        services.AddSingleton<ProxyServing>();
        services.AddSingleton(sp =>
        {
            var current = sp.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataProxy.Semantic");
            var loaded = SemanticModelsLoader.Load(current, ResolveDataDirectory(current), sp.GetRequiredService<SecretsRuntime>(), problem => log.LogWarning("{Problem}", problem));
            // the saved queries people keep for themselves (J27) are looked up in the state database
            loaded.UserQueries = sp.GetRequiredService<DbDataProxy.Server.State.SavedQueryStore>();
            // a changed server.yaml is applied to the models without a restart; the first pass only learns which names the file gives
            var directory = ResolveDataDirectory(current);
            var configured = SemanticModelsLoader.Reload(loaded, current, directory, new HashSet<string>(), _ => { });
            sp.GetRequiredService<IOptionsMonitor<ProxyOptions>>().OnChange((changed, _) =>
                configured = SemanticModelsLoader.Reload(loaded, changed, ResolveDataDirectory(changed), configured, problem => log.LogWarning("{Problem}", problem)));
            return loaded;
        });
        services.AddSingleton<IServingServices>(sp => sp.GetRequiredService<ProxyServing>().WithSemantic(sp.GetRequiredService<SemanticModels>()));
        services.AddSingleton(sp =>
        {
            var current = sp.GetRequiredService<IOptionsMonitor<ProxyOptions>>();
            return new FlightSqlModule(new FlightSqlOptions
            {
                Compression = () => current.CurrentValue.Compression,
                SourceCompression = name => current.CurrentValue.DataSources.TryGetValue(name, out var source) ? source.Compression : null,
                LogSql = () => current.CurrentValue.LogSql,
                ServerName = "DbDataProxy",
                ServerVersion = typeof(ProxyHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            }, sp.GetRequiredService<TimeProvider>());
        });
        services.AddHostedService<FlightSqlModuleService>();

        // The shared providers: one registry, one composer, one failure model per engine. With Proxy:LogDriverErrors the providers keep the
        // (redacted) driver text of failed connections on the failure, for the local log only.
        services.AddSingleton(sp =>
        {
            var redactor = sp.GetRequiredService<IRedactor>();
            bool raw = sp.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue.LogDriverErrors;
            return new DbDataKit.Connections.ProviderRegistry(
            [
                new DbDataKit.Connections.Odbc.OdbcProvider(redactor: redactor, includeRawFailures: raw),
                new DbDataKit.Connections.SqlServer.SqlServerProvider(redactor: redactor, includeRawFailures: raw),
                new DbDataKit.Connections.DuckDb.DuckDbProvider(redactor: redactor, includeRawFailures: raw),
                new DbDataKit.Connections.Postgres.PostgresProvider(redactor: redactor, includeRawFailures: raw),
                new DbDataKit.Connections.FlightSql.FlightSqlProvider(redactor: redactor, includeRawFailures: raw),
            ]);
        });
        services.AddSingleton<QueryLimits>();
        services.AddSingleton<ActiveQueries>();
        services.AddSingleton<RecentFailures>();
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
                o.Interceptors.Add<RedactingInterceptor>(); // outermost: scrubs the detail of every RpcException
                o.Interceptors.Add<FlightAuthInterceptor>();
                o.Interceptors.Add<LoginThrottleInterceptor>();
                o.MaxReceiveMessageSize = 64 * 1024 * 1024;
                o.ResponseCompressionAlgorithm = "gzip";
                o.ResponseCompressionLevel = System.IO.Compression.CompressionLevel.Fastest;
                o.CompressionProviders = [new GzipCompressionProvider(System.IO.Compression.CompressionLevel.Fastest)];
            })
            .AddFlightServer<ServingFlightServer>();
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, ProxyOptions options)
    {
        string dataDirectory = ResolveDataDirectory(options);
        var holder = new TlsCertificateHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(sp => DbDataProxy.Server.State.ProxyState.Open(dataDirectory, new DbDataKit.State.StateSettings(options.State.Engine, options.State.ConnectionString),
            text => sp.GetRequiredService<SecretsRuntime>().ResolveConnectionString("Proxy:State:ConnectionString", text, "state")));
        builder.Services.AddSingleton(sp => new DbDataKit.State.NotificationStore(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        builder.Services.AddSingleton(sp => new DbDataProxy.Server.State.SavedQueryStore(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        builder.Services.AddSingleton<ProxyEvents>();
        builder.Services.AddHostedService<DbDataProxy.Server.State.ProxyStateStartup>();
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

                var services = kestrel.ApplicationServices;
                if (holder.Current is null)
                    holder.Set(CertificateProvider.Load(options, dataDirectory,
                        services.GetRequiredService<ILoggerFactory>().CreateLogger("Tls"), secrets: services.GetRequiredService<SecretsRuntime>()));
                listen.UseHttps(https =>
                {
                    // Looked up per handshake so a rotated certificate is served without a restart.
                    https.ServerCertificateSelector = (_, _) => holder.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
            ProxyManagementHost.Listen(kestrel, options, holder);
        });
    }

    public static string ResolveDataDirectory(ProxyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DataDirectory)) return Path.GetFullPath(options.DataDirectory);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(string.IsNullOrEmpty(common) ? AppContext.BaseDirectory : common, ServiceName);
    }
}

/// <summary>Housekeeping: drops expired sessions and the Flight SQL results and statements nobody came back for.</summary>
public sealed class MaintenanceService(SessionStore sessions, LoginThrottle throttle, FlightSqlModule flight, ILogger<MaintenanceService> log) : BackgroundService
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
                    await flight.SweepAsync();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Maintenance sweep failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>Starts the Flight SQL module on Proxy's services when the host starts, and drains it and stops it (disposing the results nobody fetched) when the host stops.</summary>
public sealed class FlightSqlModuleService(FlightSqlModule module, IServingServices services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => module.StartAsync(services, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await module.DrainAsync(cancellationToken);
        await module.StopAsync(cancellationToken);
    }
}
