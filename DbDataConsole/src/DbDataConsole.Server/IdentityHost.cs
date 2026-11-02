using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Auth.Controllers;
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

/// <summary>The home's state database: <see cref="HomeSchema.Schema"/>, which holds everything an install's people are.</summary>
public static class IdentityHostState
{
    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        System.IO.Directory.CreateDirectory(directory);
        settings ??= new StateSettings();
        var path = Path.Combine(directory, "state.db");
        if (settings.IsSqlite) return StateOpener.Open(settings, path, HomeSchema.Schema);
        // the password of a server engine is a secret reference: the home's store, then the environment
        var secrets = new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(directory, environment), new DbDataKit.Secrets.EnvSecretProvider(environment)]);
        return StateOpener.Open(settings, path, HomeSchema.Schema, secrets);
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

    /// <param name="webRoot">The console's built web app, whose invitation and Users screens the home's own administrators use; the folder <c>wwwroot</c> beside the program when null. Without it the home serves its endpoints and the sign-in page.</param>
    public static WebApplication Build(IdentityHostSettings settings, X509Certificate2 certificate, Action<string> log, string? webRoot = null, Action<IServiceCollection>? configure = null)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The identity home listens on HTTPS only.", nameof(settings));
        webRoot ??= Path.Combine(AppContext.BaseDirectory, "wwwroot");

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
        var hasWebApp = Directory.Exists(webRoot);
        builder.AddIdentityHome(new IdentityHomeOptions(settings.Public, settings.Directory,
            sp => sp.GetRequiredService<StateDatabase>(),
            sp => new DataProtectionSigningKeyProtector(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("dbdatakit-identity.signing-key")),
            Auth,
            // the console's web app, when it is installed beside the home, has the invitation screen; without it the home serves its own
            InvitePage: !hasWebApp));
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();

        if (hasWebApp)
        {
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        app.MapIdentityHome(log);

        RequestDelegate webApp = async context =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/identity", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var index = Path.Combine(webRoot, "index.html");
            if (File.Exists(index))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(index);
                return;
            }
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The dbdatakit identity home is running. Its endpoints are under /.well-known/openid-configuration and /identity/; its administration screens are not installed here (no wwwroot).");
        };
        app.MapFallback(webApp).AllowAnonymous();
        return app;
    }

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
