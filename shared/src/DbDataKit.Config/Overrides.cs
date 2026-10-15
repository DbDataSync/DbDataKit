using System.Collections;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Config;

/// <summary>One override: a path (segments; numeric segments index lists), the text value and where it came from (<c>env:NAME</c>, <c>cli:--set a.b</c>).</summary>
public sealed record OverrideEntry(IReadOnlyList<string> Segments, string Value, string Origin)
{
    public string Path => string.Concat(Segments.Select((s, i) => int.TryParse(s, out var n) ? $"[{n}]" : (i == 0 ? s : "." + s)));
}

/// <summary>A legacy environment prefix kept for compatibility (<c>DBPROXY_</c> with section <c>Proxy</c>, or <c>DbDataSync__</c>).</summary>
public sealed record EnvAlias(string Prefix, string? StripSection = null);

/// <summary>
/// Environment source. Canonical form is <c>DBDATA_&lt;APP&gt;__A__B</c> (double underscore separates levels; a numeric level indexes a list).
/// Names are matched case-insensitively. Legacy aliases are applied first, so the canonical name wins when both are set.
/// </summary>
public static class EnvironmentSource
{
    public static IReadOnlyList<OverrideEntry> Read(string app, IEnumerable<EnvAlias>? legacy = null, IReadOnlyDictionary<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);
        var result = new List<OverrideEntry>();
        foreach (var alias in legacy ?? [])
            Collect(env, alias.Prefix, alias.StripSection, result);
        Collect(env, $"DBDATA_{app}__", null, result);
        return result;
    }

    private static void Collect(IReadOnlyDictionary<string, string?> env, string prefix, string? strip, List<OverrideEntry> into)
    {
        foreach (var (name, value) in env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (value == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length) continue;
            var segs = name[prefix.Length..].Split("__", StringSplitOptions.None).ToList();
            if (strip != null && segs.Count > 1 && string.Equals(segs[0], strip, StringComparison.OrdinalIgnoreCase)) segs.RemoveAt(0);
            if (segs.Any(s => s.Length == 0)) continue;
            into.Add(new OverrideEntry(segs, value, "env:" + name));
        }
    }
}

public sealed record CliParse(IReadOnlyList<OverrideEntry> Overrides, IReadOnlyList<string> Rest);

/// <summary>
/// Command-line source: <c>--set a.b=value</c> (or <c>--set=a.b=value</c>), and the shorthand <c>--a.b=value</c> / <c>--a.b value</c> for any option
/// whose name contains a dot. Everything else is returned untouched in <see cref="CliParse.Rest"/>.
/// </summary>
public static class CommandLineSource
{
    public static CliParse Read(IEnumerable<string> args)
    {
        var list = args.ToList();
        var overrides = new List<OverrideEntry>();
        var rest = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a == "--set" && i + 1 < list.Count) { Add(list[++i], overrides, "--set"); continue; }
            if (a.StartsWith("--set=", StringComparison.Ordinal)) { Add(a[6..], overrides, "--set"); continue; }
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2 && a.Contains('.') )
            {
                var eq = a.IndexOf('=');
                if (eq > 0) { Add(a[2..eq] + "=" + a[(eq + 1)..], overrides, a[..eq]); continue; }
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) { Add(a[2..] + "=" + list[++i], overrides, a); continue; }
            }
            rest.Add(a);
        }
        return new CliParse(overrides, rest);
    }

    private static void Add(string assignment, List<OverrideEntry> into, string flag)
    {
        var eq = assignment.IndexOf('=');
        if (eq <= 0) return;
        var segs = ParsePath(assignment[..eq]);
        if (segs.Count == 0) return;
        into.Add(new OverrideEntry(segs, assignment[(eq + 1)..], $"cli:{flag} {assignment[..eq]}"));
    }

    public static List<string> ParsePath(string path)
    {
        var segs = new List<string>();
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = part.IndexOf('[');
            if (b < 0) { segs.Add(part); continue; }
            if (b > 0) segs.Add(part[..b]);
            foreach (var idx in part[b..].Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries)) segs.Add(idx);
        }
        return segs;
    }
}

