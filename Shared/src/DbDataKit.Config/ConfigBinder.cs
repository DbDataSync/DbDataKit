using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Config;

/// <summary>Where a bound value came from.</summary>
public sealed record SourceInfo(YamlNode Node, string File)
{
    public int Line => Node.Line;
    public int Column => Node.Column;
}

public sealed class BindResult<T>(T? value, IReadOnlyList<ConfigDiagnostic> diagnostics, IReadOnlyDictionary<string, SourceInfo> sources)
{
    public T? Value { get; } = value;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    /// <summary>Dotted key path (<c>server.users[0].name</c>) to the node it was read from.</summary>
    public IReadOnlyDictionary<string, SourceInfo> Sources { get; } = sources;
    public bool Ok => !Diagnostics.HasErrors();
}

/// <summary>
/// Binds a strict node tree onto a schema-described type with reflection. No YamlDotNet object deserializer is involved: every value goes through
/// the node tree, so the strict-subset safety rules hold. All problems are reported (not just the first) as diagnostics with positions.
/// </summary>
public sealed class ConfigBinder(IDiagnosticCatalog? catalog = null, IReadOnlyDictionary<YamlNode, string>? nodeFiles = null)
{
    private readonly IDiagnosticCatalog _catalog = catalog ?? DefaultDiagnosticCatalog.Instance;
    private readonly IReadOnlyDictionary<YamlNode, string>? _nodeFiles = nodeFiles;

    public BindResult<T> Bind<T>(YamlNode? root, string file) where T : class
    {
        var diags = new List<ConfigDiagnostic>();
        var sources = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        var run = new Run(this, file, diags, sources);
        T? value = null;
        if (root == null) value = (T?)run.Empty(typeof(T));
        else value = (T?)run.Bind(SchemaModel.For(typeof(T)), root, "", topLevel: true);
        return new BindResult<T>(value, diags, sources);
    }

    private sealed class Run(ConfigBinder b, string file, List<ConfigDiagnostic> diags, Dictionary<string, SourceInfo> sources)
    {
        private string nodeFileOr(YamlNode n) => b._nodeFiles != null && b._nodeFiles.TryGetValue(n, out var f) ? f : file;

        private void Add(string code, YamlNode at, string message, string? fix = null, string? path = null) =>
            diags.Add(b._catalog.Create(code, nodeFileOr(at), at.Line, at.Column, message, fix, path));

        public object? Empty(Type t)
        {
            var schema = SchemaModel.For(t);
            if (schema is not ObjectSchema obj) return null;
            var inst = Activator.CreateInstance(t, nonPublic: true)!;
            ApplyDefaultsAndRequired(obj, inst, [], null, "", new YamlMapping([], 1, 1));
            return inst;
        }

        public object? Bind(TypeSchema schema, YamlNode node, string path, bool topLevel = false)
        {
            sources[path] = new SourceInfo(node, nodeFileOr(node));
            switch (schema)
            {
                case ScalarSchema s: return BindScalar(s, node, path);
                case ListSchema l: return BindList(l, node, path);
                case MapSchema m: return BindMap(m, node, path);
                case UnionSchema u: return BindUnion(u, node, path, topLevel);
                case ObjectSchema o: return BindObject(o, node, path, topLevel, null);
                default: return null;
            }
        }

        private object? BindUnion(UnionSchema u, YamlNode node, string path, bool topLevel)
        {
            if (node is not YamlMapping map) { Add(SharedCodes.InvalidValue, node, $"`{Label(path)}` must be a mapping with `{u.Discriminator}`.", path: path); return null; }
            var disc = map.Get(u.Discriminator);
            if (disc is not YamlScalar { Value.Length: > 0 } ds)
            {
                Add(SharedCodes.MissingKey, map, $"Required key `{u.Discriminator}` is missing.", $"Add `{u.Discriminator}:` with one of: {string.Join(", ", u.Cases.Keys)}.", path);
                return null;
            }
            if (!u.Cases.TryGetValue(ds.Value, out var caseSchema))
            {
                var near = DidYouMean.Closest(ds.Value, u.Cases.Keys);
                Add(SharedCodes.UnknownUnionKind, ds, $"`{u.Discriminator}` is `{ds.Value}`." + (near != null ? $" Did you mean `{near}`?" : ""), $"One of: {string.Join(", ", u.Cases.Keys)}.", path + "." + u.Discriminator);
                return null;
            }
            return BindObject(caseSchema, map, path, topLevel, u.Discriminator);
        }

