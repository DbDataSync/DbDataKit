using DbDataKit.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// The Gateway's <c>state:</c> section, read through the shared <see cref="StateSettingsReader"/> like every product's (plan 29b C07): <c>engine</c>, <c>connection</c>, <c>registry</c>, <c>namespaces</c> and <c>retention</c> in <c>server.yaml</c>. The Gateway's own
/// layers decide which value wins, as they always have (a file in the configuration folder, then <c>DBGATEWAY_</c> and plain environment variables, then the command line), and the result says which layer gave the engine and the connection.
/// The keys of the earlier release (<c>connectionString</c> and <c>definitionPollSeconds</c> under <c>state:</c>) are refused with a message that names the file and shows the new shape.
/// </summary>
public static class GatewayStateConfig
{
    private static readonly string[] Canonical = ["engine", "connection", "registry", "namespaces", "retention", "days", "modules", "tables", "schema", "prefix", "file"];

    /// <summary>The state settings and where each came from; raises <see cref="GatewayConfigException"/> naming the file (or variable) for an old key or a setting that is not one.</summary>
    public static ResolvedState Resolve(IConfigurationRoot configuration)
    {
        if (OldKey(configuration) is { } old) throw new GatewayConfigException(old.Message, []);
        var section = configuration.GetSection($"{GatewayOptions.SectionName}:State");
        var raw = Dictionary(section);
        StateSettings settings;
        try { settings = StateSettingsReader.Read(raw.Count == 0 ? null : raw, "state"); }
        catch (InvalidOperationException ex) { throw new GatewayConfigException(Located(configuration, section, raw, ex.Message), []); }

        StateSource Source(string key, params string[] more) => Where(configuration, key, more);
        return new ResolvedState(settings, new StateSources(
            Source("Engine"), Source("Connection"), Source("Namespaces", "Registry"), Source("Retention")));
    }

    /// <summary>The message for a key of the earlier release, or null when there is none; <c>Line</c> and <c>File</c> say where it was written when a file did.</summary>
    internal static (string Message, string? File, int Line, int Column)? OldKey(IConfigurationRoot configuration)
    {
        foreach (var child in configuration.GetSection($"{GatewayOptions.SectionName}:State").GetChildren())
        {
            var key = child.Key.Replace("_", "").Replace("-", "").ToLowerInvariant();
            if (key is not ("connectionstring" or "definitionpollseconds")) continue;
            var (file, line, column) = FileOf(configuration, child.Path);
            var where = file is null ? $"the setting {child.Path} ({Layer(configuration, child.Path)})" : $"{file}:{line}";
            var message = key == "connectionstring"
                ? $"{where}: state.{child.Key} is the key of an earlier release and is no longer read; the gateway's state settings are the section every DbData product shares. Replace it with:\n" +
                  "state:\n  engine: postgres            # or mssql; sqlite is the default\n  connection: Host=db;Database=dbdatagateway_state;Username=gateway;Password=secret:state-db   # what state.connectionString held\n" +
                  "(namespaces and retention are optional; 'dbdatagateway state init --topology service|group|install' writes the section.)"
                : $"{where}: state.{child.Key} moved out of the state section, which is now the one every DbData product shares (engine, connection, namespaces, retention). Write it at the top level of the file as definitionPollSeconds: <seconds>.";
            return (message, file, line, column);
        }

        return null;
    }

    private static Dictionary<string, object?> Dictionary(IConfigurationSection section, params string[] above)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        // module and table names (the keys under namespaces, retention.modules and retention.tables) are the operator's, not settings: they keep their spelling
        var names = above is ["namespaces"] or ["retention", "modules"] or ["retention", "tables"];
        foreach (var child in section.GetChildren())
        {
            var name = names ? child.Key : Name(child.Key);
            result[name] = child.GetChildren().Any() ? Dictionary(child, [.. above, name]) : child.Value;
        }

        return result;
    }

    /// <summary>The setting a key is, matched without regard to case (the Gateway's configuration is case-insensitive); anything else is left as written for the reader to refuse.</summary>
    private static string Name(string key) => Canonical.FirstOrDefault(c => string.Equals(c, key, StringComparison.OrdinalIgnoreCase)) ?? key;

    private static string Located(IConfigurationRoot configuration, IConfigurationSection section, Dictionary<string, object?> raw, string message)
    {
        // the message starts with the path of the key (state.engin ...): find where that key was written
        var path = message.Split(' ', 2)[0].TrimEnd(':');
        var segments = path.Split('.').Skip(1).ToList();
        var key = $"{section.Path}";
        var current = section;
        foreach (var segment in segments)
        {
            var next = current.GetChildren().FirstOrDefault(c => string.Equals(c.Key, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null) break;
            current = next;
            key = next.Path;
        }

        var (file, line, _) = FileOf(configuration, key);
        return file is not null ? $"{file}:{line}: {message}" : $"{message} (written in {Layer(configuration, key)})";
    }

    private static (string? File, int Line, int Column) FileOf(IConfigurationRoot configuration, string key)
    {
        foreach (var provider in configuration.Providers.Reverse())
        {
            if (provider is GatewayConfigDirectoryProvider directory)
            {
                var found = directory.Origins.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
                if (found.Value is { } origin) return (origin.File, origin.KeyLine, origin.KeyColumn);
                continue;
            }

            if (provider.TryGet(key, out _) || HasChild(provider, key)) return (null, 0, 0);
        }

        return (null, 0, 0);
    }

    private static bool HasChild(IConfigurationProvider provider, string key) => provider.GetChildKeys([], key).Any();

    /// <summary>Where the value of a key is read from: the layer that wins for it.</summary>
    private static string Layer(IConfigurationRoot configuration, string key)
    {
        var source = WhereExact(configuration, key);
        return source.Kind == StateSourceKind.Default ? "the configuration" : source.ToString();
    }

    private static StateSource Where(IConfigurationRoot configuration, string name, string[] more)
    {
        foreach (var candidate in new[] { name }.Concat(more))
        {
            var found = WhereExact(configuration, $"{GatewayOptions.SectionName}:State:{candidate}");
            if (found.Kind != StateSourceKind.Default) return found;
        }

        return StateSource.Default;
    }

    private static StateSource WhereExact(IConfigurationRoot configuration, string key)
    {
        foreach (var provider in configuration.Providers.Reverse())
        {
            switch (provider)
            {
                case GatewayConfigDirectoryProvider directory:
                    if (directory.Origins.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)) is { Value: { } origin } && !string.IsNullOrEmpty(origin.File))
                        return new StateSource(StateSourceKind.File, origin.File);
                    break;
                case EnvironmentVariablesConfigurationProvider:
                    if (provider.TryGet(key, out var value) && !string.IsNullOrEmpty(value)) return new StateSource(StateSourceKind.Environment, EnvironmentName(key, value));
                    break;
                case CommandLineConfigurationProvider:
                    if (provider.TryGet(key, out var arg) && !string.IsNullOrEmpty(arg)) return new StateSource(StateSourceKind.Flag, $"--{key}");
                    break;
                default:
                    if (provider.TryGet(key, out var other) && !string.IsNullOrEmpty(other)) return new StateSource(StateSourceKind.File, provider.GetType().Name);
                    break;
            }
        }

        return StateSource.Default;
    }

    private static string EnvironmentName(string key, string value)
    {
        var flat = key.Replace(":", "__");
        foreach (var candidate in new[] { "DBGATEWAY_" + flat, flat })
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                if (e.Key is string name && string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Value as string, value, StringComparison.Ordinal)) return name;
        return "DBGATEWAY_" + flat;
    }
}