/// <summary>Applies overrides to a merged tree. A scalar override replaces exactly the value at its path (no list appending); keys match existing keys ignoring case, <c>_</c> and <c>-</c>.</summary>
public static class ConfigOverlay
{
    public static MergedYaml Apply(MergedYaml baseYaml, IEnumerable<OverrideEntry> entries, ObjectSchema? schema, List<ConfigDiagnostic> diags, IDiagnosticCatalog? catalog = null)
    {
        catalog ??= DefaultDiagnosticCatalog.Instance;
        var fileOf = new Dictionary<YamlNode, string>(baseYaml.FileOf, ReferenceEqualityComparer.Instance);
        YamlNode root = baseYaml.Root;
        foreach (var e in entries)
        {
            var origin = e.Origin;
            try
            {
                root = Set(root, e.Segments, 0, e, schema, fileOf, origin) ?? root;
            }
            catch (InvalidOperationException ex)
            {
                diags.Add(catalog.Create(SharedCodes.InvalidValue, origin, 0, 0, $"Override `{e.Path}` could not be applied: {ex.Message}", keyPath: e.Path));
            }
        }
        return new MergedYaml((YamlMapping)root, fileOf);
    }

    private static YamlNode? Set(YamlNode? node, IReadOnlyList<string> segs, int i, OverrideEntry e, TypeSchema? schema, Dictionary<YamlNode, string> fileOf, string origin)
    {
        T Own<T>(T n) where T : YamlNode { fileOf[n] = origin; return n; }
        if (i == segs.Count) return Own(Leaf(e.Value, schema, fileOf, origin));
        var seg = segs[i];
        var isIndex = int.TryParse(seg, out var idx) && idx >= 0;

        if (node is YamlSequence || (node == null && isIndex))
        {
            if (!isIndex) throw new InvalidOperationException($"`{seg}` is not a list index.");
            var items = ((YamlSequence?)node)?.Items.ToList() ?? [];
            var itemSchema = (schema as ListSchema)?.Item;
            if (idx > items.Count) throw new InvalidOperationException($"index {idx} is past the end of a list with {items.Count} items.");
            var child = Set(idx < items.Count ? items[idx] : null, segs, i + 1, e, itemSchema, fileOf, origin)!;
            if (idx < items.Count) items[idx] = child; else items.Add(child);
            return Own(new YamlSequence(items, node?.Line ?? 0, node?.Column ?? 0));
        }
        if (node is YamlMapping || node == null)
        {
            var map = (YamlMapping?)node;
            var entries = map?.Entries.ToList() ?? [];
            var at = entries.FindIndex(x => x.Key.Value == seg);
            if (at < 0) at = entries.FindIndex(x => ConfigBinder.Norm(x.Key.Value) == ConfigBinder.Norm(seg));
            string key = at >= 0 ? entries[at].Key.Value : seg;
            TypeSchema? childSchema = null;
            switch (schema)
            {
                case ObjectSchema o:
                    var p = o.Properties.FirstOrDefault(x => x.Key == key) ?? o.Properties.FirstOrDefault(x => ConfigBinder.Norm(x.Key) == ConfigBinder.Norm(seg));
                    if (p != null) { childSchema = p.Type; if (at < 0) key = p.Key; }
                    break;
                case MapSchema m: childSchema = m.Value; break;
            }
            var child = Set(at >= 0 ? entries[at].Value : null, segs, i + 1, e, childSchema, fileOf, origin)!;
            if (at >= 0) entries[at] = new YamlEntry(entries[at].Key, child);
            else entries.Add(new YamlEntry(Own(new YamlScalar(key, 0, 0)), child));
            return Own(new YamlMapping(entries, map?.Line ?? 0, map?.Column ?? 0));
        }
        throw new InvalidOperationException($"`{seg}` goes below a single value.");
    }

    private static YamlNode Leaf(string value, TypeSchema? schema, Dictionary<YamlNode, string> fileOf, string origin)
    {
        if (schema is ListSchema { Item: ScalarSchema })
        {
            var items = value.Length == 0 ? [] : value.Split(',').Select(v => (YamlNode)new YamlScalar(v.Trim(), 0, 0)).ToList();
            foreach (var n in items) fileOf[n] = origin;
            return new YamlSequence(items, 0, 0);
        }
        return new YamlScalar(value, 0, 0);
    }
}
