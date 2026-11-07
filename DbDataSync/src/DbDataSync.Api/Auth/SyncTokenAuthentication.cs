using System.Text.Encodings.Web;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace DbDataSync.Api.Auth;

/// <summary>
/// The one way a person's call reaches Sync (plan 13a, X06): a console's signed token, which names the person and holds the roles the console may grant here. Sync keeps no users, sessions,
/// invitations, passkeys, Windows sign-in or network trust of its own; a request with no token has no caller, and the fallback policy refuses it. The runner's own routes (<c>/api/runner</c>)
/// have a guard and token of their own (<c>RunnerStateGuard</c>), and <c>/api/health</c>, pairing and <c>/version</c> are open on purpose.
/// </summary>
public sealed class SyncTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ConsoleTokenValidator tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[ManagementAuthentication.TokenHeader].FirstOrDefault() is not { Length: > 0 } token)
            return Task.FromResult(AuthenticateResult.NoResult());
        return Task.FromResult(ConsoleTokenAuthentication.Authenticate(tokens.Validate(token), SyncAuth.Product, Scheme.Name));
    }
}

public static class SyncTokenAuthenticationRegistration
{
    /// <summary>The token scheme, the options and current user the controllers read, and the two policies with the fallback that closes anything unmarked (admin only).</summary>
    public static IServiceCollection AddSyncTokenAuth(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton(sp => AuthOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(PasskeyOptions.Disabled);
        // A singleton that reads IHttpContextAccessor per call: a scoped current user would make every singleton that records who did something fail to construct.
        services.AddSingleton<CurrentUser>();
        services.AddAuthentication(SyncAuth.Product.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SyncTokenAuthenticationHandler>(SyncAuth.Product.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(nameof(UserRole.Admin)))
            .AddPolicy(Policies.Viewer, policy => policy.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Viewer)))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin)).Build());
        return services;
    }
}
