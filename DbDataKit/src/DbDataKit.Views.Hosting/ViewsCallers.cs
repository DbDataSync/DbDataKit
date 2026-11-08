using DbDataKit.Serving;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// What an app's token narrows a caller to (plan 32 section 5 item 1: the apps host calls the module with the app's scoped token, "which narrows"): the app instance, and the semantic models its approved manifest names
/// (<c>semantic: [sales]</c>). A document that queries a model outside the list is not opened or rendered for the app, whatever the person behind it may do; the person's rows are still the serving core's to decide.
/// </summary>
public sealed record ViewsAppScope(string Instance, IReadOnlyCollection<string> Models)
{
    public bool Allows(string model) => Models.Contains(model, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Who is calling the module: the serving core's <see cref="ServingCaller"/> (identity, roles, attributes: what every request runs under) and, for an app's token, the narrowing. <see cref="Caller"/> is what the access rules key on;
/// a narrowed principal's caller carries the claim <c>app</c> (the instance) added by the module, so a cached result is never shared between the person's own session and the same person inside an app.
/// </summary>
public sealed record ViewsPrincipal(ServingCaller Caller, ViewsAppScope? App = null);

/// <summary>
/// Turns what a request carries into a principal: the same tokens as the rest of the serving layer (a console token, a personal access token, an access token of the identity home, an app's token). The host that mounts the
/// module supplies it (the Proxy's <c>HomeCallers</c> is the one for the identity home); the module checks nothing itself and trusts nothing but what this returns.
/// </summary>
public interface IViewsCallers
{
    /// <param name="authorization">The <c>Authorization</c> header as sent, or null.</param>
    /// <param name="clientAddress">Where the call came from, for the host's login throttle.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>The principal, or null when the credentials identify nobody (the module answers 401 and says nothing of why).</returns>
    ValueTask<ViewsPrincipal?> AuthenticateAsync(string? authorization, string? clientAddress, CancellationToken cancellationToken);
}
