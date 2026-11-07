using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Identity;

/// <summary>
/// Signing in at the home through a Microsoft Entra ID tenant (plan 20 O06b): the home is a confidential OpenID Connect client of the tenant. Register an app in the tenant with the redirect URI
/// <c>&lt;home&gt;/identity/upstream/entra/callback</c> and the <c>groups</c> claim in its id token.
/// </summary>
/// <param name="Authority">The tenant's issuer, exactly as its discovery document gives it: <c>https://login.microsoftonline.com/&lt;tenant id&gt;/v2.0</c> (the tenant's id, not its domain name, since the id is what the tokens name).</param>
/// <param name="ClientId">The id of the app registered in the tenant for the home.</param>
/// <param name="ClientSecret">That app's client secret.</param>
/// <param name="TrustEmail">Whether the tenant's <c>email</c> claim says whose an address is, so it finds the account that holds it (plan 13a U16). Only for a tenant whose addresses its own administrators set.</param>
/// <param name="Label">The button on the home's sign-in page.</param>
/// <param name="Http">The client the home reaches the tenant with; null: a plain one.</param>
public sealed record EntraUpstream(string Authority, string ClientId, string? ClientSecret, bool TrustEmail = false, string Label = "Sign in with Microsoft Entra ID", Func<HttpClient>? Http = null);

/// <summary>
/// Signing in at the home with the person's Windows account (Negotiate: Kerberos, or NTLM on a Windows host; on Linux, Kerberos with the home's keytab in <c>KRB5_KTNAME</c>). The account's SID is the credential, its user principal
/// name the account key, and its groups (SIDs, and on Windows their <c>DOMAIN\group</c> names too) are what the home's Windows group mappings match.
/// </summary>
/// <param name="Label">The button on the home's sign-in page.</param>
public sealed record WindowsUpstream(string Label = "Sign in with Windows");

/// <summary>The home's endpoints for the upstream sign-ins, and turning what each one says into the home's user (<see cref="ExternalSignIns"/>) and a session at the home.</summary>
public static class UpstreamSignIn
{
    /// <summary>Starts an Entra sign-in: the browser is sent to the tenant. <c>returnUrl</c> (an address under <c>/identity/</c>) is where it ends up.</summary>
    public const string EntraPath = "/identity/upstream/entra";

    /// <summary>Where the tenant sends the browser back: the redirect URI to register in the tenant (under the home's public address).</summary>
    public const string EntraCallbackPath = "/identity/upstream/entra/callback";

    /// <summary>Negotiates the person's Windows account and signs them in. A browser navigates here (the button), so it is a GET.</summary>
    public const string WindowsPath = "/identity/upstream/windows";

    /// <summary>The Entra client of the home, which remembers the sign-ins it has started in the state until the browser comes back.</summary>
    internal sealed class EntraClient(OpenIdClient client)
    {
        public OpenIdClient Client { get; } = client;
    }

    internal static void Register(IServiceCollection services, IdentityHomeOptions options)
    {
        services.AddSingleton(sp => new ExternalSignIns(sp.GetRequiredService<IUserAccounts>(), sp.GetRequiredService<HomeDirectory>()));
        if (options.Entra is { } entra)
        {
            var redirect = new Uri(options.PublicUrl, EntraCallbackPath).AbsoluteUri;
            // the attempts are in the state, so the tenant may send the browser back to any instance of the home's group
            services.AddSingleton(sp => new EntraClient(new OpenIdClient(new OpenIdClientOptions(entra.Authority, entra.ClientId, entra.ClientSecret, redirect), entra.Http?.Invoke() ?? new HttpClient(),
                sp.GetService<TimeProvider>(), new StateOpenIdAttempts(sp.GetRequiredService<IOneTimeValues>(), "upstream:entra"))));
        }
    }

