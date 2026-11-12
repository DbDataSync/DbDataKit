using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Identity;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.State.Engines;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Auth.Controllers;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>Where an identity home that runs on its own keeps what is its own, and where it listens (plan 13a U15).</summary>
/// <param name="Directory">The home's directory: <c>state.db</c> (users, sessions, invitations, groups, role assignments, clients, signing keys; with SQLite), <c>dataprotection/</c> (the key ring that protects the signing keys at rest), <c>secrets/</c>, its certificate, and <c>FIRST-RUN.txt</c> until the first administrator exists. <b>Moving the home is moving this directory and the state database</b> (the directory alone when the state is SQLite).</param>
/// <param name="Listen">The address it listens on; HTTPS (a passkey needs a secure origin).</param>
/// <param name="PublicUrl">The address every console, app and browser reaches the home at, which is the token issuer: it has to be the same before and after the home moves. Null: the address it listens on, or <c>localhost</c> on the same port where that is every address.</param>
/// <param name="State">The engine of the state database; null is SQLite in <see cref="Directory"/>.</param>
/// <param name="StateSources">Where each state setting came from (the flags, the environment, the <c>state:</c> section of <c>server.yaml</c>, the default), for <c>GET /api/v1/state</c>; null: it is worked out from the directory when asked.</param>
public sealed record IdentityHostSettings(string Directory, Uri Listen, Uri? PublicUrl = null, StateSettings? State = null, StateSources? StateSources = null)
{
    public const string Product = "dbdatakit-identity";
    public const string DefaultListen = "https://localhost:8482";

    /// <summary>The module's name in the suite's folders and variables (<see cref="SuiteEnvironment"/>): <c>identity</c>.</summary>
    public const string ServiceName = "identity";

    /// <summary>The variable that names the home's directory for every command: <c>DBDATAKIT_DATA_IDENTITY</c>, or the folder <c>identity</c> under <c>DBDATAKIT_DATA</c>.</summary>
    public static string DirectoryVariable => SuiteEnvironment.FolderVariable(ServiceName);

    public static string EngineVariable => SuiteEnvironment.StateEngineVariableOf(ServiceName);
    public static string ConnectionVariable => SuiteEnvironment.StateConnectionVariableOf(ServiceName);

    /// <summary>The address a browser opens, and the origin a passkey is bound to.</summary>
    public Uri Public => PublicUrl ?? (Listen.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+" ? new UriBuilder(Listen) { Host = "localhost" }.Uri : Listen);

    /// <summary>The issuer written into every token: the public address without a path.</summary>
    public string Issuer => Public.GetLeftPart(UriPartial.Authority);

    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        SuiteEnvironment.ResolveDataFolder(ServiceName, environment, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatakit", "identity"));

    /// <summary>The home's settings file: <c>server.yaml</c> in its directory, whose <c>state:</c> section is the one every product reads (plan 29b C07).</summary>
    public static string SettingsFileIn(string directory) => Path.Combine(directory, "server.yaml");

    /// <summary>
    /// The state settings and where each came from (plan 29b C07): the flags, then <see cref="EngineVariable"/> and <see cref="ConnectionVariable"/> (then the install's), then the <c>state:</c> section of <c>server.yaml</c> in
    /// <paramref name="directory"/>, then SQLite in the directory. The file is read strictly and never created.
    /// </summary>
    public static ResolvedState ResolveState(string directory, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null) =>
        StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs(ServiceName, SettingsFileIn(directory), engineFlag, "--state-engine", connectionFlag, "--state-connection", environment));

    /// <summary>The settings <see cref="ResolveState"/> finds; null when nothing names any, which is SQLite in the directory.</summary>
    public static StateSettings? StateFrom(Func<string, string?>? environment, string? engineFlag = null, string? connectionFlag = null, string? directory = null)
    {
        if (directory is null) return SuiteEnvironment.ResolveState(ServiceName, environment, engineFlag, connectionFlag);
        var resolved = ResolveState(directory, environment, engineFlag, connectionFlag);
        return resolved.Sources == StateSources.Defaults ? null : resolved.Settings;
    }
}

/// <summary>The home's state database: the <c>identity</c> module (<see cref="DbDataKit.Identity.IdentityHomeState"/>), which holds everything an install's people are. A database made by the earlier structure is refused naming it (plan 29a Q16).</summary>
public static class IdentityHostState
{
    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        System.IO.Directory.CreateDirectory(directory);
        settings ??= new StateSettings();
        var path = Path.Combine(directory, "state.db");
        if (settings.IsSqlite) return StateOpener.Open(settings, path, DbDataKit.Identity.IdentityHomeState.Modules, StateScope.Install, appliedBy: "dbdatakit identity");
        // the password of a server engine is a secret reference: the home's store, then the environment
        return StateOpener.Open(settings, path, DbDataKit.Identity.IdentityHomeState.Modules, StateScope.Install, Secrets(directory, environment), "dbdatakit identity");
    }

