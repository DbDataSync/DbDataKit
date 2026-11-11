using DbDataGateway.Server.Secrets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Hosting.Local;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Tls;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The Gateway's management API as a listener (plan 13, P03): HTTPS on a port of its own, never the Flight port. A connection to it is tagged when it is accepted, and a middleware keeps the two apart whatever the port
/// numbers are: a management path on the Flight listener is a 404, and on the management listener only the management paths and the shared <c>/health</c> and <c>/version</c> answer. The calls themselves are
/// <see cref="GatewayManagement"/>'s. Pairing, the token check and the roles are the shared ones every product has.
/// </summary>
public static class GatewayManagementHost
{
    /// <summary>The connection item that says a connection came in on the management listener.</summary>
    public const string ListenerItem = "dbdata.listener.management";

    /// <summary>Adds the management listener to Kestrel (after the Flight one). Only when the options ask for it and TLS is on.</summary>
    public static void Listen(KestrelServerOptions kestrel, GatewayOptions options, TlsCertificateHolder holder)
    {
        if (!options.Management.Enabled || options.Tls.Mode == TlsMode.None) return;
        kestrel.Listen(IPAddress.Parse(options.Management.Address), options.Management.Port, listen =>
        {
            listen.Use(next => async connection =>
            {
                connection.Items[ListenerItem] = true;
                await next(connection);
            });
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(https =>
            {
                https.ServerCertificateSelector = (_, _) => holder.Current;
                https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
            });
        });
    }

    private static bool OnManagementListener(HttpContext context) => context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(ListenerItem) == true;

    /// <summary>Keeps management paths off the Flight listener and everything else off the management one. Runs first, before the shared pipeline answers anything.</summary>
    public static void Guard(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var management = path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/management/", StringComparison.Ordinal);
            var shared = path.StartsWith("/health", StringComparison.Ordinal) || path == "/version";
            var onManagement = OnManagementListener(context);
            if ((management && !onManagement) || (onManagement && !management && !shared))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }

