using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbData.Config.Yaml;

/// <summary>A value to write into a document: text (quoted when plain would change its meaning) or a raw YAML token (numbers, booleans, flow collections).</summary>
public readonly record struct YamlValue(string Text, bool IsRaw = false)
{
    public static YamlValue Raw(string yaml) => new(yaml, true);
    public static implicit operator YamlValue(string text) => new(text);
}

/// <summary>Writing YAML scalars: plain when that is unambiguous for people and editors, double-quoted otherwise.</summary>
public static partial class YamlText
{
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~" };

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_ .,()\-]*$")]
    private static partial Regex BlockSafe();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_ .()\-]*$")]
    private static partial Regex FlowSafe();

    [GeneratedRegex(@"^[0-9][0-9_.,eE+\- ]*$")]
    private static partial Regex NumberLike();

    /// <summary>A scalar in a block context (<c>type: DECIMAL(14, 2)</c>).</summary>
    public static string Scalar(string value) => Plain(value, BlockSafe()) ? value : Quoted(value);

    /// <summary>A scalar inside a flow collection (<c>[a, b]</c>), where a comma would end it.</summary>
    public static string FlowScalar(string value) => Plain(value, FlowSafe()) ? value : Quoted(value);

    public static string FlowList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(FlowScalar)) + "]";

    internal static string Render(YamlValue v, bool flow) => v.IsRaw ? v.Text : flow ? FlowScalar(v.Text) : Scalar(v.Text);

    private static bool Plain(string value, Regex safe) =>
        value.Length > 0 && value == value.Trim() && safe.IsMatch(value) && !Reserved.Contains(value) && !NumberLike().IsMatch(value);

    private static string Quoted(string value) => JsonSerializer.Serialize(value, Quote);
}