    /// <summary>The resolver for the references in a server engine's connection string: the home's secret store, then the environment.</summary>
    public static DbDataKit.Secrets.ISecretResolver Secrets(string directory, Func<string, string?>? environment = null) =>
        new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(directory, environment), new DbDataKit.Secrets.EnvSecretProvider(environment)]);
}

/// <summary>The encrypted columns of the home's state (plan 29b C04): the signing keys, protected with the key ring in <c>dataprotection/</c> of the home's directory.</summary>
public static class IdentityHostSecrets
{
    public const string SigningKeyPurpose = "dbdatakit-identity.signing-key";
    public const string ApplicationName = "dbdatakit-identity";

    public static IReadOnlyList<DbDataKit.State.Engines.Snapshots.StateSecretSample> Samples(string directory) =>
    [
        new DbDataKit.State.Engines.Snapshots.StateSecretSample(IdentityModule.Name, "signing_key", "private_key", value => Opens(directory, value)),
    ];

    /// <summary>True when the home's key ring decrypts the value. A directory with no key ring decrypts nothing, and none is made for the question.</summary>
    private static bool Opens(string directory, string stored)
    {
        var ring = new DirectoryInfo(Path.Combine(directory, "dataprotection"));
        if (!ring.Exists) return false;
        var provider = DataProtectionProvider.Create(ring, builder => builder.SetApplicationName(ApplicationName));
        _ = provider.CreateProtector(SigningKeyPurpose).Unprotect(Convert.FromBase64String(stored));
        return true;
    }
}

/// <summary>Protects the signing keys at rest with the framework's key ring, which the home keeps in its directory.</summary>
public sealed class DataProtectionSigningKeyProtector(IDataProtector protector) : ISigningKeyProtector
{
    public byte[] Protect(byte[] privateKey) => protector.Protect(privateKey);
    public byte[] Unprotect(byte[] protectedKey) => protector.Unprotect(protectedKey);
}

/// <summary>
/// The identity home on its own (plan 13a U15, <c>dbdatakit identity run</c>): <see cref="DbDataKit.Identity"/>'s OpenID Connect provider and the shared sign-in (passkeys, sessions, invitations, users) in a host that is
/// nothing else, so it can sit behind the firewall with the serving layer forwarding its public endpoints (plan 20, O07), or run beside the consoles until the serving layer hosts it (O06).
/// Every console, app and token keeps working when the home moves here from anywhere, as long as the address (the issuer), the state and the key ring move with it: the signing keys, the refresh tokens, the clients and
/// the roles are all in the state.
/// </summary>
public static class IdentityHost
{
    public static readonly AuthProduct Auth = new("DbDataIdentity", "dbdatakit identity run (it writes FIRST-RUN.txt again while there is no user)");

    /// <summary>The server's instance id: made and written to <c>server.yaml</c> in the home's directory the first time, read from it after. A console pairs with it like any server (plan 13a U21).</summary>
    public static string InstanceId(string directory) => ServerIdentity.InstanceId(Path.Combine(directory, "server.yaml"), "identity", "dbdatakit identity pair");

