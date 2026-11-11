using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    private static readonly Regex NameRule = new("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The reading of one file: the helpers here, the document in <c>ViewsLoader.Document.cs</c>, widgets in <c>ViewsLoader.Widgets.cs</c>, layouts in <c>ViewsLoader.Layouts.cs</c>.</summary>
    private sealed partial class Reader(string fileName, List<ConfigDiagnostic> diags, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? targets) : YamlFieldReader(fileName, diags)
    {
        private readonly HashSet<string> declared = [];

        private static string J(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
        private static string I(string path, int index) => $"{path}[{index}]";

        private void Bad(string code, YamlNode at, string path, string message, string? fix = null) => Add(code, at, message, fix, path);

        private void Warn(string code, YamlNode at, string path, string message, string? fix = null) =>
            Diagnostics.Add(new ConfigDiagnostic(code, ConfigSeverity.Warning, File, at.Line, at.Column, message, fix) { KeyPath = path });

        private void Unknown(YamlEntry e, string path, string where, IReadOnlyCollection<string> allowed)
        {
            var near = DidYouMean.Closest(e.Key.Value, allowed);
            Add(ViewsCodes.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in {where}." + (near != null ? $" Did you mean `{near}`?" : ""), $"Keys: {string.Join(", ", allowed)}.", J(path, e.Key.Value));
        }

        /// <summary>Keys outside <paramref name="allowed"/> are refused; <paramref name="special"/> gives a key its own refusal (a key of another type, a computation) and returns whether it handled it.</summary>
        private void CheckKeysAt(YamlMapping map, IReadOnlyCollection<string> allowed, string where, string path, Func<YamlEntry, bool>? special = null)
        {
            foreach (var e in map.Entries)
                if (!allowed.Contains(e.Key.Value) && !(special?.Invoke(e) ?? false)) Unknown(e, path, where, allowed);
        }

        private YamlMapping? Map(YamlNode? node, string path, string what, YamlNode at)
        {
            if (node is YamlMapping m) return m;
            Bad(ViewsCodes.InvalidValue, node ?? at, path, $"{what} must be a mapping.");
            return null;
        }

        private YamlSequence? Seq(YamlNode? node, string path, string what, YamlNode at, int min = 0, int max = int.MaxValue)
        {
            if (node is not YamlSequence s) { Bad(ViewsCodes.InvalidValue, node ?? at, path, $"{what} must be a list."); return null; }
            if (s.Items.Count < min) { Bad(ViewsCodes.InvalidValue, s, path, $"{what} must have at least {min} entr{(min == 1 ? "y" : "ies")}."); return null; }
            if (s.Items.Count > max) { Bad(ViewsCodes.InvalidValue, s, path, $"{what} has at most {max} entries."); return null; }
            return s;
        }

        private YamlNode? Required(YamlMapping map, string key, string path)
        {
            var node = map.Get(key);
            if (node is null) Add(ViewsCodes.MissingKey, map, $"Required key `{key}` is missing.", $"Add `{key}:`.", J(path, key));
            return node;
        }

        private string? Text(YamlNode? node, string path, string what, int max = 200, YamlNode? at = null)
        {
            if (node is YamlScalar { Value.Length: > 0 } s && s.Value.Length <= max && !s.Value.Any(char.IsControl)) return s.Value;
            Bad(ViewsCodes.InvalidValue, node ?? at!, path, $"{what} is one line of text, 1 to {max} characters.");
            return null;
        }

        private string? Name(YamlNode? node, string path, string what)
        {
            if (node is YamlScalar s && NameRule.IsMatch(s.Value)) return s.Value;
            Bad(ViewsCodes.InvalidValue, node!, path, $"{what} is a name: lower-case letters, digits, '-' and '_', starting with a letter (at most 64).");
            return null;
        }

        private bool? Flag(YamlNode? node, string path, string what)
        {
            if (node is YamlScalar { Quoted: false, Value: "true" or "false" } s) return s.Value == "true";
            Bad(ViewsCodes.InvalidValue, node!, path, $"{what} is true or false.");
            return null;
        }

        private double? Number(YamlNode? node, string path, string what, double min = double.NegativeInfinity, bool positive = false, double max = double.PositiveInfinity, string code = ViewsCodes.InvalidValue)
        {
            if (node is YamlScalar { Quoted: false } s && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                && d >= min && d <= max && (!positive || d > 0)) return d;
            Bad(code, node!, path, $"{what} is a number{(positive ? " above 0" : double.IsNegativeInfinity(min) ? "" : $" from {min.ToString(CultureInfo.InvariantCulture)}")}{(double.IsPositiveInfinity(max) ? "" : $" to {max.ToString(CultureInfo.InvariantCulture)}")}.");
            return null;
        }

        private int? Whole(YamlNode? node, string path, string what, int min, int max)
        {
            if (node is YamlScalar { Quoted: false } s && long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) return (int)n;
            Bad(ViewsCodes.InvalidValue, node!, path, $"{what} is a whole number from {min} to {max}.");
            return null;
        }

        private string? Choice(YamlNode? node, string path, string what, string[] choices, string code = ViewsCodes.InvalidValue)
        {
            if (node is YamlScalar s && choices.Contains(s.Value)) return s.Value;
            Bad(code, node!, path, $"{what} is one of: {string.Join(", ", choices)}.");
            return null;
        }

        private string? Field(YamlNode? node, string path, string what)
        {
            if (node is YamlScalar { Value.Length: > 0 } s && s.Value.Length <= 300 && !s.Value.Any(char.IsControl)) return s.Value;
            Bad(ViewsCodes.InvalidValue, node!, path, $"{what} is a field name (orders.net_revenue) or the alias of a query output.");
            return null;
        }

        private bool IsDeclared(string name) => declared.Contains(name);
    }
}