    internal static void Map(WebApplication app, RouteGroupBuilder open, IdentityHomeOptions options)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataKit.Identity.Upstream");
        if (options.Entra is { } entra)
        {
            var client = app.Services.GetRequiredService<EntraClient>().Client;
            open.MapGet(EntraPath, async (HttpContext context, string? returnUrl) =>
            {
                try { return Results.Redirect((await client.BeginAsync(SafeReturn(returnUrl), context.RequestAborted)).AbsoluteUri); }
                catch (OpenIdClientException ex) { return Refused(options, log, $"Microsoft Entra ID could not be reached: {ex.Message}"); }
            });
            open.MapGet(EntraCallbackPath, async (HttpContext context, string? state, string? code, string? error, string? error_description) =>
            {
                HomeSignIn signedIn;
                try { signedIn = await client.CompleteAsync(state, code, error, context.RequestAborted); }
                catch (OpenIdClientException ex) { return Refused(options, log, $"Microsoft Entra ID did not sign you in. {ex.Message}"); }
                return Finish(context, options, log, ExternalSignIns.FromOpenId(ExternalSources.Entra, signedIn.Identity, entra.TrustEmail), SafeReturn(signedIn.ReturnUrl));
            });
        }
        if (options.Windows is not null)
        {
            // not in the open group: its policy is the one that runs Negotiate, and an open group's AllowAnonymous would skip it
            app.MapGet(WindowsPath, (HttpContext context, string? returnUrl) =>
                FromNegotiate(context.User) is { } identity
                    ? Finish(context, options, log, identity, SafeReturn(returnUrl))
                    : Refused(options, log, "Windows did not say who you are."))
                .RequireAuthorization(new AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());
        }
    }

    /// <summary>
    /// What a negotiated account says about the person: the SID (else, on Linux, the Kerberos principal name) as the subject, the user principal name the domain issued as the address, and the groups. Null when the principal is
    /// not authenticated.
    /// </summary>
    public static ExternalIdentity? FromNegotiate(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } identity) return null;
        var subject = principal.FindFirst(ClaimTypes.PrimarySid)?.Value ?? identity.Name;
        if (string.IsNullOrWhiteSpace(subject)) return null;
        // the UPN is the domain's own claim in the ticket, nothing the browser sent, so it is trusted as the person's (as the console's Windows sign-in does); a name that is not an address is no key
        var upn = principal.FindFirst(ClaimTypes.Upn)?.Value;
        if (AccountKey.Normalize(upn) is null) upn = null;
        var groups = new List<string>();
        groups.AddRange(principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value));
        // on Linux, the groups an LDAP lookup gave the Negotiate handler arrive as role claims of their names
        groups.AddRange(principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
        if (OperatingSystem.IsWindows()) groups.AddRange(NamesOf(principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value)));
        return new ExternalIdentity(ExternalSources.Windows, subject, identity.Name, upn, upn is not null, [.. groups.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The <c>DOMAIN\group</c> names of group SIDs, so a mapping may name a group either way; a SID the machine cannot name is left out.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> NamesOf(IEnumerable<string> sids)
    {
        foreach (var sid in sids)
        {
            string? name;
            try { name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { name = null; }
            if (name is not null) yield return name;
        }
    }

    /// <summary>The home's user for the person, a session at the home, and the browser back to the sign-in page, which goes on to where it was going (a navigation of the home's own, so its strict cookie goes along).</summary>
    private static IResult Finish(HttpContext context, IdentityHomeOptions options, ILogger log, ExternalIdentity identity, string? returnUrl)
    {
        UserRecord user;
        try { user = context.RequestServices.GetRequiredService<ExternalSignIns>().SignIn(identity); }
        catch (AccountConflictException ex) { return Refused(options, log, ex.Message); }
        if (!user.Enabled) return Refused(options, log, $"'{user.DisplayName}' is disabled at the identity home.");

        var auth = context.RequestServices.GetRequiredService<AuthOptions>();
        var session = context.RequestServices.GetRequiredService<ISessionStore>().Create(user.Id);
        context.Response.Cookies.Append(auth.CookieName, session.Id, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Expires = session.ExpiresAtUtc, Path = "/",
        });
        log.LogInformation("{User} signed in at the identity home through {Source}.", user.Id, identity.Source);
        return Results.Redirect(IdentityHome.SignInPath + (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
    }

    /// <summary>The sign-in page again, saying why; nobody is signed in.</summary>
    private static IResult Refused(IdentityHomeOptions options, ILogger log, string message)
    {
        log.LogWarning("An upstream sign-in at the identity home was refused: {Message}", message);
        return Results.Content(IdentityHomePages.SignIn(options, message), "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Only an address under <c>/identity/</c> of the home is returned to (the provider's authorize request); anything else is dropped.</summary>
    internal static string? SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 } r && r.StartsWith("/identity/", StringComparison.Ordinal) && !r.StartsWith("//", StringComparison.Ordinal) && !r.Contains('\\') ? r : null;
}
