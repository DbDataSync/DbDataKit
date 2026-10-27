using DbDataKit.Serving;
using DbDataProxy.Server.Auth;

namespace DbDataProxy.Server.Serving;

/// <summary>A caller of the serving layer as Proxy knows them: the principal says which data sources they may use and carries the database credentials of a pass-through caller.</summary>
public sealed record ProxyCaller(ProxyPrincipal Principal)
    : ServingCaller(Principal.Name, Principal.Name, new HashSet<string>(), new Dictionary<string, string> { ["method"] = Principal.Method });
