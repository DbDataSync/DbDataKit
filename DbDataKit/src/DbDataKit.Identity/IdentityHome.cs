using System.Text.Json.Serialization;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Auth.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Identity;

/// <summary>What a host of the identity home needs in its state database: the <c>identity</c> module (plan 29a Q09, Q16), which holds everything an install's people are.</summary>
public static class IdentityHomeState
{
    public static IReadOnlyList<StateModule> Modules { get; } = [IdentityModule.Module];
}

/// <summary>What a host of the identity home supplies: where it is reached, where its state is, how its signing keys are protected.</summary>
/// <param name="PublicUrl">The address every console, app and browser reaches the home at. Its scheme, host and port are the token issuer and the origin a passkey is bound to, so they must not change when the home moves.</param>
/// <param name="FirstRunDirectory">Where the first-run invitation (<see cref="BootstrapInvite.FileName"/>) is written while the home has no user.</param>
/// <param name="State">The state database that holds the home (the <c>identity</c> module, <see cref="IdentityModule.Module"/>, built into it).</param>
/// <param name="Protector">Protects the private half of the signing keys at rest. Every instance that shares the state must be given the same one.</param>
/// <param name="Auth">The product name of the home's own sign-in: its configuration section, cookie and scheme.</param>
/// <param name="InvitePage">Serve the home's own invitation page at <c>/invite</c> (the address the first-run invitation names). A host that serves the console's web app beside the home passes false and its <c>/invite</c> screen is used.</param>
/// <param name="Entra">Sign people in through a Microsoft Entra ID tenant too (plan 20 O06b); null: not offered.</param>
/// <param name="Windows">Sign people in with their Windows account too (Negotiate); null: not offered.</param>
/// <param name="Tokens">How long personal access tokens may last here (plan 13a U20); null: 90 days by default, at most a year.</param>
public sealed record IdentityHomeOptions(
    Uri PublicUrl,
    string FirstRunDirectory,
    Func<IServiceProvider, StateDatabase> State,
    Func<IServiceProvider, ISigningKeyProtector> Protector,
    AuthProduct Auth,
    bool InvitePage = true,
    EntraUpstream? Entra = null,
    WindowsUpstream? Windows = null,
    AccessTokenPolicy? Tokens = null)
{
    /// <summary>The issuer written into every token: the public address without a path.</summary>
    public string Issuer => PublicUrl.GetLeftPart(UriPartial.Authority);
}

/// <summary>
/// The identity home in a web application (plan 13a U13, U15; plan 20 O06): the OpenID Connect provider, the passkey sign-in, invitations and the shared sign-in controllers, over one state database.
/// The same code runs in the serving layer (Proxy's <c>identity</c> listener) and on its own (<c>dbdatakit identity run</c>); the host adds its own listener, certificate, health endpoints and anything else it serves.
/// </summary>
public static class IdentityHome
{
    /// <summary>The page a person who is not signed in at the home is sent to by <c>/identity/authorize</c>.</summary>
    public const string SignInPath = "/identity/sign-in";

    /// <summary>The invitation page: the address of the first-run invitation and of every invitation made at the home.</summary>
    public const string InvitePath = "/invite";

