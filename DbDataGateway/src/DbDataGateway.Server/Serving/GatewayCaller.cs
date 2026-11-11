using DbDataKit.Serving;
using DbDataGateway.Server.Auth;

namespace DbDataGateway.Server.Serving;

/// <summary>A caller of the serving layer as Gateway knows them (the groups of the identity home are the roles, so what is shared to a group, such as a saved query, reaches its members on every path): the principal says which data sources they may use and carries the database credentials of a pass-through caller.</summary>
public sealed record GatewayCaller(GatewayPrincipal Principal)
    : ServingCaller(Principal.HomeUserId ?? Principal.Name, Principal.Name, new HashSet<string>(Principal.HomeGroups, StringComparer.OrdinalIgnoreCase), new Dictionary<string, string> { ["method"] = Principal.Method });
