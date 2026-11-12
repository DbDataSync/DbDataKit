using DbDataKit.Serving;
using DbDataKit.Serving.FlightSql;
using DbDataKit.Serving.Semantic;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting;
using CertificateReadinessProbe = DbDataKit.Hosting.Certificates.CertificateReadinessProbe;
using DbDataGateway.Data;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Tls;
using Grpc.Net.Compression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using DbDataKit.Secrets;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Hosting;

public static class GatewayHost
{
    public const string ServiceName = "DbDataGateway";

    /// <summary>The name the Windows service is registered under by default: lower case like the executable (the service control manager compares names without case, so an existing registration is updated in place).
    /// <see cref="ServiceName"/> stays the product's name for its data directory.</summary>
    public const string RegistrationName = "dbdatagateway";

    /// <summary>
    /// The web application builder with the gateway's configuration sources (the data directory's config folder, DBDATAKIT_ environment variables, command line).
    /// A configuration file that cannot be parsed surfaces as <see cref="GatewayConfigException"/>.
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
            // The settings live in the data directory's config folder (Configuration/GatewayConfigFile.cs). The file of earlier releases is refused.
            var dataDirectory = GatewayConfigFile.ResolveDataDirectory(args);
            if (GatewayConfigFile.LegacyFileMessage(dataDirectory) is { } legacy) throw new GatewayConfigException(legacy, []);
            GatewayConfigFile.AddDataDirectory(builder.Configuration, dataDirectory, args);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            throw new GatewayConfigException(DescribeLoadFailure(ex), []);
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
    /// Builds the gateway host. <paramref name="configure"/> lets tests and embedders adjust it before it is built.
    /// <paramref name="overrideServices"/> runs after the gateway's own registrations, so a test can replace one (for example the provider registry).
    /// Throws <see cref="GatewayConfigException"/> when the effective configuration is not valid (the command line exits with code 2).
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, Action<IServiceCollection>? overrideServices = null)
    {
        var builder = CreateBuilder(args);
        var loadGuard = GatewayConfigLoadGuard.Install(builder.Configuration);
        configure?.Invoke(builder);

        // Strict startup validation: unknown keys, types, ranges and cross-references are errors. Later edits of a running service are
        // validated the same way, and a rejected edit keeps the last good configuration (GatewayOptionsMonitor).
        var configRoot = (IConfigurationRoot)builder.Configuration;
        var loaded = GatewayConfig.LoadOrThrow(configRoot);
        builder.Services.AddSingleton<IOptionsMonitor<GatewayOptions>>(sp =>
            new GatewayOptionsMonitor(configRoot, loaded.Value!, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Config"), loadGuard));

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
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Config").LogWarning("{Details}", GatewayConfig.Format(loaded.Diagnostics));
        // Resolves every referenced secret (registering it for redaction), warns about literals, and fails when
        // Gateway:RequireReferencedSecrets is on and one is found.
        app.Services.GetRequiredService<SecretsRuntime>().ValidateStartup(app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue);
        ConnectionStartupChecks.WarnAboutTlsOptOuts(
            app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue, app.Services.GetRequiredService<SecretsRuntime>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Connections"));
        // The management API has a listener of its own (plan 13, P03); the guard keeps it off the Flight port and the Flight RPCs off it.
        GatewayManagementHost.Guard(app);
        app.UseDbDataPipeline();
        GatewayManagementHost.Map(app, options, ResolveDataDirectory(options), app.Services.GetRequiredService<TlsCertificateHolder>(), configRoot);
        app.MapDbDataEndpoints();
        if (GatewayModules.Has(options, GatewayModules.FlightSql)) app.MapFlightEndpoint();
        app.MapGet("/", () => "DbDataGateway Arrow Flight SQL endpoint. Connect with a Flight SQL client.");
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
    private static void UseSharedHost(WebApplicationBuilder builder, GatewayOptions options, string[] args) =>
        builder.UseDbDataHost(host =>
        {
            host.Product = "dbdatagateway";
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
        (typeof(GatewayHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static void ConfigureServices(IServiceCollection services, GatewayOptions options)
    {
        if (options.Compression.Mode == CompressionMode.Arrow || options.DataSources.Values.Any(d => d.Compression?.Mode == CompressionMode.Arrow))
            FlightIpcCompression.EnsureSupported();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SessionStore>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<HomeCallers>();
        services.AddSingleton<IBasicCredentialValidator, HomeCredentialValidator>();
        services.AddSingleton<IWindowsLogon, NativeWindowsLogon>();
        services.AddSingleton<IBasicCredentialValidator, WindowsAccountValidator>();
        services.AddSingleton<IBasicCredentialValidator, PassThroughValidator>();
        services.AddSingleton<ChainedCredentialValidator>();
        services.AddSingleton<IJwtValidator, JwtBearerValidator>();
        services.AddSingleton<GatewayServing>();
        services.AddSingleton(sp =>
        {
            var current = sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Semantic");
            var loaded = SemanticModelsLoader.Load(current, ResolveDataDirectory(current), sp.GetRequiredService<SecretsRuntime>(), problem => log.LogWarning("{Problem}", problem));
            // the saved queries people keep for themselves (J27) are looked up in the state database
            loaded.UserQueries = sp.GetRequiredService<DbDataGateway.Server.State.SavedQueryStore>();
            // a record of every statement (SL23)
            loaded.Runs = sp.GetRequiredService<DbDataGateway.Server.State.SemanticRunStore>();
            // a changed server.yaml is applied to the models without a restart; the first pass only learns which names the file gives
            var directory = ResolveDataDirectory(current);
            var configured = SemanticModelsLoader.Reload(loaded, current, directory, new HashSet<string>(), _ => { });
            sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>().OnChange((changed, _) =>
                configured = SemanticModelsLoader.Reload(loaded, changed, ResolveDataDirectory(changed), configured, problem => log.LogWarning("{Problem}", problem), sp.GetRequiredService<PublishedSemanticModels>()));
            return loaded;
        });
        // the definitions published to the group, in its state (plan 20 O03): each instance follows them, so a publish through any of them, or by a publisher since stopped, is served by all
        services.AddSingleton(sp => ServingKinds.StoreOf(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        services.AddSingleton(sp => new PublishedSemanticModels(sp.GetRequiredService<DbDataKit.State.DefinitionStore>(), sp.GetRequiredService<SemanticModels>(),
            problem => sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Semantic").LogWarning("{Problem}", problem)));
        // the data sources a client publishes to a group (O04b), written where the configuration reads them, which is then read again
        services.AddSingleton(sp => new PublishedDataSources(sp.GetRequiredService<DbDataKit.State.DefinitionStore>(), sp.GetRequiredService<DbDataKit.State.StateDatabase>(),
            ResolveDataDirectory(sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue),
            () => ((IConfigurationRoot)sp.GetRequiredService<IConfiguration>()).Providers.OfType<GatewayConfigDirectoryProvider>().FirstOrDefault()?.ReloadNow(),
            problem => sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Definitions").LogWarning("{Problem}", problem)));
        // the master data models a DbDataStore server publishes to the group (O10)
        services.AddSingleton(sp => new PublishedEntityModels(sp.GetRequiredService<DbDataKit.State.DefinitionStore>(),
            problem => sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Definitions").LogWarning("{Problem}", problem)));
        // dashboards and reports (plan 32, DBR11b): published through the same state; the module is told of a version this instance could not use, so its health says so
        services.AddSingleton(sp => new GatewayViewsPublishing(sp.GetRequiredService<DbDataKit.State.DefinitionStore>(), sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        if (GatewayModules.Has(options, GatewayModules.Views)) services.AddSingleton<GatewayViews>();
        services.AddSingleton(sp =>
        {
            var views = GatewayModules.Has(sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue, GatewayModules.Views) ? sp.GetRequiredService<GatewayViews>() : null;
            return new DbDataKit.State.DefinitionFollower(sp.GetRequiredService<DbDataKit.State.DefinitionStore>(),
                [sp.GetRequiredService<PublishedDataSources>(), sp.GetRequiredService<PublishedSemanticModels>(), sp.GetRequiredService<PublishedEntityModels>(), .. views?.Publications.Subscribers ?? []],
                (kind, name, version, ex) =>
                {
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Definitions").LogWarning("The published {Kind} '{Name}' version {Version} is not served here: {Message}", kind, name, version, ex.Message);
                    views?.Module.OnProblem(kind, name, version, ex);
                },
                gate: GatewayVersionGate.Create(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        });
        services.AddHostedService<DbDataGateway.Server.State.DefinitionFollowerService>();
        services.AddSingleton<IServingServices>(sp => sp.GetRequiredService<GatewayServing>().WithSemantic(sp.GetRequiredService<SemanticModels>()));
        services.AddSingleton(sp =>
        {
            var current = sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>();
            return new FlightSqlModule(new FlightSqlOptions
            {
                Compression = () => current.CurrentValue.Compression,
                SourceCompression = name => current.CurrentValue.DataSources.TryGetValue(name, out var source) ? source.Compression : null,
                LogSql = () => current.CurrentValue.LogSql,
                ServerName = "DbDataGateway",
                ServerVersion = typeof(GatewayHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            }, sp.GetRequiredService<TimeProvider>());
        });
        if (GatewayModules.Has(options, GatewayModules.FlightSql))
        {
            services.AddHostedService<FlightSqlModuleService>();
            // the Flight module's own state first; when it is serving, the group's state database, which sessions and prepared statements need: without it Flight SQL still reads the hand-made data sources, so the line is degraded
            services.AddSingleton<IReadinessProbe>(sp => new ServingModuleReadinessProbe(sp.GetRequiredService<FlightSqlModule>(), () =>
                sp.GetRequiredService<FlightSqlModule>().Health() is { State: not ServingHealthState.Ok } own ? own : ServingModuleHealth.Of(true, false, sp.GetRequiredService<DbDataKit.State.StateDatabase>(), ServingHealthState.Degraded)));
        }
        // the identity home (plan 20, O06): a listener and a web application of its own, only when asked for
        if (GatewayModules.Has(options, GatewayModules.Identity))
        {
            services.AddSingleton<GatewayIdentityModule>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayIdentityModule>());
            services.AddSingleton<IReadinessProbe>(sp => new ServingModuleReadinessProbe(sp.GetRequiredService<GatewayIdentityModule>()));
        }
        if (GatewayModules.Has(options, GatewayModules.Apps))
        {
            services.AddSingleton<GatewayAppsModule>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayAppsModule>());
            services.AddSingleton<IReadinessProbe>(sp => new ServingModuleReadinessProbe(sp.GetRequiredService<GatewayAppsModule>()));
        }
        if (GatewayModules.Has(options, GatewayModules.Store))
        {
            services.AddSingleton<GatewayStoreModule>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayStoreModule>());
            services.AddSingleton<IReadinessProbe>(sp => new ServingModuleReadinessProbe(sp.GetRequiredService<GatewayStoreModule>()));
        }
        if (GatewayModules.Has(options, GatewayModules.Mcp))
        {
            services.AddSingleton(sp => new McpServices(sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>(), sp.GetRequiredService<DbDataKit.State.StateDatabase>(), sp.GetRequiredService<SecretsRuntime>(),
                sp.GetRequiredService<IServingServices>(), sp.GetRequiredService<SemanticModels>(), sp.GetRequiredService<PublishedEntityModels>(), sp.GetRequiredService<GatewayViewsPublishing>(), sp.GetRequiredService<HomeCallers>()));
            services.AddSingleton<GatewayMcpModule>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayMcpModule>());
            services.AddSingleton<IReadinessProbe>(sp => new ServingModuleReadinessProbe(sp.GetRequiredService<GatewayMcpModule>()));
        }
        if (GatewayModules.Has(options, GatewayModules.Views))
        {
            services.AddSingleton<GatewayViewsModule>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayViewsModule>());
            services.AddSingleton<IReadinessProbe, ViewsReadinessProbe>();
        }

        // The shared providers: one registry, one composer, one failure model per engine. With Gateway:LogDriverErrors the providers keep the
        // (redacted) driver text of failed connections on the failure, for the local log only.
        services.AddSingleton(sp =>
        {
            var redactor = sp.GetRequiredService<IRedactor>();
            bool raw = sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue.LogDriverErrors;
            return DbDataGateway.Server.Data.GatewaySources.Providers(redactor, raw);
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

    private static void ConfigureKestrel(WebApplicationBuilder builder, GatewayOptions options)
    {
        string dataDirectory = ResolveDataDirectory(options);
        var holder = new TlsCertificateHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(sp => DbDataGateway.Server.State.GatewayState.Open(dataDirectory, options.State.ToSettings(),
            text => sp.GetRequiredService<SecretsRuntime>().ResolveConnectionString("Gateway:State:Connection", text, "state")));
        builder.Services.AddSingleton(sp => new DbDataKit.State.NotificationFeedStore(sp.GetRequiredService<DbDataKit.State.StateDatabase>(), DbDataGateway.Server.State.GatewayState.ServiceId));
        builder.Services.AddSingleton<DbDataKit.Monitoring.INotificationFeed>(sp => sp.GetRequiredService<DbDataKit.State.NotificationFeedStore>());
        builder.Services.AddSingleton(sp => new DbDataGateway.Server.State.SavedQueryStore(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        builder.Services.AddSingleton(sp => new DbDataGateway.Server.State.SemanticRunStore(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        // what statements read, as a summary per table for the lineage (plan 18 G08): counted in memory, written every few seconds
        builder.Services.AddSingleton(sp => new DbDataGateway.Server.State.TableReadStore(sp.GetRequiredService<DbDataKit.State.StateDatabase>()));
        builder.Services.AddSingleton(sp => new DbDataGateway.Server.State.TableReadRecorder(sp.GetRequiredService<DbDataGateway.Server.State.TableReadStore>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService<DbDataGateway.Server.State.TableReadFlushService>();
        builder.Services.AddSingleton<GatewayEvents>();
        builder.Services.AddSingleton<DbDataGateway.Server.State.GroupSessionKey>();
        // the secrets a group's clients keep on it (plan 20 O04b); the state is looked up when a client's secret is first asked for, never to open the state itself
        builder.Services.AddSingleton(sp => new GroupSecretStore(() => sp.GetRequiredService<DbDataKit.State.StateDatabase>(), () => sp.GetRequiredService<DbDataGateway.Server.State.GroupSessionKey>()));
        builder.Services.AddHostedService<DbDataGateway.Server.State.GatewayStateStartup>();
        // this instance's row in the state; a second instance on a SQLite state is refused (plan 20 O02)
        builder.Services.AddSingleton<DbDataGateway.Server.State.ServingInstanceLease>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DbDataGateway.Server.State.ServingInstanceLease>());
        builder.Services.AddSingleton<IReadinessProbe>(new CertificateReadinessProbe(() => holder.Current, CertificateProvider.RenewBeforeDays));
        // a group of self-signed instances presents one certificate, kept in its state (plan 20 O04)
        builder.Services.AddSingleton<GroupCertificate>();
        builder.Services.AddHostedService(sp => new CertificateRotationService(
            holder, options, dataDirectory, sp.GetRequiredService<ILogger<CertificateRotationService>>(), group: sp.GetRequiredService<GroupCertificate>()));

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Flight streams large result sets; do not cap or time out the response body.
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);

            // the certificate is loaded here, not by the Flight listener: the management and identity listeners need it when the instance serves no Flight SQL
            if (options.Tls.Mode != TlsMode.None && holder.Current is null)
                holder.Set(CertificateProvider.Load(options, dataDirectory,
                    kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tls"), secrets: kestrel.ApplicationServices.GetRequiredService<SecretsRuntime>(),
                    group: kestrel.ApplicationServices.GetRequiredService<GroupCertificate>()));
            var address = IPAddress.Parse(options.Listen.Address);
            if (GatewayModules.Has(options, GatewayModules.FlightSql)) kestrel.Listen(address, options.Listen.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (options.Tls.Mode == TlsMode.None) return;
                listen.UseHttps(https =>
                {
                    // Looked up per handshake so a rotated certificate is served without a restart.
                    https.ServerCertificateSelector = (_, _) => holder.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
            GatewayManagementHost.Listen(kestrel, options, holder);
        });
    }

    public static string ResolveDataDirectory(GatewayOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DataDirectory)) return Path.GetFullPath(options.DataDirectory);
        // DBDATAKIT_DATA_GATEWAY, or the folder gateway under DBDATAKIT_DATA, else the machine folder (%ProgramData%\DbDataKit\gateway, /var/lib/dbdatakit/gateway)
        return Path.GetFullPath(DbDataKit.State.SuiteEnvironment.ResolveServiceFolder("gateway"));
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

/// <summary>Starts the Flight SQL module on Gateway's services when the host starts, and drains it and stops it (disposing the results nobody fetched) when the host stops.</summary>
public sealed class FlightSqlModuleService(FlightSqlModule module, IServingServices services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => module.StartAsync(services, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await module.DrainAsync(cancellationToken);
        await module.StopAsync(cancellationToken);
    }
}
