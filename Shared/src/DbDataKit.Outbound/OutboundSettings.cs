using DbDataKit.Config.Yaml;

namespace DbDataKit.Outbound;

/// <summary>
/// The <c>outbound:</c> setting of a service's settings file (<c>settings.yaml</c> of the console, <c>server.yaml</c> of the others): one list of the destinations the service may connect to.
/// <code>
/// outbound:
///   - https://api.nuget.org
///   - https://*.githubusercontent.com
///   - ssh://github.com
/// </code>
/// Without the setting the service connects anywhere; <c>outbound: []</c> allows nothing. The line with no value is refused, so a list that was emptied by mistake does not open or close everything silently.
/// </summary>
public static class OutboundSettings
{
    public const string Key = "outbound";

    /// <summary>The setting of a settings file; <see cref="OutboundList.Unrestricted"/> when the file or the line is absent. A file that is not YAML, a value that is not a list of text entries and an entry that is not valid fail naming the file.</summary>
    public static OutboundList Load(string path)
    {
        if (!File.Exists(path)) return OutboundList.Unrestricted;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics) as YamlMapping;
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error)
            throw new OutboundSettingsException($"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        return Read(root?.Get(Key), path);
    }

    /// <summary>Reads the value of the key; <paramref name="source"/> is the file it came from, named in every refusal. Null is <see cref="OutboundList.Unrestricted"/>.</summary>
    public static OutboundList Read(YamlNode? node, string source)
    {
        if (node is null) return OutboundList.Unrestricted;
        if (node is YamlScalar { Value.Length: 0, Quoted: false })
            throw new OutboundSettingsException($"{source}: `{Key}` has no value. Write a list of destinations, `{Key}: []` for none, or remove the line to allow every destination.");
        if (node is not YamlSequence sequence)
            throw new OutboundSettingsException($"{source}: `{Key}` (line {node.Line}) must be a list of destinations, one per line, like `- https://api.nuget.org`.");
        var items = new List<string>();
        foreach (var item in sequence.Items)
            items.Add(item is YamlScalar { Value.Length: > 0 } scalar ? scalar.Value : throw new OutboundSettingsException($"{source}: `{Key}` (line {item.Line}) holds an entry that is not text."));
        return OutboundList.Parse(items, source);
    }
}