    /// <summary>Maps pairing, the token check and the calls, and starts the local channel if asked. Called once, with the options the server started with.</summary>
    public static GatewayManagement? Map(WebApplication app, GatewayOptions options, string dataDirectory, TlsCertificateHolder holder, IConfigurationRoot configuration)
    {
        if (!options.Management.Enabled) return null;

        var serverYaml = GatewayConfigFile.PathIn(dataDirectory);
        // the pairing codes wait in the data directory, not in the configuration folder a person keeps under version control; a group keeps them, its paired clients and its instance id in its state (plan 20 O04)
        var state = app.Services.GetRequiredService<DbDataKit.State.StateDatabase>();
        var files = new PairingFiles(serverYaml, Path.Combine(dataDirectory, "pairing-codes.json"), DbDataGateway.Server.State.GatewayState.IsGroup(state) ? state : null);
        var instanceId = files.InstanceId("gateway", "dbdatagateway server");
        if (files.Group is not null && GroupIdentity.Differs(instanceId, serverYaml) is { } own)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Management").LogWarning(
                "{File} names this instance {Own}, and it serves as its group, {Group}: a client paired with {Own} alone pairs again with the group.", serverYaml, own, instanceId, own);
        var clients = files.Clients();
        var configDirectory = GatewayConfigFile.DirectoryIn(dataDirectory);
        var provider = configuration.Providers.OfType<GatewayConfigDirectoryProvider>().Single();
        var git = new GitCommitService(configDirectory);
        // The history starts with the configuration as it was found, so the first change made through the API reads as a change to something.
        if (git.GetHistory("", 1).Count == 0 && Directory.EnumerateFiles(configDirectory, "*.yaml", SearchOption.AllDirectories).ToList() is { Count: > 0 } found)
            git.CommitChanges(found, "Configuration as found", new GitAuthor("dbdatagateway", "dbdatagateway@local.invalid"));
        // the data sources need the management object's validation, and it needs them to route to: made in two steps
        GatewayDataSources? dataSources = null;
        // while in maintenance the instance's own row in the state stops being refreshed, so a move's comparison against the source sees no change in it (plan 29b C06)
        var maintenance = new MaintenanceGate(onEnter: _ => app.Services.GetRequiredService<DbDataGateway.Server.State.ServingInstanceLease>().Pause(), onLeave: () => app.Services.GetRequiredService<DbDataGateway.Server.State.ServingInstanceLease>().Resume());
        var management = new GatewayManagement(configuration, configDirectory, instanceId, GatewayHost.Version);
        // one for every kind of change, in the state every instance of a group shares (plan 20 O02); the file it used to be is read in once
        var audit = new DbDataGateway.Server.State.StateAuditLog(app.Services.GetRequiredService<DbDataKit.State.StateDatabase>());
        dataSources = new GatewayDataSources(configDirectory, management, provider.ReloadNow, () => app.Services.GetRequiredService<DataSourceCatalog>(), audit, git, findClient: clients.Find, published: app.Services.GetRequiredService<Serving.PublishedDataSources>());
        var identities = new GatewayIdentities(configDirectory, management, provider.ReloadNow, () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, audit, git);
        var activity = new GatewayActivity(app.Services.GetRequiredService<SessionStore>(), app.Services.GetRequiredService<ActiveQueries>(), app.Services.GetRequiredService<QueryLimits>(),
            app.Services.GetRequiredService<LoginThrottle>(), () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, audit,
            app.Services.GetRequiredService<DbDataKit.Monitoring.INotificationFeed>(), app.Services.GetRequiredService<RecentFailures>(), instanceId, GatewayHost.Version);
        var certificate = new GatewayCertificate(holder, () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, dataDirectory, app.Services.GetRequiredService<RecentFailures>(), audit, group: app.Services.GetRequiredService<GroupCertificate>());
        // the state endpoints and the maintenance mode (plan 29b, C05). The Gateway has no loop that claims work (its serving is requests), so the gate has no hook: refusing requests is all it stops.
        var stateApi = new StateManagement(new StateManagementOptions(
            "dbdatagateway", "dbdatagateway", "the gateway",
            () =>
            {
                try
                {
                    return new DbDataKit.State.Cli.StateTarget(options.State.ToSettings(), DbDataGateway.Server.State.GatewayState.PathIn(dataDirectory),
                        () => new DbDataKit.Secrets.SecretResolverChain(SecretsRuntime.CreateProviders(options, SecretsRuntime.OpenStore(options))),
                        GatewayConfigFile.PathIn(dataDirectory), GatewayStateConfig.Resolve(configuration).Sources);
                }
                catch (GatewayConfigException ex) { throw new InvalidOperationException(ex.Message, ex); }
            },
            DbDataGateway.Server.State.GatewayState.Modules, DbDataGateway.Server.State.GatewayState.ScopeOf(), maintenance,
            new StateAuditSink(app.Services.GetRequiredService<DbDataKit.State.StateDatabase>(), DbDataGateway.Server.State.GatewayState.ServiceId),
            instanceId, GatewayHost.Version, app.Services.GetRequiredService<DbDataGateway.Server.State.ServingInstanceLease>(), Move: true));
        management = new GatewayManagement(configuration, configDirectory, instanceId, GatewayHost.Version, dataSources, identities, activity, certificate, git,
            new GatewaySecrets(app.Services.GetRequiredService<GroupSecretStore>() is { Enabled: true } group ? group : SecretsRuntime.OpenStore(options), audit, references: dataSources.References), files.Codes(),
            new GatewaySemanticModels(app.Services.GetRequiredService<Serving.PublishedSemanticModels>(), app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(),
                () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, configDirectory, () => app.Services.GetRequiredService<DataSourceCatalog>(), audit),
            pairedClients: clients.All,
            identityAdmin: options.Identity.Enabled
                ? new DbDataKit.Identity.IdentityAdminApi(app.Services.GetRequiredService<DbDataKit.State.StateDatabase>(), GatewayIdentityModule.PublicUrlOf(options).AbsoluteUri, new DbDataKit.Identity.AccessTokenPolicy(options.Identity.AccessTokenMaxDays))
                : null,
            stateApi: stateApi,
            views: new GatewayViewsManagement(app.Services.GetRequiredService<Serving.GatewayViewsPublishing>(), GatewayModules.Has(options, GatewayModules.Views) ? app.Services.GetRequiredService<Serving.GatewayViews>() : null, audit),
            userQueries: new GatewayUserQueries(app.Services.GetRequiredService<DbDataGateway.Server.State.SavedQueryStore>(), app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(),
                GatewayModules.Has(options, GatewayModules.Views) ? app.Services.GetRequiredService<Serving.GatewayViews>() : null, audit),
            audit: audit,
            serverSettings: new GatewayServerSettings(configDirectory, management, provider.ReloadNow, audit, git),
            entityModels: new GatewayEntityModels(app.Services.GetRequiredService<Serving.PublishedEntityModels>(),
                () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue,
                dataSource =>
                {
                    // who made a data source: its file in this instance's configuration, else the group's published one (O04b)
                    var file = Path.Combine(configDirectory, "connections", dataSource + ".yaml");
                    var text = File.Exists(file) ? File.ReadAllText(file) : app.Services.GetRequiredService<Serving.PublishedDataSources>().Text(dataSource);
                    return text is null ? null : DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, dataSource + ".yaml").Definition?.ManagedBy;
                }, audit),
            apps: new GatewayApps(new DbDataKit.State.DefinitionStore(state), () => HomeApprovers.Open(state, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue), audit,
                appsAddress: () => { var current = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue; return GatewayModules.Has(current, GatewayModules.Apps) ? GatewayAppsModule.PublicUrlOf(current) : null; },
                instanceSaved: name => AppClient(app, state, name, remove: false), instanceRemoved: name => AppClient(app, state, name, remove: true)));
        if (audit.ImportFile(dataDirectory) is > 0 and var imported)
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Management").LogInformation("Read {Count} entries of {File} into the state's audit; the file is now {File}.imported.", imported, DbDataGateway.Server.State.StateAuditLog.LegacyFileName, DbDataGateway.Server.State.StateAuditLog.LegacyFileName);
        // a token one instance of a group accepted is refused by every other (plan 20 O02b): the used ids are in the state they share
        var used = new DbDataKit.State.IdentityOneTimeValues(app.Services.GetRequiredService<DbDataKit.State.StateDatabase>());
        var validator = new ConsoleTokenValidator(instanceId, clients, new StateReplayCache(used, "management:" + instanceId));
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Management");

        app.UseManagementApiVersion();
        app.UseManagementAuthentication(validator, refused => log.LogWarning("A management call was refused ({Failure}): {Detail}", refused.Failure, refused.Detail));
        // after the token check, so an administrator's call is known: everything else is refused with 503 while the server is in maintenance
        app.UseMaintenance(maintenance, context => context.ManagementTokenOrNull() is { } token && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed);
        app.MapPairing(new PairingService(instanceId,
            () => holder.Current is { } certificate ? PairingProof.CertificateFingerprint(certificate) : throw new InvalidOperationException("This server has no certificate to pair against."),
            clients, files.Codes()));
        app.AnnounceToConsole("dbdatagateway", instanceId, options.Management.Port, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new StateReplayCache(used, "key-rotation:" + instanceId)));

        // the server upgrades itself when the console says so (plan 24a UC09); its own route wins over the catch-all below
        DbDataKit.Upgrade.UpgradeSetup.MapUpgrade(app, "dbdatagateway", GatewayHost.Version, "DbDataGateway", options.Management.Port, audit, () => [.. clients.All().Select(c => new DbDataKit.Upgrade.PeerGeneration(c.Name, c.Generation))]);
        app.MapMethods($"{GatewayManagement.Prefix}/{{**rest}}", ["GET", "POST", "PUT", "DELETE"], async (HttpContext context) =>
        {
            var caller = new ManagementCaller(context.ManagementToken());
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = context.Request.Method is "POST" or "PUT" ? await reader.ReadToEndAsync(context.RequestAborted) : null;
            var result = await management.HandleAsync(context.Request.Method, context.Request.Path.Value! + context.Request.QueryString, body, caller, context.RequestAborted);
            return result.Status == StatusCodes.Status204NoContent ? Results.NoContent() : Results.Json(result.Body, statusCode: result.Status);
        });

        if (options.Management.LocalChannel)
        {
            var channel = new LocalChannelServer(new LocalChannelOptions("dbdatagateway") { Directory = string.IsNullOrWhiteSpace(options.Management.LocalChannelDirectory) ? null : options.Management.LocalChannelDirectory }, (request, ct) => HandleLocalAsync(management, request, ct));
            channel.Start();
            app.Lifetime.ApplicationStopping.Register(() => channel.DisposeAsync().AsTask().GetAwaiter().GetResult());
            log.LogInformation("The local channel is {Endpoint}.", channel.Endpoint);
        }
        return management;
    }

    /// <summary>A local-channel message is <c>{"method":"GET","path":"/api/v1/config","body":"..."}</c>; the answer is <c>{"status":200,"body":{...}}</c>. The operating system decided who could connect, so the caller holds every role.</summary>
    // the identity home's client of an app instance follows the instance (plan 26 AP19); a failure here never fails the save: the apps module makes the missing ones when it starts
    private static void AppClient(WebApplication app, DbDataKit.State.StateDatabase state, string instance, bool remove)
    {
        try
        {
            var current = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue;
            if (!GatewayModules.Has(current, GatewayModules.Apps)) return;
            var clients = new DbDataKit.Identity.OidcClients(state);
            if (remove) Serving.AppInstanceClients.Remove(clients, instance);
            else Serving.AppInstanceClients.Ensure(clients, instance, Serving.AppInstanceClients.CallbackOf(GatewayAppsModule.PublicUrlOf(current)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataGateway.Management").LogWarning(ex, "The OpenID client of the app instance {Instance} could not be {Change}.", instance, remove ? "removed" : "registered");
        }
    }

    internal static async Task<byte[]> HandleLocalAsync(GatewayManagement management, byte[] request, CancellationToken cancellationToken)
    {
        try
        {
            var message = JsonNode.Parse(request)?.AsObject();
            var result = await management.HandleAsync((string?)message?["method"] ?? "GET", (string?)message?["path"] ?? "", (string?)message?["body"], ManagementCaller.Local(Environment.UserName), cancellationToken);
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = result.Status, ["body"] = result.Body });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = 400, ["body"] = new JsonObject { ["error"] = "The message is not a JSON object with a method and a path." } });
        }
    }
}
