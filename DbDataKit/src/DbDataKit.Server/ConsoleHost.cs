using DbDataKit.State.Api;
using DbDataKit.Identity;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using DbDataKit.Hosting;
using Microsoft.AspNetCore.DataProtection;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Auth.Controllers;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>
/// The dbdatakit console host (plan 13, U07): Kestrel over HTTPS serving the console's web app and the API behind it, with the shared sign-in (<see cref="SessionAuthRegistration"/>): passkeys, sessions, invitations and users.
/// The console is the install's identity home by default, so the users live in its own state database; the first administrator is made by redeeming the invitation it writes to <c>FIRST-RUN.txt</c> when it first starts.
/// </summary>
public static class ConsoleHost
{
    public static readonly AuthProduct Auth = new("DbDataKit", "dbdatakit user invite", "dbdatakit setup link");

    public static string Version { get; } =
        (typeof(ConsoleHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    /// <param name="webRoot">The built web app; the folder <c>wwwroot</c> beside the program when null. Without it the host serves the API and a page that says the app is not installed.</param>
    public static WebApplication Build(ConsoleSettings settings, X509Certificate2 certificate, Action<string> log, string? webRoot = null, Action<IServiceCollection>? configure = null)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The console listens on HTTPS only.", nameof(settings));
        webRoot ??= Path.Combine(AppContext.BaseDirectory, "wwwroot");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LineLoggerProvider(log));
        // service and systemd modes, draining on stop, /health and /version: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = ConsoleSettings.Product;
            host.ServiceName = ConsoleService.RegistrationName;
            host.Version = Version;
        });
        if (settings.AnnounceListen is { } announce && (announce.Scheme != Uri.UriSchemeHttp || announce.Port == settings.Listen.Port))
            throw new ArgumentException("The announce address is a plain http address on a port of its own.", nameof(settings));
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(AddressOf(settings.Listen), settings.Listen.Port, listen => listen.UseHttps(certificate));
            if (settings.AnnounceListen is { } announce) o.Listen(AddressOf(announce), announce.Port);
        });

        // A passkey belongs to a host name; unless the configuration says otherwise it is the one people open the console at.
        var relyingParty = $"{Auth.Section}:Passkeys:RelyingPartyId";
        if (string.IsNullOrWhiteSpace(builder.Configuration[relyingParty]))
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [relyingParty] = settings.Public.Host });

        // The framework's key ring is kept with the rest of what the console owns, so a container that is replaced does not lose it and the log does not warn about it.
        builder.Services.AddDataProtection().SetApplicationName("dbdatakit-console").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.Directory, "dataprotection")));

        builder.Services.AddControllers()
            // controller discovery starts from the entry assembly, which is not always this one (the tests): name the one that holds the sign-in controllers
            .AddApplicationPart(typeof(AuthController).Assembly)
            .AddApplicationPart(typeof(ConsoleHost).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var runnerSettings = settings.RunnerOrFile();
        builder.Services.AddSingleton(_ => ConsoleState.Open(settings.Directory, settings.State, runner: runnerSettings));
        if (runnerSettings.Enabled)
        {
            // the runner embedded in the console (plan 28a R20): off unless settings.yaml says runner.enabled; stopping the console stops it, and stopping waits for its tasks to take their cancellation
            builder.Services.AddSingleton(runnerSettings);
            builder.Services.AddSingleton(sp => new ConsoleRunner(runnerSettings, settings, sp.GetRequiredService<StateDatabase>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataKit.Server.Runner")));
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<ConsoleRunner>());
            builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromTicks(Math.Max(h.ShutdownTimeout.Ticks, (runnerSettings.CancellationGrace + TimeSpan.FromSeconds(30)).Ticks)));
        }
        builder.Services.AddSingleton(sp => new NotificationFeedStore(sp.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId));
        builder.Services.AddSingleton(sp => new AuditLog(sp.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId));
        builder.Services.AddSingleton(new ConsoleActivitySource(settings.State is { IsSqlite: false }));
        builder.Services.AddSingleton<DbDataKit.Monitoring.INotificationFeed>(sp => sp.GetRequiredService<NotificationFeedStore>());
        // how long a sign-in lasts: the session: section of settings.yaml (plan 13a U22); a file that sets it out of range stops the console from starting, with a message naming the setting
        builder.Services.AddSingleton(SessionSettingsFile.Load(settings.SettingsPath));
        // how long the setup link of a new install lives: the setup: section (plan 23a FQ19, QF5)
        builder.Services.AddSingleton(SetupLinkSettingsFile.Load(settings.SettingsPath));
        builder.Services.AddSessionAuth(Auth,
            sp => sp.GetRequiredService<StateDatabase>(),
            sp => PasskeyOptions.FromConfiguration(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), Auth, settings.Public.GetLeftPart(UriPartial.Authority), [], []),
            _ => settings.Directory);
        // the first-run link is a whole address: the one people open the console at (plan 23a FQ19)
        builder.Services.AddSingleton(new BootstrapInviteLocation(settings.Directory, settings.Public.GetLeftPart(UriPartial.Authority)));
        // the servers this console manages, and the key it signs for them with
        builder.Services.AddSingleton(_ => ConsoleSecrets.Open(settings.Directory));
        builder.Services.AddSingleton(sp => new ConsoleKeyStore(sp.GetRequiredService<EncryptedFileSecretStore>(), sp.GetRequiredService<EncryptedFileSecretStore>()));
        builder.Services.AddSingleton(_ => new InstanceRegistry(settings.InstancesPath));
        builder.Services.AddSingleton<ConsoleInstances>();
        builder.Services.AddSingleton<ConsoleUpgrades>(sp => new ConsoleUpgrades(settings, sp.GetRequiredService<ConsoleInstances>()));
        // state databases as connections of kind state, and the move of a server's state onto one (plan 29b, C06)
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(sp => new ConsoleStateConnections(settings.Directory, sp.GetRequiredService<EncryptedFileSecretStore>()));
        builder.Services.AddSingleton(_ => new PendingInstances(settings.PendingPath));
        builder.Services.AddSingleton(RelayTimeouts.Default);
        builder.Services.AddSingleton<ConsoleRelay>();
        builder.Services.AddSingleton<ConsoleLineage>();
        // the agents the console hosts and the knowledge its screens read (plan 27a AG14 to AG17, AG29 to AG32, KN28, KN36 to KN38); the model an agent's slot is bound to comes from IAgentModels (none until model providers are connections, AG05)
        // the model providers (connections of kind model-provider) and the slots of models/_slots.yml (plan 27a AG05): what an agent's slot is bound to; a test gives its own IAgentModels and ModelHttp
        builder.Services.TryAddSingleton<ModelHttp>();
        // the connections of outside systems (plan 27a IX01): connections/ in the console's directory
        builder.Services.AddSingleton(sp => new ConsoleConnections(settings, sp.GetRequiredService<EncryptedFileSecretStore>()));
        builder.Services.AddSingleton<ConsoleModels>(sp => new ConsoleModels(settings, sp.GetRequiredService<ConsoleConnections>(), sp.GetRequiredService<ModelHttp>()));
        builder.Services.TryAddSingleton<IAgentModels>(sp => sp.GetRequiredService<ConsoleModels>());
        builder.Services.TryAddSingleton<IAgentToolSource, ConsoleToolSource>();
        builder.Services.AddSingleton<ConsoleKnowledge>();
        builder.Services.AddSingleton<ConsoleAgents>();
        // an outside agent (`ui mcp --report-to`) signs in as the person with a token made on the Agents page, as Basic credentials, and reaches the outside endpoints only
        builder.Services.AddSingleton<IPasswordCredentials>(sp => new AgentTokenCredentials(sp.GetRequiredService<ConsoleAgents>().Store, sp.GetRequiredService<IUserAccounts>()));
        builder.Services.AddSingleton<ConsoleQuality>();
        builder.Services.AddSingleton(sp => new SetupItems(sp.GetRequiredService<StateDatabase>()));
        if (settings.Home is { } home)
        {
            // No users of its own: the first-run invitation would make one, so it is not made, and sign-in is the home's.
            foreach (var d in builder.Services.Where(d => d.ImplementationType == typeof(BootstrapInvite)).ToList()) builder.Services.Remove(d);
            builder.Services.AddSingleton(new OpenIdClient(
                new OpenIdClientOptions(home.Authority.AbsoluteUri, home.ClientId, home.ClientSecret, new Uri(settings.Public, ConsoleFederation.CallbackPath).AbsoluteUri, new Uri(settings.Public, "/").AbsoluteUri),
                // a home that does not answer must not hold a request for the default 100 seconds
                new HttpClient(HomeTrust.Handler(home.TrustedCertificate)) { Timeout = TimeSpan.FromSeconds(10) }));
            // the sign-in screen offers the home (plan 20 O06)
            builder.Services.AddSingleton(SignInMethod.IdentityHome);
            builder.Services.AddSingleton(sp =>
            {
                // the home's tokens are kept protected by the framework's key ring, which is kept with the rest of what the console owns
                var protector = sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("dbdatakit-console.federated-session");
                var policy = FederationPolicy.With(protector.Protect, protector.Unprotect) with
                {
                    RefreshInterval = home.RefreshInterval ?? FederationPolicy.DefaultRefreshInterval,
                    Grace = home.Grace ?? FederationPolicy.DefaultGrace,
                };
                return new FederatedUsers(sp.GetRequiredService<StateDatabase>(), sp.GetRequiredService<IUserAccounts>(), sp.GetRequiredService<ISessionStore>(), home.AppId, policy, sp.GetService<TimeProvider>());
            });
            builder.Services.AddSingleton<ISignOutHandler, FederationSignOut>();
        }
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        if (app.Services.GetRequiredService<PasskeyOptions>().Problem() is { } problem)
            log($"Passkeys are misconfigured and will not work: {problem}");

        // The private port answers announcements and nothing else; the announcement path answers on no other port
        var announcePort = settings.AnnounceListen?.Port;
        app.Use(async (context, next) =>
        {
            var onAnnouncePort = announcePort is { } port && context.Connection.LocalPort == port;
            var isAnnouncement = context.Request.Path.Equals(ConsoleAnnouncer.Path, StringComparison.Ordinal);
            if (!onAnnouncePort && !isAnnouncement) { await next(); return; }
            if (!onAnnouncePort || !isAnnouncement || !HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 4096;
            Announcement? announcement;
            try { announcement = await context.Request.ReadFromJsonAsync<Announcement>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), context.RequestAborted); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException) { announcement = null; }
            var resolves = announcement?.Host is { } host && PendingInstances.HostPattern().IsMatch(host) && await ResolvesAsync(host);
            var refusal = announcement is null ? "the body is not an announcement" : context.RequestServices.GetRequiredService<PendingInstances>().Announce(announcement, context.Connection.RemoteIpAddress, resolves);
            if (refusal is null) { context.Response.StatusCode = StatusCodes.Status204NoContent; return; }
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = refusal }, context.RequestAborted);
        });

        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();

        // The web app is code, public on GitHub; the data is behind /api, which the fallback policy closes. Served before authentication so the sign-in screen can be.
        if (Directory.Exists(webRoot))
        {
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            // The docs of the modules are Markdown files the viewer fetches (plan 13a, U11); the static file provider has no type for them, and an unknown type is not served.
            var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            types.Mappings[".md"] = "text/markdown; charset=utf-8";
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });
        }

        app.UseWebSockets();
        // A session that came from the identity home asks the home again for the person's roles when the last answer is old (plan 13a U17); before authentication, so a session the home ended is refused on this very request.
        if (settings.Home is not null) app.UseFederationRefresh();
        app.UseAuthentication();
        // a request signed in with an outside agent's token is not a person at a screen: it reaches the outside endpoints and nothing else
        app.Use(async (context, next) =>
        {
            if (context.User.HasClaim(PasswordCredentials.MethodClaim, PasswordCredentials.Method) && !AgentTokenCredentials.Allows(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "A token made for an outside agent reaches only what an outside agent needs. Sign in to use the rest of the console." });
                return;
            }
            await next();
        });
        // what the console records (UI05): sign-ins, invitations, servers, its own changes and the writes relayed to a server; after authentication, so the person is known
        app.UseConsoleAuditTrail(Auth);
        // maintenance mode (plan 29b, C05): an administrator's session still works, so does the sign-in; the web app's own files are served before this and stay up
        var maintenance = new DbDataKit.State.Api.MaintenanceGate();
        app.UseMaintenance(maintenance, context => context.User.IsInRole(nameof(DbDataKit.State.UserRole.Admin)), ["/api/auth"]);
        app.UseAuthorization();
        app.MapControllers();
        MapState(app, settings, maintenance);
        // the certificate this console presents: days left and subject, for its own Monitoring row (UI10); the shape of every server's `GET /api/v1/certificate`
        app.MapGet(CertificateStatusEndpoint.Path, () => Results.Json(CertificateStatusEndpoint.Describe(certificate, "self-signed", TimeProvider.System.GetUtcNow()))).RequireAuthorization(DbDataKit.Web.Auth.Policies.Viewer);
        app.MapEmbeddedRunner();
        app.MapRelay();
        if (settings.Home is not null) app.MapFederation();

        // Deep links are the web app's routes, not this server's: /users has to return the app. An unmatched /api route is a 404, never the app.
        RequestDelegate webApp = async context =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var index = Path.Combine(webRoot, "index.html");
            if (File.Exists(index))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(index);
                return;
            }
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The dbdatakit console is running, but its web app is not installed here (no wwwroot).");
        };
        app.MapFallback(webApp).AllowAnonymous();
        // The default fallback leaves out a path whose last segment has an extension, as a missing file's address does. A server's own names are routes too, and one with a dot in it (a Sync table mapping `dbo.Orders`)
        // is a deep link under /servers/<id>/, so those paths are matched on their own: nothing under /servers/ is a file, which the static files above would have served before the routes saw it.
        app.MapFallback("/servers/{*path}", webApp).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// <c>GET /api/v1/state</c> and the maintenance mode of this console, for its signed-in people: a viewer reads, an administrator changes (the role is checked in the call so a refusal is audited). The audit is the state's own.
    /// </summary>
    private static void MapState(WebApplication app, ConsoleSettings settings, DbDataKit.State.Api.IMaintenanceGate maintenance)
    {
        var api = new DbDataKit.State.Api.StateManagement(new DbDataKit.State.Api.StateManagementOptions(
            ConsoleSettings.Product, "dbdatakit", "the console",
            () =>
            {
                var resolved = ConsoleState.Resolve(settings.Directory);
                return new DbDataKit.State.Cli.StateTarget(settings.State ?? resolved.Settings, settings.StatePath,
                    () => new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(settings.Directory), new DbDataKit.Secrets.EnvSecretProvider()]),
                    ConsoleState.SettingsFileIn(settings.Directory), settings.StateSources ?? resolved.Sources);
            },
            ConsoleState.ModulesFor(settings.RunnerOrFile()), ConsoleState.ScopeFor(settings.RunnerOrFile()), maintenance, new DbDataKit.State.Api.StateAuditSink(app.Services.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId),
            Version: Version));
        app.MapState(api, context =>
        {
            var user = context.User;
            var id = user.FindFirst(Auth.UserIdClaim)?.Value ?? user.Identity?.Name ?? "unknown";
            return DbDataKit.State.Api.StateCaller.FromSession(id, user.Identity?.Name, role =>
                role == DbDataKit.Web.Trust.ServerRole.Viewer ? user.IsInRole(nameof(DbDataKit.State.UserRole.Viewer)) || user.IsInRole(nameof(DbDataKit.State.UserRole.Admin))
                : user.IsInRole(nameof(DbDataKit.State.UserRole.Admin)));
        }).RequireAuthorization(DbDataKit.Web.Auth.Policies.Viewer);
    }

    /// <summary>Whether the console can look the name up, which a name on the network the servers share with it can.</summary>
    private static async Task<bool> ResolvesAsync(string host)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return (await Dns.GetHostAddressesAsync(host, limit.Token)).Length > 0;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException) { return false; }
    }

    private static IPAddress AddressOf(Uri listen) =>
        listen.Host == "localhost" ? IPAddress.Loopback
        : listen.HostNameType == UriHostNameType.Dns ? IPAddress.Any
        : IPAddress.Parse(listen.Host);

    /// <summary>Sends the host's log lines to one callback (the command's error stream), with the level.</summary>
    private sealed class LineLoggerProvider(Action<string> write) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Line(write);
        public void Dispose() { }

        private sealed class Line(Action<string> write) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                write($"{logLevel.ToString().ToLowerInvariant()}: {formatter(state, exception)}");
        }
    }
}