        private object? BindObject(ObjectSchema o, YamlNode node, string path, bool topLevel, string? discriminator)
        {
            if (node is not YamlMapping map)
            {
                Add(SharedCodes.InvalidValue, node, $"`{Label(path)}` must be a mapping.", path: path);
                return null;
            }
            var inst = Activator.CreateInstance(o.ClrType, nonPublic: true)!;
            var allowed = o.AllowedKeys(topLevel).ToList();
            var extras = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var e in map.Entries)
            {
                var key = e.Key.Value;
                if (discriminator != null && key == discriminator) continue;
                if (topLevel && o.HasVersion && (key == o.VersionKey || (key == SchemaHeader.Key && o.AcceptsHeader))) continue;
                var p = o.Find(key);
                if (p == null)
                {
                    if (o.UnknownKeys == UnknownKeyPolicy.Preserve) { extras[key] = e.Value; continue; }
                    var near = DidYouMean.Closest(key, allowed);
                    Add(SharedCodes.UnknownKey, e.Key, $"Unknown key `{key}` in {(path.Length == 0 ? "the document" : $"`{path}`")}." + (near != null ? $" Did you mean `{near}`?" : ""), $"Keys: {string.Join(", ", allowed)}.", Join(path, key));
                    continue;
                }
                var childPath = Join(path, key);
                if (p.DeprecatedMessage != null)
                    diags.Add(b._catalog.Create(SharedCodes.Deprecated, nodeFileOr(e.Key), e.Key.Line, e.Key.Column, $"`{key}` is deprecated{(p.DeprecatedSince != null ? $" (since {p.DeprecatedSince})" : "")}: {p.DeprecatedMessage}", null, childPath));
                sources[childPath + "#key"] = new SourceInfo(e.Key, nodeFileOr(e.Key));
                var value = BindProperty(p, e.Value, childPath);
                if (value.Ok) p.Property.SetValue(inst, value.Value);
            }
            if (o.Extra != null && extras.Count > 0) o.Extra.SetValue(inst, extras);
            ApplyDefaultsAndRequired(o, inst, map.Entries.Select(e => e.Key.Value).ToHashSet(), map, path, map);
            return inst;
        }

        private void ApplyDefaultsAndRequired(ObjectSchema o, object inst, HashSet<string> present, YamlMapping? map, string path, YamlNode at)
        {
            foreach (var p in o.Properties)
            {
                if (present.Contains(p.Key)) continue;
                if (p.Required) { Add(SharedCodes.MissingKey, at, $"Required key `{p.Key}` is missing.", $"Add `{p.Key}:`.", Join(path, p.Key)); continue; }
                if (p.Default != null)
                {
                    var v = ConvertDefault(p, p.Default);
                    if (v != null) p.Property.SetValue(inst, v);
                }
            }
        }

        private static object? ConvertDefault(PropertySchema p, object d)
        {
            var t = Nullable.GetUnderlyingType(p.Property.PropertyType) ?? p.Property.PropertyType;
            if (t.IsInstanceOfType(d)) return d;
            if (t.IsEnum) return d is string s ? Enum.Parse(t, s, true) : Enum.ToObject(t, d);
            if (t == typeof(TimeSpan) && d is string ts) return ParseTimeSpan(ts);
            return Convert.ChangeType(d, t, CultureInfo.InvariantCulture);
        }

        private (bool Ok, object? Value) BindProperty(PropertySchema p, YamlNode node, string path)
        {
            // an empty `key:` is "no value": allowed for nullable/reference types, a mistake for the rest
            var before = diags.Count;
            var v = Bind(p.Type, node, path);
            if (diags.Skip(before).Any(d => d.Severity == ConfigSeverity.Error)) return (false, null);
            Constrain(p, v, node, path);
            return (true, v);
        }

        private void Constrain(PropertySchema p, object? v, YamlNode node, string path)
        {
            if (v == null) return;
            if ((p.Min != null || p.Max != null) && v is IConvertible and not string and not bool and not Enum)
            {
                var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (p.Min != null && d < p.Min) Add(SharedCodes.InvalidValue, node, $"`{p.Key}` is {FormatNum(d)}; the minimum is {FormatNum(p.Min.Value)}.", path: path);
                if (p.Max != null && d > p.Max) Add(SharedCodes.InvalidValue, node, $"`{p.Key}` is {FormatNum(d)}; the maximum is {FormatNum(p.Max.Value)}.", path: path);
            }
            else if (v is TimeSpan ts && (p.Min != null || p.Max != null))
            {
                if (p.Min != null && ts.TotalSeconds < p.Min) Add(SharedCodes.InvalidValue, node, $"`{p.Key}` is shorter than {FormatNum(p.Min.Value)} seconds.", path: path);
                if (p.Max != null && ts.TotalSeconds > p.Max) Add(SharedCodes.InvalidValue, node, $"`{p.Key}` is longer than {FormatNum(p.Max.Value)} seconds.", path: path);
            }
            if (p.Pattern != null && v is string s && !Regex.IsMatch(s, "^(?:" + p.Pattern + ")$", RegexOptions.CultureInvariant))
                Add(SharedCodes.InvalidValue, node, $"`{p.Key}` is `{s}`, which does not match `{p.Pattern}`.", path: path);
        }

