using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Data;

/// <summary>
/// <see cref="DataSourceOptions"/> stays the Proxy's configuration shape (PascalCase keys, nothing renamed); this is the view of it as the shared
/// <see cref="ConnectionDefinition"/> (schema <c>connection/1</c>). What the shared type has fields for (engine, access, TLS, timeouts) goes into
/// those fields; what only the Proxy knows (bitness, pass-through, metadata mode, batch size, classifier, limits) goes into <c>extensions.proxy</c>.
/// </summary>
public static class DataSourceDefinitions
{
    public const string ExtensionKey = "proxy";

    /// <summary>The definition for <paramref name="options"/>; <paramref name="connectionString"/> is the string after secret resolution and credential injection.</summary>
    public static ConnectionDefinition Build(string name, DataSourceOptions options, string connectionString) =>
        SourceConnection.BuildDefinition(
            name, options.Provider.ToLowerInvariant(), connectionString, readOnly: !options.AllowWrites, options.CommandTimeoutSeconds, Extension(options));

    /// <summary><c>extensions.proxy</c> for a data source (snake_case keys, like the rest of <c>connection/1</c>).</summary>
    public static YamlMapping Extension(DataSourceOptions o)
    {
        var entries = new List<YamlEntry>
        {
            Entry("bitness", o.Bitness.ToLowerInvariant()),
            Entry("pass_through", o.PassThrough),
            Entry("allow_writes", o.AllowWrites),
            Entry("classifier", o.Classifier.ToString().ToLowerInvariant()),
            Entry("metadata", o.Metadata.ToLowerInvariant()),
            Entry("batch_size", o.BatchSize),
        };
        return new YamlMapping(entries, 0, 0);
    }

    private static YamlEntry Entry(string key, object value) =>
        new(new YamlScalar(key, 0, 0), new YamlScalar(value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var other => other.ToString() ?? "",
        }, 0, 0));

    /// <summary>Reads a value back from <c>extensions.proxy</c> (null when absent). The Proxy never needs it at run time; the tests use it to prove nothing is lost.</summary>
    public static string? ExtensionValue(ConnectionDefinition definition, string key) =>
        definition.Extensions.Items.TryGetValue(ExtensionKey, out var node) && node is YamlMapping map && map.Get(key) is YamlScalar scalar ? scalar.Value : null;
}
