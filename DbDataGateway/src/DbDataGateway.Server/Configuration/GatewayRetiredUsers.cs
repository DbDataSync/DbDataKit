using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// Gateway's own users are retired (plan 16, "Sign-in decisions" item 2; plan 13a P13): people sign in with the identity home (a home token, their home user name with a personal access token, or Windows resolved to the home's account by UPN), and
/// the home's groups, named in the data source files, decide which data sources they may use. A configuration that still lists them (<c>Gateway:Users</c>, <c>gateway/users.yaml</c>, a Windows <c>AuthType</c> entry) is refused naming the file and the key:
/// a clean break, never a silent one.
/// </summary>
internal static class GatewayRetiredUsers
{
    /// <summary>The first place the configuration lists a Gateway user, as a diagnostic; null when it lists none (an empty <c>users: []</c> lists none).</summary>
    public static ConfigDiagnostic? Find(IConfigurationRoot configuration)
    {
        var key = $"{GatewayOptions.SectionName}:Users";
        foreach (var provider in configuration.Providers.Reverse())
        {
            if (provider is GatewayConfigDirectoryProvider directory)
            {
                var first = directory.Origins.Where(o => o.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).OrderBy(o => o.Value.KeyLine).FirstOrDefault();
                if (first.Value is { } origin) return Refuse(origin.File, origin.KeyLine, origin.KeyColumn, first.Key);
                continue;
            }
            var children = provider.GetChildKeys([], key).ToList();
            if (children.Count > 0 || provider.TryGet(key, out var value) && !string.IsNullOrEmpty(value))
                return Refuse(WhereWritten(provider), 0, 0, key + (children.Count > 0 ? ":" + children[0] : ""));
        }
        return null;
    }

    private static string WhereWritten(IConfigurationProvider provider) =>
        provider is FileConfigurationProvider file && file.Source.Path is { Length: > 0 } path ? path
        : provider is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider ? "the environment variables"
        : provider is Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider ? "the command line"
        : "the configuration";

    private static ConfigDiagnostic Refuse(string file, int line, int column, string written) =>
        GatewayConfig.Catalog.Create(SharedCodes.InvalidValue, file, line, column,
            $"`{GatewayOptions.SectionName}:Users` is retired: Gateway no longer has users of its own, and Windows accounts are not listed either (the entry is `{DisplayKey(written)}`).",
            "People sign in with the identity home: a home token, their home user name with a personal access token, or Windows (set `auth: windows: true`) resolved to the home's account by its address. Remove the users, " +
            "and name the home groups that may use each data source with `extensions.gateway.groups` (or `everyone: true`) in its file under connections/. Database credentials that connect through (`pass_through`) are unchanged.",
            keyPath: "Users");

    private static string DisplayKey(string key) => key.Length > 80 ? key[..80] : key;
}
