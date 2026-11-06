using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Auth.Controllers;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataConsole.Server;

/// <summary>Where an identity home that runs on its own keeps what is its own, and where it listens (plan 13a U15).</summary>
/// <param name="Directory">The home's directory: <c>state.db</c> (users, sessions, invitations, groups, role assignments, clients, signing keys; with SQLite), <c>dataprotection/</c> (the key ring that protects the signing keys at rest), <c>secrets/</c>, its certificate, and <c>FIRST-RUN.txt</c> until the first administrator exists. <b>Moving the home is moving this directory and the state database</b> (the directory alone when the state is SQLite).</param>
/// <param name="Listen">The address it listens on; HTTPS (a passkey needs a secure origin).</param>
/// <param name="PublicUrl">The address every console, app and browser reaches the home at, which is the token issuer: it has to be the same before and after the home moves. Null: the address it listens on, or <c>localhost</c> on the same port where that is every address.</param>
/// <param name="State">The engine of the state database; null is SQLite in <see cref="Directory"/>.</param>
public sealed record IdentityHostSettings(string Directory, Uri Listen, Uri? PublicUrl = null, StateSettings? State = null)
{
    public const string Product = "dbdatakit-identity";
    public const string DefaultListen = "https://localhost:8490";

    /// <summary>The variable that names the home's directory for every command.</summary>
    public const string DirectoryVariable = "DBDATAKIT_IDENTITY_DIR";

    public const string EngineVariable = "DBDATAKIT_IDENTITY_STATE_ENGINE";
    public const string ConnectionVariable = "DBDATAKIT_IDENTITY_STATE_CONNECTION";

    /// <summary>The address a browser opens, and the origin a passkey is bound to.</summary>
    public Uri Public => PublicUrl ?? (Listen.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+" ? new UriBuilder(Listen) { Host = "localhost" }.Uri : Listen);

    /// <summary>The issuer written into every token: the public address without a path.</summary>
    public string Issuer => Public.GetLeftPart(UriPartial.Authority);

    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatakit", "identity");

    public static StateSettings? StateFrom(Func<string, string?>? environment, string? engineFlag = null, string? connectionFlag = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var engine = engineFlag ?? environment(EngineVariable);
        var connection = connectionFlag ?? environment(ConnectionVariable);
        return string.IsNullOrWhiteSpace(engine) && string.IsNullOrWhiteSpace(connection) ? null : new StateSettings(engine, connection);
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
        var secrets = new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(directory, environment), new DbDataKit.Secrets.EnvSecretProvider(environment)]);
        return StateOpener.Open(settings, path, DbDataKit.Identity.IdentityHomeState.Modules, StateScope.Install, secrets, "dbdatakit identity");
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
            host.Version = ConsoleHost.Version;
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(AddressOf(settings.Listen), settings.Listen.Port, listen => listen.UseHttps(certificate)));

        builder.Services.AddDataProtection().SetApplicationName("dbdatakit-identity").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.Directory, "dataprotection")));
        builder.Services.AddSingleton(_ => IdentityHostState.Open(settings.Directory, settings.State));
        builder.AddIdentityHome(new IdentityHomeOptions(settings.Public, settings.Directory,
            sp => sp.GetRequiredService<StateDatabase>(),
            sp => new DataProtectionSigningKeyProtector(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("dbdatakit-identity.signing-key")),
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