        private static string FormatNum(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private object? BindList(ListSchema l, YamlNode node, string path)
        {
            if (node is not YamlSequence seq) { Add(SharedCodes.InvalidValue, node, $"`{Label(path)}` must be a list, for example `{Label(path)}: [a, b]`.", path: path); return null; }
            var itemType = l.Item.ClrType;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
            var ok = true;
            for (var i = 0; i < seq.Items.Count; i++)
            {
                var before = diags.Count;
                var item = Bind(l.Item, seq.Items[i], $"{path}[{i}]");
                if (diags.Skip(before).Any(d => d.Severity == ConfigSeverity.Error)) { ok = false; continue; }
                if (item == null && itemType.IsValueType && Nullable.GetUnderlyingType(itemType) == null) { ok = false; continue; }
                list.Add(item);
            }
            if (!ok) return null;
            if (l.ClrType.IsArray) { var arr = Array.CreateInstance(itemType, list.Count); list.CopyTo(arr, 0); return arr; }
            return list;
        }

        private object? BindMap(MapSchema m, YamlNode node, string path)
        {
            if (node is not YamlMapping map) { Add(SharedCodes.InvalidValue, node, $"`{Label(path)}` must be a mapping.", path: path); return null; }
            var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), m.Value.ClrType))!;
            var ok = true;
            foreach (var e in map.Entries)
            {
                var before = diags.Count;
                var v = Bind(m.Value, e.Value, Join(path, e.Key.Value));
                if (diags.Skip(before).Any(d => d.Severity == ConfigSeverity.Error)) { ok = false; continue; }
                dict[e.Key.Value] = v;
            }
            return ok ? dict : null;
        }

        private object? BindScalar(ScalarSchema s, YamlNode node, string path)
        {
            if (node is not YamlScalar sc) { Add(SharedCodes.InvalidValue, node, $"`{Label(path)}` must be a single value.", path: path); return null; }
            var t = s.Underlying;
            var text = sc.Value;
            if (t == typeof(string)) return text;
            if (!sc.Quoted && (text.Length == 0 || text is "null" or "~"))
            {
                if (s.IsNullable) return null;
                Add(SharedCodes.InvalidValue, sc, $"`{Label(path)}` has no value.", path: path);
                return null;
            }
            try
            {
                if (t == typeof(bool))
                {
                    if (text is "true" or "false") return text == "true";
                    return Bad(sc, path, text, "true or false (lowercase).");
                }
                if (t.IsEnum)
                {
                    var names = Enum.GetNames(t);
                    var hit = names.FirstOrDefault(n => Norm(n) == Norm(text));
                    if (hit != null && !text.All(char.IsDigit)) return Enum.Parse(t, hit);
                    var near = DidYouMean.Closest(text, names.Select(n => n));
                    return Bad(sc, path, text, "One of: " + string.Join(", ", names.Select(n => SchemaModel.ApplyNaming(n, KeyNaming.CamelCase))) + (near != null ? $". Did you mean `{near}`?" : "."));
                }
                if (t == typeof(TimeSpan))
                    return ParseTimeSpan(text) is { } ts ? ts : Bad(sc, path, text, "A duration such as `30s`, `5m`, `2h`, `1d`, `250ms` or `00:00:30`.");
                if (t == typeof(Guid)) return Guid.TryParse(text, out var g) ? g : Bad(sc, path, text, "A GUID.");
                if (t == typeof(Uri)) return Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var u) ? u : Bad(sc, path, text, "A URI.");
                if (t == typeof(DateTimeOffset)) return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) ? dto : Bad(sc, path, text, "An ISO 8601 date and time.");
                if (t == typeof(DateTime)) return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : Bad(sc, path, text, "An ISO 8601 date and time.");
                if (t == typeof(char)) return text.Length == 1 ? text[0] : Bad(sc, path, text, "One character.");
                if (t == typeof(decimal))
                    return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) ? dec : Bad(sc, path, text, "A number.");
                if (t == typeof(double) || t == typeof(float))
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) || double.IsNaN(dbl) || double.IsInfinity(dbl)) return Bad(sc, path, text, "A number.");
                    return Convert.ChangeType(dbl, t, CultureInfo.InvariantCulture);
                }
                // integral
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) && !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    return Bad(sc, path, text, "A whole number.");
                return Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
            {
                return Bad(sc, path, text, $"A value that fits {t.Name}.");
            }
        }

        private object? Bad(YamlScalar sc, string path, string text, string supported)
        {
            Add(SharedCodes.InvalidValue, sc, $"`{Label(path)}` is `{(text.Length > 60 ? text[..60] + "..." : text)}`.", supported, path);
            return null;
        }
    }

    internal static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();
    private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;
    private static string Label(string path) { var i = path.LastIndexOf('.'); return i < 0 ? path : path[(i + 1)..]; }

    /// <summary>Parses <c>00:00:30</c> or a number with a unit: <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>.</summary>
    public static TimeSpan? ParseTimeSpan(string text)
    {
        text = text.Trim();
        if (text.Contains(':')) return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) ? ts : null;
        var m = Regex.Match(text, @"^(\d+(?:\.\d+)?)(ms|s|m|h|d)$", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n), "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n),
        };
    }
}