    /// <summary>Registers the home's services. Call before <c>Build</c>.</summary>
    public static WebApplicationBuilder AddIdentityHome(this WebApplicationBuilder builder, IdentityHomeOptions options)
    {
        if (options.PublicUrl.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The identity home is reached over HTTPS only (a passkey needs a secure origin).", nameof(options));
        var relyingParty = $"{options.Auth.Section}:Passkeys:RelyingPartyId";
        if (string.IsNullOrWhiteSpace(builder.Configuration[relyingParty]))
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [relyingParty] = options.PublicUrl.Host });

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddControllers()
            .AddApplicationPart(typeof(AuthController).Assembly)
            .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var authentication = services.AddSessionAuth(options.Auth,
            options.State,
            sp => PasskeyOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>(), options.Auth, options.Issuer, [], []),
            _ => options.FirstRunDirectory);
        if (options.Windows is not null) authentication.AddNegotiate();
        UpstreamSignIn.Register(services, options);
        services.AddSingleton(sp => new AccessTokens(options.State(sp), sp.GetRequiredService<IUserAccounts>(), options.Tokens, sp.GetService<TimeProvider>()));
        // a user name and a personal access token, for clients that can send nothing else (the session handler tries it on Authorization: Basic)
        services.AddSingleton<IPasswordCredentials>(sp => sp.GetRequiredService<AccessTokens>());
        services.AddSingleton(sp => new HomeDirectory(options.State(sp)));
        services.AddSingleton(sp => new OidcClients(options.State(sp)));
        services.AddSingleton(sp => new SigningKeys(options.State(sp), options.Protector(sp)));
        services.AddSingleton(sp => new OpenIdProvider(options.State(sp), sp.GetRequiredService<IUserAccounts>(), sp.GetRequiredService<HomeDirectory>(),
            sp.GetRequiredService<OidcClients>(), sp.GetRequiredService<SigningKeys>(), options.Issuer, sp.GetService<TimeProvider>(), sp.GetService<IAppGrants>()));
        return builder;
    }

    /// <summary>
    /// Maps the home: authentication, the shared sign-in controllers, the provider's public endpoints and the home's pages. Call after the host's own middleware that must run first (its pipeline,
    /// its static files) and before its fallback. Reports a misconfiguration of passkeys to <paramref name="log"/>, and makes the first signing key now if there is none.
    /// </summary>
    public static WebApplication MapIdentityHome(this WebApplication app, Action<string> log)
    {
        var options = app.Services.GetRequiredService<IdentityHomeOptions>();
        if (app.Services.GetRequiredService<PasskeyOptions>().Problem() is { } problem)
            log($"Passkeys are misconfigured and will not work: {problem}");
        // a home that has no signing key yet makes its first one now, so the first request is not the one that pays for it
        app.Services.GetRequiredService<SigningKeys>().Current();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        // The provider's endpoints are public by nature (a console and a browser reach them with no session); so are the pages. Everything else stays closed by the fallback policy.
        var open = app.MapGroup("");
        open.AllowAnonymous();
        var auth = app.Services.GetRequiredService<AuthOptions>();
        var sessions = app.Services.GetRequiredService<ISessionStore>();
        open.MapOpenIdProvider(app.Services.GetRequiredService<OpenIdProvider>(), app.Services.GetRequiredService<SigningKeys>(), new OpenIdHostOptions(
            c => c.User.FindFirst(options.Auth.UserIdClaim)?.Value,
            SignInPath,
            EndSession: c =>
            {
                if (c.Request.Cookies[auth.CookieName] is { Length: > 0 } id) sessions.Delete(id);
                c.Response.Cookies.Delete(auth.CookieName);
                return Task.CompletedTask;
            },
            // the console's sign-out sends the browser here from another site, with no cookie: the hint names the person
            EndSessionOf: (_, userId) => { sessions.DeleteAllFor(userId); return Task.CompletedTask; }));
        open.MapGet(SignInPath, () => Results.Content(IdentityHomePages.SignIn(options), "text/html; charset=utf-8"));
        UpstreamSignIn.Map(app, open, options);
        AccessTokenEndpoints.Map(app);
        if (options.InvitePage) open.MapGet(InvitePath, () => Results.Content(IdentityHomePages.Invite, "text/html; charset=utf-8"));
        return app;
    }

    /// <summary>Whether a request path is one of the home's (its endpoints, its pages, the shared sign-in API): what a host that serves other things on other listeners keeps apart.</summary>
    public static bool IsHomePath(PathString path)
    {
        var value = path.Value ?? "";
        return value.StartsWith("/identity/", StringComparison.Ordinal) || value == "/.well-known/openid-configuration" || value == InvitePath
            || value.StartsWith("/api/auth/", StringComparison.Ordinal) || value.StartsWith("/api/invites", StringComparison.Ordinal) || value.StartsWith("/api/users", StringComparison.Ordinal) || value.StartsWith(AccessTokenEndpoints.Path, StringComparison.Ordinal);
    }
}
