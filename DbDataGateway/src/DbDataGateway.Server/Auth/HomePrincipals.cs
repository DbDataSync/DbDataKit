using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Auth;

/// <summary>
/// The principal of an identity home user (plan 13a P13). Which data sources a home user may use is decided by the home groups the data source files name (<see cref="DataSourceOptions.Groups"/>, <see cref="DataSourceOptions.Everyone"/>) and the
/// home's groups of the person at the time of the sign-in; the data source catalog checks the groups again at each use, against the configuration then in force.
/// </summary>
public static class HomePrincipals
{
    /// <summary>A personal access token is <c>ddk_&lt;id&gt;_&lt;secret&gt;</c>.</summary>
    public static bool LooksLikeToken(string text) => text.StartsWith("ddk_", StringComparison.Ordinal);

    public static GatewayPrincipal Create(StoreCallers callers, UserRecord user, string method, GatewayOptions options)
    {
        var groups = callers.GroupsOf(user.Id);
        var sources = options.DataSources.Where(d => DataSourceAccess.HomeMayUse(d.Value, groups)).Select(d => d.Key);
        return new GatewayPrincipal(user.DisplayName, method, sources, defaultDataSource: null, credentials: null, homeUserId: user.Id, homeGroups: groups);
    }
}

/// <summary>The rule for a home user and a data source, and the sentence that says why not.</summary>
public static class DataSourceAccess
{
    public static bool HomeMayUse(DataSourceOptions source, IEnumerable<string> groups) =>
        source.Everyone || source.Groups.Any(named => groups.Contains(named, StringComparer.OrdinalIgnoreCase));

    public static string Refusal(string name, DataSourceOptions source, string person) =>
        source.Everyone || source.Groups.Count > 0
            ? $"Not authorized for data source '{name}': it is for the home groups {string.Join(", ", source.Groups)}, and {person} is in none of them."
            : $"Not authorized for data source '{name}': it names no home group and is not open to everyone, so no home user may use it (a published definition lists the groups under `grants`, and a Build project publishes it again; an administrator lists them under extensions.gateway.groups in a file made by hand).";
}
