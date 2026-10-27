using DbDataKit.Serving;
using DbDataKit.Dialect;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Serving;

/// <summary>
/// Proxy's side of the serving contract (plan 20, section 10; plan 19, N02): the core services the Flight SQL module runs on. Identity is the chain of credential validators and the session store, a data source is what a statement runs against,
/// and the catalog is what the data source's metadata provider reports. The protocol knows none of this; it asks for a caller, a session, an answer or a list of objects and gets a serving exception when the answer is no.
/// </summary>
public sealed partial class ProxyServing(
    DataSourceCatalog sources,
    ChainedCredentialValidator validators,
    SessionStore sessions,
    IJwtValidator jwt,
    IOptionsMonitor<ProxyOptions> options,
    ILogger<ProxyServing> log,
    TimeProvider time,
    ProxyEvents? events = null) : IServingServices, IServingIdentity, IServingSessions
{
    public IServingIdentity Identity => this;

    public IServingSessions Sessions => this;

    public IServingQueries Queries => queries;

    public IServingCatalog Catalog => catalog;

    public IServingEntities? Entities => null;

    public IServingUpdates? Updates => queries;

    private readonly QueryService queries = new(sources, log, events);
    private readonly CatalogService catalog = new(sources, log);

    public async ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, string? clientAddress = null, CancellationToken cancellationToken = default)
    {
        switch (credentials)
        {
            case ServingCredentials.Password(var user, var password):
                var principal = await validators.ValidateAsync(user, password, cancellationToken, clientAddress ?? "").ConfigureAwait(false);
                return principal is null ? null : new ProxyCaller(principal);
            case ServingCredentials.Bearer(var token):
                if (sessions.TryGet(token) is { } session) return new ProxyCaller(session);
                // not one of our session tokens: a JWT from an external identity provider, if that is enabled
                return jwt is { Enabled: true } && token.Count(c => c == '.') == 2 && await jwt.ValidateAsync(token, cancellationToken).ConfigureAwait(false) is { } external
                    ? new ProxyCaller(external)
                    : null;
            default:
                return null;
        }
    }

    // a session is the bearer token the session store issued; the principal stays in memory, so pass-through database credentials never leave the server inside a token
    public Task<ServingSession> OpenAsync(ServingCaller caller, string protocol, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        var principal = PrincipalOf(caller);
        var token = sessions.Issue(principal);
        return Task.FromResult(new ServingSession(token, protocol, caller.Id, [], time.GetUtcNow().AddHours(options.CurrentValue.Auth.SessionMaxHours)));
    }

    public Task<ServingSession?> GetAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<ServingSession?>(sessions.TryGet(id) is { } p && p.Name == caller.Name
            ? new ServingSession(id, "flightsql", caller.Id, [], time.GetUtcNow().AddHours(options.CurrentValue.Auth.SessionMaxHours))
            : null);

    // the adapters of this host keep their state themselves (prepared statements, pending answers); nothing is stored in the session
    public Task SaveStateAsync(ServingCaller caller, string id, byte[] state, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CloseAsync(ServingCaller caller, string id, CancellationToken cancellationToken = default)
    {
        sessions.Revoke(id);
        return Task.CompletedTask;
    }

    internal static ProxyPrincipal PrincipalOf(ServingCaller caller) =>
        caller is ProxyCaller proxy ? proxy.Principal : throw new ServingUnauthenticatedException("The caller was not identified by this server.");
}
