using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// Every listener takes one <c>listen</c> setting (an address, or a list of them), and the settings <c>port</c> and <c>address</c> of a listener are gone, with no alias: a configuration that still has one (<c>listen: { address, port }</c>,
/// <c>management: { port }</c>, <c>apps: { port }</c>, <c>public: { port }</c>, <c>rest: { address }</c> ...) is refused naming the file and the setting, never read in part.
/// </summary>
internal static class GatewayRetiredListeners
{
    private static readonly (string Section, string Replacement)[] Sections =
    [
        ("Listen", "listen: https://0.0.0.0:<port>"),
        ("Management", "management: { listen: https://127.0.0.1:<port> }"),
        ("Public", "public: { listen: https://0.0.0.0:<port> }"),
        ("Identity", "identity: { listen: https://0.0.0.0:<port> }"),
        ("Apps", "apps: { listen: https://0.0.0.0:<port> }"),
        ("Store", "store: { listen: https://0.0.0.0:<port> }"),
        ("Views", "views: { listen: https://0.0.0.0:<port> }"),
        ("Mcp", "mcp: { listen: https://0.0.0.0:<port> }"),
        ("Rest", "rest: { listen: https://0.0.0.0:<port> }"),
    ];

    /// <summary>The first <c>port</c> or <c>address</c> of a listener the configuration has, as a diagnostic; null when there is none.</summary>
    public static ConfigDiagnostic? Find(IConfigurationRoot configuration)
    {
        foreach (var (section, replacement) in Sections)
            foreach (var name in new[] { "Port", "Address" })
            {
                var key = $"{GatewayOptions.SectionName}:{section}:{name}";
                foreach (var provider in configuration.Providers.Reverse())
                {
                    if (provider is GatewayConfigDirectoryProvider directory)
                    {
                        var hit = directory.Origins.Where(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)).Select(o => (o.Key, o.Value)).FirstOrDefault();
                        if (hit.Value is { } origin) return Refuse(origin.File, origin.KeyLine, origin.KeyColumn, section, name, replacement);
                        continue;
                    }
                    if (provider.TryGet(key, out var value) && !string.IsNullOrEmpty(value))
                        return Refuse(WhereWritten(provider), 0, 0, section, name, replacement);
                }
            }
        return null;
    }

    private static string WhereWritten(IConfigurationProvider provider) =>
        provider is FileConfigurationProvider file && file.Source.Path is { Length: > 0 } path ? path
        : provider is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider ? "the environment variables"
        : provider is Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider ? "the command line"
        : "the configuration";

    private static ConfigDiagnostic Refuse(string file, int line, int column, string section, string name, string replacement) =>
        GatewayConfig.Catalog.Create(SharedCodes.InvalidValue, file, line, column,
            $"`{section}:{name}` is gone: a listener takes one `listen` setting, an address with its scheme and port, or a list of addresses.",
            $"Write `{replacement}` (a list is `[https://0.0.0.0:<port>, https://127.0.0.1:<port>]`) in place of `port` and `address`.",
            keyPath: $"{section}.{name}");
}
