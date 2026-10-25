using DbDataKit.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Web.Auth;

/// <summary>
/// The registrations every host that signs people in shares (plan 13, U07): the options, the current user, the passkeys, the user, session and invitation stores over the host's <see cref="StateDatabase"/>, the first-run
/// invitation, the session scheme, and the two policies with the fallback that closes anything unmarked. A host that also signs in with Windows adds <c>AddNegotiate()</c> to the builder this returns.
/// </summary>
public static class SessionAuthRegistration
{
    /// <param name="services">The host's services.</param>
    /// <param name="product">Whose sign-in this is; every cookie, section and claim derives from it.</param>
    /// <param name="state">The host's state database (a product may register a type derived from <see cref="StateDatabase"/>, so it says how to get it).</param>
    /// <param name="passkeys">The passkey options, built from the host's own configuration and public address.</param>
    /// <param name="firstRunDirectory">Where the first-run invitation is written (the host's data directory).</param>
    /// <returns>The authentication builder, with the session scheme added.</returns>
    public static AuthenticationBuilder AddSessionAuth(this IServiceCollection services, AuthProduct product, Func<IServiceProvider, StateDatabase> state, Func<IServiceProvider, PasskeyOptions> passkeys, Func<IServiceProvider, string> firstRunDirectory)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton(sp => AuthOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>(), product));
        // A singleton that reads IHttpContextAccessor per call: a scoped current user would make every singleton that records who did something fail to construct.
        services.AddSingleton<CurrentUser>();
        services.AddSingleton(passkeys);
        services.AddSingleton<PasskeyService>();
        services.AddSingleton(sp => new UserStore(state(sp)));
        services.AddSingleton(sp => new SessionStore(state(sp)));
        services.AddSingleton(sp => new InviteStore(state(sp)));
        services.AddSingleton(sp => new BootstrapInviteLocation(firstRunDirectory(sp)));
        services.AddHostedService<BootstrapInvite>();

        // One scheme for every request, so there is one answer to "who is this".
        var authentication = services
            .AddAuthentication(product.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(product.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, policy => policy.RequireRole(nameof(UserRole.Admin)))
            // An admin is a viewer too. Stating it here rather than putting both roles on every read endpoint keeps the attribute on a controller meaning what it says.
            .AddPolicy(Policies.Viewer, policy => policy.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Viewer)))
            // Anything unmarked is closed: an endpoint added later is admin-only by omission rather than open by omission.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(nameof(UserRole.Admin))
                .Build());
        return authentication;
    }
}
