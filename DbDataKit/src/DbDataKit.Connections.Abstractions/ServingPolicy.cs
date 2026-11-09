using System.Globalization;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections;

/// <summary>
/// The Gateway's serving policy as part of a published definition (plan 13c D19, owner decision of 2026-11-09 and its correction the same day). A connection file of a Build project may carry the policy under <c>extensions.gateway</c>,
/// the block a data source file made by hand has; a publish sends it and the Gateway replaces its copy completely (no merge), as it does the grants. Two fields are <see cref="Blocked"/> and not part of any definition:
/// <c>allow_writes</c> and <c>allowed_directories</c> (the trusted directories); a definition that carries either is refused naming the field and its line. A publish replaces the whole file on the Gateway, those two fields
/// included; editing a published data source on the Gateway is not supported and nothing guards against it. Who may use the data source is <c>grants</c>, not <c>extensions.gateway.groups</c>/<c>everyone</c>.
/// </summary>
public static class ServingPolicy
{
    /// <summary>The key under <c>extensions</c> that holds the Gateway's policy.</summary>
    public const string Key = "gateway";

    /// <summary>What no definition carries and no Gateway screen or API edits: only an administrator, in the file on the Gateway's disk.</summary>
    public static IReadOnlyList<string> Blocked { get; } = ["allow_writes", "allowed_directories"];

    /// <summary>Who may use the data source: the definition's <c>grants</c>, never these keys of the policy block.</summary>
    public static IReadOnlyList<string> GrantKeys { get; } = ["groups", "everyone"];

    private static readonly string[] BoolFields = ["pass_through", "default"];
    private static readonly string[] IntFields = ["max_concurrent_queries", "max_concurrent_queries_per_user", "acquire_timeout_seconds", "batch_size"];

    /// <summary>The policy fields a definition may carry, in the order the editors show them.</summary>
    public static IReadOnlyList<string> Published { get; } =
        ["pass_through", "bitness", "classifier", "metadata", "default", "max_concurrent_queries", "max_concurrent_queries_per_user", "acquire_timeout_seconds", "batch_size", "compression"];

    /// <summary>What is wrong with one field of a policy block: the field (dotted from <c>extensions.gateway</c>), where it is, and the sentences.</summary>
    public sealed record Problem(string Field, YamlNode At, bool IsBlocked, string Message, string? Fix);

    public static string BlockedSentence(string field) =>
        $"`extensions.gateway.{field}` is not something a definition carries: Build does not author or publish it, and the Gateway does not take it from a publishing client.";

    public const string BlockedFix = "Remove it from the definition. A publish replaces the whole data source file on the Gateway, so what it publishes is read-only and reads no directories outside its database.";

    /// <summary>The problems of a policy block a client would publish: a blocked field, groups or everyone (which are <c>grants</c>), a key the Gateway does not have, a value of the wrong kind.</summary>
    public static IReadOnlyList<Problem> Check(YamlMapping gateway)
    {
        var problems = new List<Problem>();
        foreach (var entry in gateway.Entries)
        {
            var key = entry.Key.Value;
            if (Blocked.Contains(key)) problems.Add(new(key, entry.Key, true, BlockedSentence(key), BlockedFix));
            else if (GrantKeys.Contains(key))
                problems.Add(new(key, entry.Key, false, $"`extensions.gateway.{key}` says who may use the data source; a definition says it in `grants`.", "Move it to `grants` (schema: connection/2)."));
            else if (!Published.Contains(key))
                problems.Add(new(key, entry.Key, false, $"`extensions.gateway.{key}` is not a serving policy field.", $"The fields are {string.Join(", ", Published)}."));
            else if (BoolFields.Contains(key) && !(entry.Value is YamlScalar { Value: var b } && bool.TryParse(b, out _)))
                problems.Add(new(key, entry.Value, false, $"`extensions.gateway.{key}` must be true or false.", null));
            else if (IntFields.Contains(key) && !(entry.Value is YamlScalar { Value: var n } && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                problems.Add(new(key, entry.Value, false, $"`extensions.gateway.{key}` must be a whole number.", null));
            else if (key is "bitness" or "classifier" or "metadata" && entry.Value is not YamlScalar)
                problems.Add(new(key, entry.Value, false, $"`extensions.gateway.{key}` must be a single value.", null));
            else if (key == "compression" && entry.Value is not YamlMapping)
                problems.Add(new(key, entry.Value, false, "`extensions.gateway.compression` must be a mapping.", null));
        }
        return problems;
    }

    /// <summary>The policy block of a definition's text (<c>extensions.gateway</c>), or null when it has none or the text is not YAML.</summary>
    public static YamlMapping? BlockOf(string text)
    {
        var diags = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(text, "x", diags) is not YamlMapping root || diags.Any(d => d.Severity == ConfigSeverity.Error)) return null;
        return (root.Get("extensions") as YamlMapping)?.Get(Key) as YamlMapping;
    }

    /// <summary>A node as one line of text, for a comparison or a screen: a scalar's value, a list as <c>[a, b]</c>.</summary>
    public static string Describe(YamlNode node) => node switch
    {
        YamlScalar s => s.Value,
        YamlSequence q => "[" + string.Join(", ", q.Items.Select(Describe)) + "]",
        YamlMapping m => "{" + string.Join(", ", m.Entries.Select(e => e.Key.Value + ": " + Describe(e.Value))) + "}",
        _ => "",
    };

    /// <summary>The policy block as block-style YAML lines under <c>extensions:</c>/<c>gateway:</c>, for the text a publish sends (two-space indentation, each line ending in a newline).</summary>
    public static string Emit(YamlMapping gateway)
    {
        var sb = new System.Text.StringBuilder("extensions:\n  gateway:\n");
        Write(gateway, 4);
        return sb.ToString();

        void Write(YamlMapping map, int indent)
        {
            foreach (var entry in map.Entries)
            {
                var pad = new string(' ', indent);
                switch (entry.Value)
                {
                    case YamlMapping inner:
                        sb.Append(pad).Append(YamlText.Scalar(entry.Key.Value)).Append(":\n");
                        Write(inner, indent + 2);
                        break;
                    case YamlSequence list:
                        sb.Append(pad).Append(YamlText.Scalar(entry.Key.Value)).Append(": ").Append(YamlText.FlowList(list.Items.Select(i => i is YamlScalar s ? s.Value : Describe(i)))).Append('\n');
                        break;
                    case YamlScalar scalar:
                        sb.Append(pad).Append(YamlText.Scalar(entry.Key.Value)).Append(": ").Append(!scalar.Quoted && (bool.TryParse(scalar.Value, out _) || long.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ? scalar.Value : YamlText.Scalar(scalar.Value)).Append('\n');
                        break;
                }
            }
        }
    }
}
