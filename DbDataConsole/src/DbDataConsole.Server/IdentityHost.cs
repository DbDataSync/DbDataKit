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

    /// <summary>The page a person who is not signed in at the home is sent to by <c>/identity/authorize</c>.</summary>
    public const string SignInPath = "/identity/sign-in";

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

        var relyingParty = $"{Auth.Section}:Passkeys:RelyingPartyId";
        if (string.IsNullOrWhiteSpace(builder.Configuration[relyingParty]))
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [relyingParty] = settings.Public.Host });

        builder.Services.AddDataProtection().SetApplicationName("dbdatakit-identity").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.Directory, "dataprotection")));
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(AuthController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton(_ => IdentityHostState.Open(settings.Directory, settings.State));
        builder.Services.AddSessionAuth(Auth,
            sp => sp.GetRequiredService<StateDatabase>(),
            sp => PasskeyOptions.FromConfiguration(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), Auth, settings.Public.GetLeftPart(UriPartial.Authority), [], []),
            _ => settings.Directory);
        builder.Services.AddSingleton(sp => new HomeDirectory(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new OidcClients(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSingleton(sp => new SigningKeys(sp.GetRequiredService<StateDatabase>(),
            new DataProtectionSigningKeyProtector(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("dbdatakit-identity.signing-key"))));
        builder.Services.AddSingleton(sp => new OpenIdProvider(sp.GetRequiredService<StateDatabase>(), sp.GetRequiredService<UserStore>(), sp.GetRequiredService<HomeDirectory>(),
            sp.GetRequiredService<OidcClients>(), sp.GetRequiredService<SigningKeys>(), settings.Issuer, sp.GetService<TimeProvider>()));
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        if (app.Services.GetRequiredService<PasskeyOptions>().Problem() is { } problem)
            log($"Passkeys are misconfigured and will not work: {problem}");
        // a home that has no signing key yet makes its first one now, so the first request is not the one that pays for it
        app.Services.GetRequiredService<SigningKeys>().Current();

        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();

        if (Directory.Exists(webRoot))
        {
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        // The provider's endpoints are public by nature (a console and a browser reach them with no session); the sign-in page too. Everything else stays closed by the fallback policy.
        var open = app.MapGroup("");
        open.AllowAnonymous();
        var options = app.Services.GetRequiredService<AuthOptions>();
        var sessions = app.Services.GetRequiredService<SessionStore>();
        var provider = app.Services.GetRequiredService<OpenIdProvider>();
        open.MapOpenIdProvider(provider, app.Services.GetRequiredService<SigningKeys>(), new OpenIdHostOptions(
            c => c.User.FindFirst(Auth.UserIdClaim)?.Value,
            SignInPath,
            EndSession: c =>
            {
                if (c.Request.Cookies[options.CookieName] is { Length: > 0 } id) sessions.Delete(id);
                c.Response.Cookies.Delete(options.CookieName);
                return Task.CompletedTask;
            },
            // the console's sign-out sends the browser here from another site, with no cookie: the hint names the person
            EndSessionOf: (_, userId) => { sessions.DeleteAllFor(userId); return Task.CompletedTask; }));
        open.MapGet(SignInPath, () => Results.Content(SignInPage, "text/html; charset=utf-8"));

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

    /// <summary>
    /// A person who is not signed in at the home arrives here from <c>/identity/authorize</c> with <c>returnUrl</c>. If they already have a session they go straight back (this navigation is from the home's own site, so the
    /// session cookie, which is SameSite Strict, comes with it); otherwise they sign in with a passkey. Only an address under <c>/identity/</c> of the home is returned to.
    /// </summary>
    public const string SignInPage = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sign in</title>
        <style>body{font:16px system-ui,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;background:#f5f5f4;color:#1c1917}
        main{background:#fff;padding:2rem;border-radius:.5rem;box-shadow:0 1px 4px #0003;max-width:22rem;width:calc(100% - 2rem)}
        button{font:inherit;padding:.6rem 1rem;width:100%;cursor:pointer}p#message{color:#b91c1c;min-height:1.2em}</style></head>
        <body><main data-testid="identity-sign-in"><h1>Sign in</h1><p>Use your passkey to sign in to the identity home.</p>
        <button id="passkey" data-testid="identity-sign-in-passkey" type="button">Sign in with a passkey</button><p id="message" role="alert"></p></main>
        <script>
        const requested = new URLSearchParams(location.search).get('returnUrl') || '';
        const target = requested.startsWith('/identity/') && !requested.startsWith('//') && !requested.includes('\\') ? requested : '/';
        const message = text => { document.getElementById('message').textContent = text; };
        const toBuffer = b64 => { const p = b64.replace(/-/g, '+').replace(/_/g, '/'); const bin = atob(p.padEnd(Math.ceil(p.length / 4) * 4, '=')); return Uint8Array.from(bin, c => c.charCodeAt(0)).buffer; };
        const toB64 = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        async function signedIn() { const r = await fetch('/api/auth/status'); return r.ok && (await r.json()).userId; }
        async function go() {
          const begin = await fetch('/api/auth/passkey/begin', { method: 'POST' });
          if (!begin.ok) { message((await begin.json()).error || 'Passkeys are not available here.'); return; }
          const o = await begin.json();
          const c = await navigator.credentials.get({ publicKey: { ...o, challenge: toBuffer(o.challenge), allowCredentials: (o.allowCredentials ?? []).map(a => ({ ...a, id: toBuffer(a.id) })) } });
          const r = c.response;
          const done = await fetch('/api/auth/passkey/complete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
            id: c.id, rawId: toB64(c.rawId), type: c.type, extensions: c.getClientExtensionResults(),
            response: { authenticatorData: toB64(r.authenticatorData), clientDataJson: toB64(r.clientDataJSON), signature: toB64(r.signature), userHandle: r.userHandle ? toB64(r.userHandle) : null } }) });
          if (!done.ok) { message((await done.json()).error || 'That did not sign you in.'); return; }
          location.replace(target);
        }
        document.getElementById('passkey').addEventListener('click', () => go().catch(e => message(e.message || String(e))));
        signedIn().then(yes => { if (yes) location.replace(target); }).catch(() => { });
        </script></body></html>
        """;

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