    /// <summary>
    /// The home, and the management API a console administers it through (plan 13a U21): pairing, key rotation, the version header and the token check that every server has, with the identity home's admin API
    /// (<see cref="IdentityAdminApi"/>) under <c>/api/v1/identity/</c>. It serves no web app of its own: its people, groups, roles, clients and tokens are administered from a paired console's screens, or at the command line.
    /// </summary>
    public static WebApplication Build(IdentityHostSettings settings, X509Certificate2 certificate, Action<string> log, Action<IServiceCollection>? configure = null)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The identity home listens on HTTPS only.", nameof(settings));
        var serverYaml = Path.Combine(settings.Directory, "server.yaml");
        var instanceId = InstanceId(settings.Directory);
        var pairing = new PairingFiles(serverYaml);
        var paired = pairing.Clients();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LineLoggerProvider(log));
        builder.UseDbDataHost(host =>
        {
            host.Product = IdentityHostSettings.Product;
            host.ServiceName = "DbDataKitIdentity";
            host.Telemetry = DbDataKit.Telemetry.TelemetrySettings.Load(serverYaml);   // the telemetry: section; off unless it says enabled (plan 22a GP28)
            host.Version = ConsoleHost.Version;
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(AddressOf(settings.Listen), settings.Listen.Port, listen => listen.UseHttps(certificate)));

        builder.Services.AddDataProtection().SetApplicationName(IdentityHostSecrets.ApplicationName).PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.Directory, "dataprotection")));
        builder.Services.AddSingleton(_ => IdentityHostState.Open(settings.Directory, settings.State));
        builder.Services.AddSingleton(SessionSettingsFile.Load(serverYaml));   // the session: section of server.yaml (plan 13a U22)
        builder.AddIdentityHome(new IdentityHomeOptions(settings.Public, settings.Directory,
            sp => sp.GetRequiredService<StateDatabase>(),
            sp => new DataProtectionSigningKeyProtector(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(IdentityHostSecrets.SigningKeyPurpose)),
            Auth));
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        // pairing proves itself with its code and key rotation with the old key: neither has a session at the home, whose sign-in closes everything unmarked
        var trust = app.MapGroup("").AllowAnonymous();
        trust.MapPairing(new PairingService(instanceId, PairingProof.CertificateFingerprint(certificate), paired, pairing.Codes()));
        trust.AnnounceToConsole(IdentityHostSettings.Product, instanceId, settings.Listen.Port, paired, pairing.Codes());
        // the used token ids are in the home's state, so instances of a home on one server engine refuse each other's (plan 20 O02b)
        var used = app.Services.GetRequiredService<IOneTimeValues>();
        trust.MapKeyRotation(new KeyRotationService(instanceId, paired, new StateReplayCache(used, "key-rotation:" + instanceId)));
        // the console's signed token on every management call; the home's own /api/auth, /api/invites and /api/access-tokens are a person's session, outside this prefix
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, paired, new StateReplayCache(used, "management:" + instanceId)), refused => log($"warn: a management call was refused ({refused.Failure}): {refused.Detail}"), ManagementPrefix);
        app.MapIdentityHome(log);

        // the state endpoints and the maintenance mode (plan 29b C05): a console shows the home's state and puts it into maintenance, which answers every request but the management calls with 503. Under the management prefix, so the token check above covers them;
        // the home's session policy is for people at the home, so they are marked anonymous as the admin API below is
        var maintenance = new MaintenanceGate();
        app.UseMaintenance(maintenance, context => context.ManagementTokenOrNull() is { } token && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed);
        app.MapState(StateApi(settings, instanceId, maintenance)).AllowAnonymous();

        var admin = new IdentityAdminApi(app.Services.GetRequiredService<StateDatabase>(), settings.Public.AbsoluteUri);
        app.MapMethods(IdentityAdminApi.Prefix + "/{**rest}", ["GET", "POST", "DELETE"], async (HttpContext context, string? rest) =>
        {
            var token = context.ManagementToken();
            System.Text.Json.Nodes.JsonNode? body = null;
            if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            {
                try { body = await System.Text.Json.Nodes.JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
                catch (System.Text.Json.JsonException) { return Results.Json(new { error = "The body is not JSON." }, statusCode: StatusCodes.Status400BadRequest); }
            }
            var result = admin.Handle(context.Request.Method, (rest ?? "") + context.Request.QueryString, body, ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed, token.SubjectId);
            return result.Status == StatusCodes.Status204NoContent ? Results.NoContent() : Results.Json(result.Body, statusCode: result.Status);
        }).AllowAnonymous();   // the token check above is this API's authentication; the home's session policy is for people at the home

        app.MapFallback(() => Results.Text(
            "The dbdatakit identity home is running. Its endpoints are under /.well-known/openid-configuration and /identity/; pair a console with it (dbdatakit identity pair) to administer it.", "text/plain; charset=utf-8"))
            .AllowAnonymous();
        return app;
    }

    /// <summary>The state endpoints over the home's state: the settings it was started with, the audit in a file beside <c>server.yaml</c> (the home's state has no audit module).</summary>
    private static StateManagement StateApi(IdentityHostSettings settings, string instanceId, IMaintenanceGate gate) => new(new StateManagementOptions(
        IdentityHostSettings.Product, "dbdatakit identity", "the identity home",
        () =>
        {
            var resolved = IdentityHostSettings.ResolveState(settings.Directory);
            return new DbDataKit.State.Cli.StateTarget(settings.State ?? resolved.Settings, Path.Combine(settings.Directory, "state.db"), () => IdentityHostState.Secrets(settings.Directory),
                IdentityHostSettings.SettingsFileIn(settings.Directory), settings.StateSources ?? resolved.Sources);
        },
        DbDataKit.Identity.IdentityHomeState.Modules, StateScope.Install, gate, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), instanceId, ConsoleHost.Version, Move: true, SecretSamples: IdentityHostSecrets.Samples(settings.Directory)));

    /// <summary>Where the management calls a paired console makes live: the token check covers this prefix only.</summary>
    public const string ManagementPrefix = "/api/v1/";

    private static IPAddress AddressOf(Uri listen) =>
        listen.Host == "localhost" ? IPAddress.Loopback
        : listen.HostNameType == UriHostNameType.Dns ? IPAddress.Any
        : IPAddress.Parse(listen.Host);

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
