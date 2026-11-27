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

    /// <summary>
    /// Joins the console a <c>join-request.yaml</c> in the data directory names, before the server starts: a listener of its own on the management API's
    /// addresses, with the certificate the management API will present, answers the console's callback and nothing else, and stops once the join is made.
    /// The join reports the instance's listen settings, and the key of its group when it holds one; it keeps the group's key the console hands over and, for a
    /// further instance of a group, writes the group's ports to <c>config/server.yaml</c> as its own. The server is then built again on what the join kept, so
    /// its listeners start on the group's ports and an instance of a group serves the group's certificate.
    /// Design: docs/install.md#one-startup-sequence
    /// </summary>
    /// <returns>True when a join request was there and the server must be built again; false when there was none.</returns>
    public static async Task<bool> JoinAsync(WebApplication app, string dataDirectory, CancellationToken ct)
    {
        if (!File.Exists(ServerJoin.RequestPath(dataDirectory))) return false;
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue;
        var target = app.Services.GetRequiredService<GatewayJoinTarget>();
        if (target.State is null || target.Clients is null || target.InstanceId is null)
            throw new InvalidOperationException($"The join request {ServerJoin.RequestPath(dataDirectory)} cannot be used: the management API is off here (`management.enabled: false`, or `tls.mode` None), "
                + "so the console cannot call this gateway back. Turn the management API on and start the gateway again.");
        var addresses = GatewayListeners.Parsed(options.Management.Listen).ToList();
        if (addresses.Count == 0) throw new InvalidOperationException("The management API has no address to listen on.");
        var certificate = CertificateProvider.Load(options, dataDirectory, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tls"),
                secrets: app.Services.GetRequiredService<SecretsRuntime>(), group: app.Services.GetRequiredService<GroupCertificate>())
            ?? throw new InvalidOperationException("This gateway has no certificate for the console to pin.");
        var sessionKey = app.Services.GetRequiredService<DbDataGateway.Server.State.GroupSessionKey>();
        var pending = new PendingJoins();
        var callback = CallbackListener(addresses, certificate, pending);
        try
        {
            // a port another program holds fails here, as it would fail the server's own listener
            await callback.StartAsync(ct);
            await ServerJoin.RunAsync(dataDirectory,
                () => new JoiningServer("gateway", target.InstanceId, Dns.GetHostName(), ServerJoin.AddressFor(addresses[0].ToUri()), PairingProof.CertificateFingerprint(certificate),
                    ConsoleSigningKey.Generate(), Listeners: GatewayListeners.Reported(options), GroupKey: HeldKey(options, sessionKey)),
                pending, target.Clients, new JoinedConsoles(new GroupSettings(target.State)), ct,
                keep: joined =>
                {
                    if (joined.GroupKey is { } key) sessionKey.Join(key);
                    if (joined.Listeners is { } group) GatewayListeners.TakeGroup(GatewayConfigFile.PathIn(dataDirectory), options, group);
                });
        }
        catch (ServerJoinException ex) { throw DbDataKit.Hosting.Startup.StartupStepException.JoinFailed(ex.Console, ex.Answer, ex.Message, ex); }
        finally
        {
            await callback.StopAsync(CancellationToken.None);
            await callback.DisposeAsync();
        }
        return true;
    }

    /// <summary>The listener that answers the console's callback during a join: the management API's addresses and certificate, the callback route only.</summary>
    private static WebApplication CallbackListener(IReadOnlyList<DbDataKit.Hosting.Startup.ListenUrl> addresses, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, PendingJoins pending)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            foreach (var url in addresses) kestrel.Listen(url.Address, url.Port, listen => listen.UseHttps(certificate));
        });
        var callback = builder.Build();
        callback.MapJoinCallback(pending);
        return callback;
    }

    /// <summary>The group's key as this instance holds it, sent with the join so a console that keeps none for the group keeps this one; null when it holds none.</summary>
    private static string? HeldKey(GatewayOptions options, DbDataGateway.Server.State.GroupSessionKey sessionKey)
    {
        if (!sessionKey.Key.Held) return null;
        var store = SecretsRuntime.OpenStore(options) as DbDataKit.Secrets.ISecretProvider;
        using var held = store?.TryResolveAsync(DbDataKit.Secrets.SecretRef.Parse("secret:" + DbDataGateway.Server.State.GroupSessionKey.Secret)).AsTask().GetAwaiter().GetResult();
        return held?.RevealString();
    }

    /// <summary>Adds the management listener to Kestrel (after the Flight one). Only when the options ask for it and TLS is on.</summary>
    public static void Listen(KestrelServerOptions kestrel, GatewayOptions options, TlsCertificateHolder holder)
    {
        if (!options.Management.Enabled || options.Tls.Mode == TlsMode.None) return;
        foreach (var url in GatewayListeners.Parsed(options.Management.Listen))
            kestrel.Listen(url.Address, url.Port, listen =>
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
        // the service makes its configuration folder at its first start, so it owns it; a folder another account owns stops the start naming the folder, its owner and the fix
        var git = new GitRepositoryOwnership().Open(configDirectory);
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
        var adoptions = new DataSourceAdoptions(configDirectory);
        // what one paired client owns, an administrator may hand to another (the group's hand-overs): every owner check below reads the owner through it
        var handovers = new DbDataKit.State.Api.OwnerHandovers(new GroupSettings(state));
        Func<string?, string?> ownerNow = handovers.Resolve;
        dataSources = new GatewayDataSources(configDirectory, management, provider.ReloadNow, () => app.Services.GetRequiredService<DataSourceCatalog>(), audit, git, findClient: clients.Find, published: app.Services.GetRequiredService<Serving.PublishedDataSources>(), adoptions: adoptions,
            servingCatalog: () => app.Services.GetRequiredService<DbDataKit.Serving.IServingServices>().Catalog,
            // plan 30 PB02: the bindings of the data sources' secret slots, and whether the Gateway holds a secret a slot is bound to (resolved here, never returned)
            registry: new DbDataKit.State.DefinitionPublisher(state, Serving.ServingKinds.All, targetType: Serving.ServingKinds.TargetType),
            secretHeld: reference =>
            {
                try { return app.Services.GetRequiredService<Secrets.SecretsRuntime>().ResolveSecret("Gateway:DataSources:SecretSlot", reference) is { Length: > 0 }; }
                catch (Secrets.SecretConfigurationException) { return false; }
            }, ownerNow: ownerNow);
        // the semantic models' route, which the shared publish API's hook for the kind also uses (plan 30 PB02)
        var semanticModels = new GatewaySemanticModels(app.Services.GetRequiredService<Serving.PublishedSemanticModels>(), app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(),
            () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, configDirectory, () => app.Services.GetRequiredService<DataSourceCatalog>(), audit, adoptions: adoptions, ruled: () => app.Services.GetRequiredService<Serving.RuledTables>(), ownerNow: ownerNow);
        // the entity models' route, which the shared publish API's hook for the kind also uses (plan 30 PB02)
        var entityModels = new GatewayEntityModels(app.Services.GetRequiredService<Serving.PublishedEntityModels>(),
                () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue,
                dataSource =>
                {
                    // who made a data source: its file in this instance's configuration, else the group's published one (O04b)
                    var file = Path.Combine(configDirectory, "connections", dataSource + ".yaml");
                    var text = File.Exists(file) ? File.ReadAllText(file) : app.Services.GetRequiredService<Serving.PublishedDataSources>().Text(dataSource);
                    return text is null ? null : ownerNow(DbDataKit.Connections.ConnectionDefinitionLoader.LoadDatabaseConnection(text, dataSource + ".yaml").Definition?.ManagedBy);
                }, audit, adoptions: adoptions, ruled: () => app.Services.GetRequiredService<Serving.RuledTables>(), ownerNow: ownerNow);
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
            instanceId, GatewayHost.Version, app.Services.GetRequiredService<DbDataGateway.Server.State.ServingInstanceLease>(), Move: true,
            SecretSamples: DbDataGateway.Server.State.GatewayStateSecrets.For(() => options)));
        // the result cache's calls (plan 24a UC13, UC16); a change a producer reports is mapped to what this Gateway serves by its lineage graph
        var servingCache = app.Services.GetRequiredService<DbDataGateway.Server.Serving.GatewayCache>();
        var cacheApi = new GatewayCacheManagement(servingCache, () => management.BuildLineage(), instanceId, audit, app.Services.GetService<DbDataGateway.Server.State.ServingCacheEpochStore>(), access: app.Services.GetRequiredService<Serving.GatewayAccess>());
        servingCache.Resolve = cacheApi.Resolve;
        var serverSettings = new GatewayServerSettings(configDirectory, management, provider.ReloadNow, audit, git);
        management = new GatewayManagement(configuration, configDirectory, instanceId, GatewayHost.Version, dataSources, identities, activity, certificate, git,
            new GatewaySecrets(app.Services.GetRequiredService<GroupSecretStore>() is { Enabled: true } group ? group : SecretsRuntime.OpenStore(options), audit, references: dataSources.References), files.Codes(),
            semanticModels,
            pairedClients: clients.All,
            identityAdmin: options.Identity.Enabled
                ? new DbDataKit.Identity.IdentityAdminApi(app.Services.GetRequiredService<DbDataKit.State.StateDatabase>(), GatewayIdentityModule.PublicUrlOf(options).AbsoluteUri, new DbDataKit.Identity.AccessTokenPolicy(options.Identity.AccessTokenMaxDays))
                : null,
            stateApi: stateApi,
            views: new GatewayViewsManagement(app.Services.GetRequiredService<Serving.GatewayViewsPublishing>(), GatewayModules.Has(options, GatewayModules.Views) ? app.Services.GetRequiredService<Serving.GatewayViews>() : null, audit),
            userQueries: new GatewayUserQueries(app.Services.GetRequiredService<DbDataGateway.Server.State.SavedQueryStore>(), app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(),
                GatewayModules.Has(options, GatewayModules.Views) ? app.Services.GetRequiredService<Serving.GatewayViews>() : null, audit),
            semanticRuns: new GatewaySemanticRuns(app.Services.GetRequiredService<DbDataGateway.Server.State.SemanticRunStore>(), app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(),
                app.Services.GetRequiredService<Serving.PublishedSemanticModels>(), app.Services.GetRequiredService<Serving.GatewayServing>(), app.Services.GetRequiredService<DbDataKit.Serving.IServingServices>(),
                GatewayModules.Has(options, GatewayModules.Views) ? app.Services.GetRequiredService<Serving.GatewayViews>() : null, audit),
            lineage: new GatewayLineage(() => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue,
                () => app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>().All,
                () => app.Services.GetRequiredService<Serving.PublishedEntityModels>().All,
                () =>
                {
                    // the dashboards and reports published to the group read what they depend on; an app instance reads the Store models it is bound to
                    var definitions = new DbDataKit.State.DefinitionStore(state);
                    // every channel, not production's alone: the contract guard asks who reads a column in any of them (plan 21 CH06)
                    var instances = new DbDataKit.Apps.AppInstanceStore(definitions);
                    return [.. new[] { "dashboard", "report" }.SelectMany(kind => definitions.ChannelsOfKind(kind).SelectMany(channel => definitions.CurrentOfKind(kind, channel).Select(v => new GatewayConsumer(kind, v.Name, v.DependsOn, channel)))),
                        .. definitions.ChannelsOfKind(DbDataKit.Apps.AppInstanceStore.InstanceKind).SelectMany(channel => instances.List(channel).Select(i => new GatewayConsumer("app", i.Name, [.. i.Bindings.Keys.Select(model => "entity-model:" + model)], channel)))];
                },
                (name, text) => app.Services.GetRequiredService<Secrets.SecretsRuntime>().ResolveConnectionString($"Gateway:DataSources:{name}:ConnectionString", text, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue.DataSources[name].Provider),
                app.Services.GetRequiredService<DbDataGateway.Server.State.TableReadRecorder>(), app.Services.GetRequiredService<DbDataGateway.Server.State.TableReadStore>(), instanceId),
            audit: audit,
            cache: cacheApi,
            accessKeys: new GatewayAccessManagement(app.Services.GetRequiredService<DbDataGateway.Server.State.GroupMaskKey>(), servingCache, audit, inspector: new Serving.GatewayAccessInspector(
                app.Services.GetRequiredService<Serving.GatewayAccess>(), () => app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(), app.Services.GetRequiredService<Serving.PublishedEntityModels>(), state,
                app.Services.GetRequiredService<Secrets.SecretsRuntime>(), () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue)),
            definitions: new GatewayDefinitionsManagement(state, audit, handovers: handovers, isClient: id => clients.Find(id) is not null, hooks: GatewayDefinitionsManagement.HooksOf(state, semanticModels, app.Services.GetRequiredService<Serving.PublishedSemanticModels>(), dataSources, entityModels)),
            serverSettings: serverSettings,
            export: new GatewayExportManagement(new Export.ExportInstaller(Export.ExportInstaller.DefaultFetcher(), dataDirectory, GatewayHost.Version),
                () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue, dataDirectory, serverSettings, audit),
            entityModels: entityModels,
            apps: new GatewayApps(DbDataGateway.Server.Serving.ServingKinds.StoreOf(state), () => HomeApprovers.Open(state, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue), audit,
                appsAddress: () => { var current = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue; return GatewayModules.Has(current, GatewayModules.Apps) ? GatewayAppsModule.PublicUrlOf(current) : null; },
                instanceSaved: name => AppClient(app, state, name, remove: false), instanceRemoved: name => AppClient(app, state, name, remove: true),
                addressVerifier: new Serving.AppAddressVerifier(() => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue,
                    reference => app.Services.GetRequiredService<Secrets.SecretsRuntime>().ResolveSecret("Gateway:Apps:Addresses", reference))));
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
        app.AnnounceToConsole("dbdatagateway", instanceId, GatewayListeners.Parsed(options.Management.Listen).FirstOrDefault()?.Port ?? 0, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new StateReplayCache(used, "key-rotation:" + instanceId)));
        // the console's callback during the join step, on the management listener the join names
        app.MapJoinCallback(app.Services.GetRequiredService<PendingJoins>());
        var joinTarget = app.Services.GetRequiredService<GatewayJoinTarget>();
        (joinTarget.State, joinTarget.Clients, joinTarget.InstanceId) = (state, clients, instanceId);

        // the server upgrades itself when the console says so (plan 24a UC09); its own route wins over the catch-all below
        DbDataKit.Upgrade.UpgradeSetup.MapUpgrade(app, "dbdatagateway", GatewayHost.Version, GatewayHost.RegistrationName, GatewayListeners.Parsed(options.Management.Listen).FirstOrDefault()?.Port ?? 0, audit, () => [.. clients.All().Select(c => new DbDataKit.Upgrade.PeerGeneration(c.Name, c.Generation))]);
        // the shared publish API's uploads (plan 30 PB02): streamed, so they are not the JSON calls below
        var definitionsApi = new DbDataGateway.Server.Management.GatewayDefinitionsManagement(state, audit);
        app.MapMethods($"{GatewayManagement.Prefix}/artifacts/{{sha256}}", ["HEAD", "GET", "PUT"], async (string sha256, HttpContext context) =>
        {
            var caller = new ManagementCaller(context.ManagementToken());
            if (context.Request.Method == "GET")
                return caller.Holds(ServerRole.Viewer) || caller.Holds(ServerRole.Publisher) ? (definitionsApi.OpenArtifact(sha256) is { } stream ? Results.Stream(stream, "application/octet-stream") : Results.NotFound()) : Results.StatusCode(StatusCodes.Status403Forbidden);
            if (context.Request.Method == "HEAD")
                return caller.Holds(ServerRole.Viewer) || caller.Holds(ServerRole.Publisher) ? (definitionsApi.HasArtifact(sha256) ? Results.Ok() : Results.NotFound()) : Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await definitionsApi.StoreArtifactAsync(sha256, context.Request.Body, context.Request.ContentType, caller, context.RequestAborted);
            return Results.Json(result.Body, statusCode: result.Status);
        });
        // the group's master data for the console's screens (plan 33 SC24): the store module's paths under /api/v1/store/models, as the person the token names, with the module's own rules; a write clears what this Gateway caches of it
        if (GatewayModules.Has(options, GatewayModules.Store))
            app.MapMethods($"{GatewayStoreManagement.Prefix}/{{**rest}}", ["GET", "POST", "DELETE"], async (HttpContext context) =>
            {
                var sp = app.Services;
                var api = Hosting.GatewayStoreModule.ApiOf(sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>(), sp.GetRequiredService<DbDataKit.State.StateDatabase>(), sp.GetRequiredService<DbDataGateway.Server.Secrets.SecretsRuntime>(),
                    sp.GetRequiredService<Serving.PublishedEntityModels>(), sp.GetService<Serving.GatewayCache>());
                var body = await GatewayStoreManagement.BodyAsync(context.Request.Body, context.Request.Method, context.RequestAborted);
                var result = await GatewayStoreManagement.HandleAsync(api, context.ManagementToken(), context.Request.Method, context.Request.Path.Value! + context.Request.QueryString, body, context.RequestAborted);
                return Results.Json(result.Body, statusCode: result.Status);
            });
        // the approvals of the mcp module, for the console's inbox (plan 27a AG33): the person the token names answers, with the module's own rules
        if (GatewayModules.Has(options, GatewayModules.Mcp))
            app.MapMethods($"{GatewayMcpManagement.Prefix}/{{**rest}}", ["GET", "POST"], async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = context.Request.Method == "POST" ? await reader.ReadToEndAsync(context.RequestAborted) : null;
                var result = await GatewayMcpManagement.HandleAsync(app.Services.GetRequiredService<Serving.McpServices>().Approvals, context.ManagementToken(), context.Request.Method, context.Request.Path.Value! + context.Request.QueryString, body, context.RequestAborted);
                return Results.Json(result.Body, statusCode: result.Status);
            });
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
            else if (new DbDataKit.Apps.AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(state)) is var store && store.Get(instance) is { } saved)
                Serving.AppInstanceClients.SyncRedirects(clients, saved, GatewayAppsModule.PublicUrlOf(current), current.Apps.Home.ClientId, store.List());
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

/// <summary>What the management API made the join step with: the state, the paired clients and the instance id; empty while the management API is off.</summary>
public sealed class GatewayJoinTarget
{
    public DbDataKit.State.StateDatabase? State { get; set; }

    public IPairedClientStore? Clients { get; set; }

    public string? InstanceId { get; set; }
}
