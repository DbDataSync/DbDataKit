using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataGateway.Server.Management;

/// <summary>
/// What replacing one data source file by another changes, by field (plan 13c D19, owner decision of 2026-11-09): a publisher that adopts a data source made by hand, or republishes one, gets this in the answer, so a
/// replacement never drops anything silently. Each field is a dotted path of the file (<c>extensions.gateway.batch_size</c>); a list is one value (<c>[a, b]</c>). The connection itself (address, login, secret references)
/// is named as changed and its values are not repeated: they are not policy, and the answer goes to whoever published.
/// </summary>
internal static class DefinitionChanges
{
    private static readonly string[] Hidden = ["connection_string", "connection_string_secret", "address", "auth", "tls"];

    /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>: <c>added</c>, <c>changed</c> and <c>dropped</c> fields, in document order of the old file then the new one.</summary>
    public static JsonArray Between(string before, string after)
    {
        var changes = new JsonArray();
        if (Flatten(before) is not { } oldFields || Flatten(after) is not { } newFields) return changes;
        var was = oldFields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var now = newFields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        foreach (var (field, value) in oldFields)
        {
            if (!now.TryGetValue(field, out var next)) changes.Add(Change(field, "dropped", value, null));
            else if (next != value) changes.Add(Change(field, "changed", value, next));
        }
        foreach (var (field, value) in newFields)
            if (!was.ContainsKey(field)) changes.Add(Change(field, "added", null, value));
        return changes;
    }

    private static JsonObject Change(string field, string kind, string? before, string? after)
    {
        var hidden = Hidden.Any(h => field == h || field.StartsWith(h + ".", StringComparison.Ordinal));
        return new JsonObject { ["field"] = field, ["change"] = kind, ["before"] = hidden ? null : before, ["after"] = hidden ? null : after };
    }

    /// <summary>The fields a file says, in order; null when the text is not a YAML mapping.</summary>
    internal static List<KeyValuePair<string, string>>? Flatten(string text)
    {
        var diags = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(text, "x", diags) is not YamlMapping root || diags.Any(d => d.Severity == ConfigSeverity.Error)) return null;
        var fields = new List<KeyValuePair<string, string>>();
        Walk(root, "");
        return fields;

        void Walk(YamlNode node, string path)
        {
            switch (node)
            {
                case YamlMapping map:
                    foreach (var entry in map.Entries) Walk(entry.Value, path.Length == 0 ? entry.Key.Value : path + "." + entry.Key.Value);
                    break;
                case YamlSequence list when list.Items.All(i => i is YamlScalar):
                    fields.Add(new(path, DbDataKit.Connections.ServingPolicy.Describe(list)));
                    break;
                case YamlSequence list:
                    for (var i = 0; i < list.Items.Count; i++) Walk(list.Items[i], $"{path}[{i}]");
                    break;
                case YamlScalar scalar:
                    fields.Add(new(path, scalar.Value));
                    break;
            }
        }
    }
}
