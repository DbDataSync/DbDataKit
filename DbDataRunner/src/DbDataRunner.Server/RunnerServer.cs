using DbDataKit.Runner;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting;
using DbDataKit.Runner.Api;
using DbDataKit.Hosting.Certificates;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataRunner.Server;

/// <summary>
/// The DbDataRunner server (plan 28a R10): Kestrel over HTTPS with the shared trust code of <c>DbDataKit.Web</c> (pairing, key rotation, the version header, the token check), the management API of <see cref="RunnerApi"/>
/// a console is the client of, and the runner itself (<see cref="RunnerEngine"/>) as a hosted service. It has no web page of its own. The certificate is self-signed and kept in the server's directory, and a client pins its
/// fingerprint at pairing, so nothing else about it needs to be trusted.
/// </summary>
public static class RunnerServer
{
    public static string Version { get; } =
        (typeof(RunnerServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    /// <summary>The server's instance id, made the first time and kept in its state database.</summary>
    /// <summary>
    /// The join step of the start: joins the console a <c>join-request.yaml</c> in the server's directory names, while the server listens, so it answers
    /// the console's callback. The Runner's own group is sent when it is not the default one. The group's key the console hands over is kept in the instance's own store.
    /// </summary>
    public static async Task JoinAsync(WebApplication app, RunnerSettings settings, X509Certificate2 certificate, StateDatabase state, CancellationToken ct)
    {
        try
        {
            await ServerJoin.RunAsync(settings.Directory,
                () => new JoiningServer("runner", InstanceId(state), System.Net.Dns.GetHostName(), ServerJoin.AddressFor(settings.Listen),
                    PairingProof.CertificateFingerprint(certificate), ConsoleSigningKey.Generate(), settings.Group == RunnerState.DefaultGroup ? null : settings.Group),
                app.Services.GetRequiredService<PendingJoins>(), new PairingFiles(settings.ServerYaml, Group: state).Clients(), new JoinedConsoles(new GroupSettings(state)), ct,
                // the group's key, made by the console and handed over in the join's answer: every instance of the group resolves the logins sent to any of them
                keep: joined => { if (joined.GroupKey is { } key) app.Services.GetRequiredService<RunnerGroupSecrets>().Key.Join(key); });
        }
        catch (ServerJoinException ex) { throw DbDataKit.Hosting.Startup.StartupStepException.JoinFailed(ex.Console, ex.Answer, ex.Message, ex); }
    }

    public static string InstanceId(StateDatabase state) => GroupIdentity.InstanceId(new GroupSettings(state), "runner");

    /// <summary>The server's instance id as the directory's state holds it (opens the state, so it is the SQLite file of the directory unless the environment names another).</summary>
    public static string InstanceId(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        return InstanceId(RunnerState.Open(directory, settings, environment));
    }

    /// <summary><c>GET /api/v1/state</c> and the maintenance and move calls over this runner's state (plan 29b C05, C06): the audit is the runner's own (<c>audit</c> module), the instances are the group's live runners.</summary>
    private static DbDataKit.State.Api.StateManagement StateApi(RunnerSettings settings, string instanceId, DbDataKit.State.Api.IMaintenanceGate gate, StateDatabase state) => new(new DbDataKit.State.Api.StateManagementOptions(
        RunnerSettings.Product, RunnerSettings.Product, "the runner",
        () =>
        {
            var resolved = RunnerState.Resolve(settings.Directory);
            return new DbDataKit.State.Cli.StateTarget(settings.State, RunnerState.PathIn(settings.Directory), () => RunnerState.Secrets(null), RunnerState.SettingsFile(settings.Directory), settings.StateSources ?? resolved.Sources);
        },
        RunnerState.Modules, RunnerState.ScopeOf(settings.Group), gate, new DbDataKit.State.Api.StateAuditSink(state, RunnerState.ServiceId), instanceId, Version, Move: true));

    /// <summary>The connections the binding test and the slots both see: the runner's own folder, then the copies the console handed over (plan 27a IX23).</summary>
    private static DbDataKit.Runner.Connections.IConnectionCatalog CatalogOf(RunnerSettings settings, StateDatabase state)
    {
        var host = settings.ToHostSettings();
        return host.Connections ?? new DbDataKit.Runner.Connections.LayeredConnectionCatalog(new DbDataKit.Runner.Connections.DirectoryConnectionCatalog(host.ConnectionsDirectory), new DbDataKit.Runner.Connections.StoredConnectionCatalog(new ConnectionCopies(state)));
    }

    public static WebApplication Build(RunnerSettings settings, X509Certificate2 certificate, Action<string> log, Func<string, string?>? environment = null) =>
        Build(settings, certificate, log, RunnerState.Open(settings.Directory, settings.State, environment, settings.Group));

    /// <summary>Builds the server over a state database it owns from now on (closed with the host).</summary>
    public static WebApplication Build(RunnerSettings settings, X509Certificate2 certificate, Action<string> log, StateDatabase state)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A DbDataRunner server listens on HTTPS only.", nameof(settings));
        var instanceId = InstanceId(state);
        // the runner's own encrypted store (plan 28c E20d): what a product sends with PUT /api/v1/secrets, the last provider of the chain connection slots resolve against
        var secretStore = new RunnerSecretStore(settings.Directory);
        // plan 24a UC13: what a task's run reported writing is told to the serving groups this server is paired with (gateways.json), signed as the server itself; no pairing, nothing sent
        var dataChanges = settings.DataChanges ?? RunnerDataChanges.Create(settings.Directory, secretStore, new LineLoggerProvider(log).CreateLogger("DbDataRunner.DataChanged"));
        // plan 28c E20d (group-wide): what is sent to any instance is kept in the group's state under the group's key, which the console hands each instance it joins; the instance's own store keeps the key and what it held before
        var groupSecrets = new RunnerGroupSecrets(state, () => secretStore.Open(), secretStore.Provider, () => secretStore.Exists);
        settings = settings with { SecretStore = settings.SecretStore ?? groupSecrets.Provider, DataChanges = dataChanges };
        var files = new PairingFiles(settings.ServerYaml, Group: state);
        var clients = files.Clients();
        var fingerprint = PairingProof.CertificateFingerprint(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LineLoggerProvider(log));
        // service and systemd modes, draining on stop, /health and /version, request limits: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = RunnerSettings.Product;
            host.ServiceName = RunnerService.RegistrationName;
            if (settings.SkipFolderPermissionsCheck) host.Loosened.Add(DbDataKit.Hosting.Startup.FolderPermissions.Setting);
            host.Telemetry = DbDataKit.Telemetry.TelemetrySettings.Load(settings.ServerYaml);   // the telemetry: section; off unless it says enabled (plan 22a GP28)
            host.Version = Version;
        });
        // stopping waits for the running tasks to take their cancellation, and then for the processes to go
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromTicks(Math.Max(h.ShutdownTimeout.Ticks, (settings.CancellationGrace + TimeSpan.FromSeconds(30)).Ticks)));
        builder.WebHost.ConfigureKestrel(o =>
        {
            foreach (var each in settings.ListenAll.Count > 0 ? settings.ListenAll : [settings.Listen])
                o.Listen(DbDataKit.Hosting.Startup.ListenPorts.AddressOf(each), each.Port, listen => listen.UseHttps(certificate));
        });

        // the joins this server is waiting on, which the console's callback confirms during the join step of its start
        builder.Services.AddSingleton<PendingJoins>();
        builder.Services.AddSingleton(groupSecrets);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(sp => new RunnerEngine(settings, state, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataRunner.Engine")));
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RunnerEngine>());
        // its own state's retention and snapshot, in this process (plan 35 I1): state.housekeeping; the tasks' work on customer data is the engine's
        builder.Services.AddSingleton<IHostedService>(sp => new DbDataKit.State.Engines.Housekeeping.StateHousekeeping(state, RunnerState.Modules, RunnerState.ScopeOf(settings.Group), RunnerState.ServiceId,
            settings.State, settings.Directory, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataRunner.Housekeeping")));

        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(instanceId, fingerprint, clients, files.Codes()));
        app.AnnounceToConsole(RunnerSettings.Product, instanceId, settings.Listen.Port, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));
        app.MapJoinCallback(app.Services.GetRequiredService<PendingJoins>());
        var tokens = new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache());
        RunnerWebhooks.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), tokens, settings.Secrets ?? RunnerState.Secrets(null));   // before the token check: a signed webhook has no token
        app.UseManagementAuthentication(tokens, refused => log($"A call was refused ({refused.Failure}): {refused.Detail}"));
        // maintenance mode and the state endpoints (plan 29b C05): while on, the runner claims and schedules nothing new, finishes what runs, and reports idle when the last run is over
        var maintenance = new DbDataKit.State.Api.MaintenanceGate(
            onEnter: _ => app.Services.GetRequiredService<RunnerEngine>().Draining = true,
            onLeave: () => app.Services.GetRequiredService<RunnerEngine>().Draining = false,
            work: () => app.Services.GetRequiredService<RunnerEngine>().RunningCount);
        app.UseMaintenance(maintenance, context => context.ManagementTokenOrNull() is { } token && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed);
        app.MapState(StateApi(settings, instanceId, maintenance, state));
        app.MapCertificateStatus(() => certificate);   // days left and subject, for the console's Monitoring (UI10)
        // the server upgrades itself when the console says so (plan 24a UC09)
        DbDataKit.Upgrade.UpgradeSetup.MapUpgrade(app, RunnerSettings.Product, Version, RunnerService.RegistrationName, settings.Listen.Port, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), () => [.. clients.All().Select(c => new DbDataKit.Upgrade.PeerGeneration(c.Name, c.Generation))]);
        RunnerApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), instanceId, new RunnerApiOptions("/api/v1", RunnerCallers.TokenResolver, RunnerSettings.Product, Version) { ControlApprovals = new ControlRunApprovalVerifier(instanceId, clients, new MemoryReplayCache()) });
        RunnerPublishApi.Map(app, new RunnerPublishing(state, settings.MaxArtifactBytes, settings.SignatureVerifier, settings.InstanceLease, new DbDataKit.Runner.Staging.ActivationOptions { MaxWait = settings.ActivationMaxWait }));
        RunnerSecretsApi.Map(app, state, () => groupSecrets.Store, "/api/v1", RunnerCallers.TokenResolver,
            connections: () => RunnerConnections.Inventory(state, app.Services.GetRequiredService<RunnerEngine>().Group, RunnerSettings.Product, settings.ToHostSettings().ConnectionsDirectory),
            listHeld: (_, ct) => groupSecrets.ListHeldAsync(async (opened, c) => opened is DbDataKit.Secrets.Store.ISecretAdmin admin ? [.. (await admin.ListInfoAsync("", c)).Select(i => new DbDataKit.Secrets.SecretHeld(i.Name, i.Updated == default ? null : i.Updated))] : await RunnerSecretsApi.NamesOnly(opened, c), ct));   // UI06: the overview
        // the console hands the group's key to an instance it joins (plan 28c E20d); audited as runner.group.join, never the key
        var groupAudit = new AuditLog(state, RunnerState.ServiceId);
        GroupKeyApi.Map(app, "/api/v1", () => groupSecrets.Key, (context, role) => RunnerCallers.TokenResolver(context).Authorize(role).Allowed,
            (context, outcome, detail) =>
            {
                var actor = RunnerCallers.TokenResolver(context).Actor;
                var engine = app.Services.GetRequiredService<RunnerEngine>();
                groupAudit.Record(new AuditEvent("person", actor.Id, RunnerGroupSecrets.JoinVerb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: "instance",
                    TargetId: $"{engine.InstanceId?.ToString() ?? instanceId} ({System.Net.Dns.GetHostName()})", Detail: System.Text.Json.JsonSerializer.Serialize(detail)));
            },
            held: () => groupSecrets.Held);
        RunnerBindingTestApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>().Slots, "/api/v1", RunnerCallers.TokenResolver, RunnerConnectTest.Create(CatalogOf(settings, state)));   // UI08: try a slot's binding, up to the connection
        RunnerConnectionCopyApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), "/api/v1", RunnerCallers.TokenResolver);   // plan 27a IX23: the connections the console handed over (definition and secret references, never a value)
        RunnerGroupApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), "/api/v1", RunnerCallers.TokenResolver);
        GatewayPairingApi.Map(app, "/api/v1", dataChanges.Pairings, () => Task.FromResult(RunnerDataChanges.Identity(secretStore)), () => dataChanges.Notifier, "DbDataRunner server");
        var lineageCatalog = CatalogOf(settings, state);
        RunnerLineageApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), "/api/v1", RunnerCallers.TokenResolver, lineageCatalog.Find);   // plan 18 G18: a package's tables named by the bound connection
        RunnerTrackingApi.Map(app, state, "/api/v1", RunnerCallers.TokenResolver);   // plan 28c E18: the runner's own copy of Build's tracking, and the resolve of its divergences
        RunnerMetadataApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), "/api/v1", RunnerCallers.TokenResolver,
            settings.BuiltinDirectory ?? Path.Combine(AppContext.BaseDirectory, DbDataKit.Runner.BuiltInPackages.Folder));   // the built-in package dbdatakit-metadata: discover and describe through a project's slot, for a person a console names
        RunnerQualityApi.Map(app, state, "/api/v1", RunnerCallers.TokenResolver, engine: () => app.Services.GetRequiredService<RunnerEngine>());    // plan 35 I3: the results of the checks its tasks evaluated, their history and the release of a block; I3a: a re-check starts the package's `check` task
        return app;
    }
}

/// <summary>Sends the host's own log lines (warnings and above, and the runner's) to a function, so a command line and a test see what the runner says.</summary>
internal sealed class LineLoggerProvider(Action<string> write) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LineLogger(categoryName, write);

    public void Dispose()
    {
    }

    private sealed class LineLogger(string category, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && category.StartsWith("DbDataRunner", StringComparison.Ordinal) || logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            write($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})"));
        }
    }
}
