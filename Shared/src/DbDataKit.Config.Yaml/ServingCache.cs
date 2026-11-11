using System.Globalization;

namespace DbDataKit.Config.Yaml;

/// <summary>Who may share a cached result (plan 24 section 3, 24a UC03): <see cref="Caller"/> keeps each caller's results apart, <see cref="Rules"/> shares between callers the access rules treat alike, <see cref="Shared"/> between everyone.</summary>
public enum CacheScope { Caller, Rules, Shared }

/// <summary>
/// The <c>cache:</c> section of a published definition (plan 24 section 3.1): whether results of this dataset are cached in the serving layer, for how long at most, who may share an entry, and which dataset of the
/// definition it is about. The section is a part of <b>format 2</b> of the semantic package, of DbDataStore's <c>model</c> and of the serving data source (24a D5); a format-1 file that has it is refused naming the file.
/// Nothing reads it yet: the cache service itself is UC12, and the cache is off unless the section says <c>enabled: true</c>.
/// </summary>
/// <param name="Enabled">Off by default; an owner turns it on per definition.</param>
/// <param name="Ttl">How long an entry may live when nothing the suite can see has changed (the backstop for a source written outside the suite).</param>
/// <param name="Scope">Who may share an entry.</param>
/// <param name="Dataset">The part of the definition the setting is about (an entity of a model); null for the whole definition.</param>
public sealed record CacheSection(bool Enabled, TimeSpan Ttl, CacheScope Scope, string? Dataset)
{
    /// <summary>What a section that gives no <c>ttl</c> gets (24a QU7: five minutes).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>The longest time-to-live a definition may ask for (24a QU7: 24 hours).</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);

    public static readonly CacheSection Off = new(false, DefaultTtl, CacheScope.Caller, null);

    /// <summary>The keys of the section, in the order the documentation gives them.</summary>
    public static readonly IReadOnlyList<string> Keys = ["enabled", "ttl", "scope", "dataset"];

    /// <summary>The scope names, as written in a file.</summary>
    public static readonly IReadOnlyList<string> ScopeNames = ["caller", "rules", "shared"];

    /// <summary>A whole number above zero and a unit, <c>30s</c>, <c>5m</c>, <c>24h</c>; matched entirely (the binder's <c>Pattern</c>), and with anchors in a JSON Schema.</summary>
    public const string TtlPattern = "[1-9][0-9]{0,5}(s|m|h)";

    /// <summary>Reads a time-to-live (<c>90s</c>, <c>5m</c>, <c>2h</c>: a whole number above zero and a unit) of at most <see cref="MaxTtl"/>; false with the problem in words.</summary>
    public static bool TryParseTtl(string? text, out TimeSpan ttl, out string? problem)
    {
        ttl = default;
        problem = null;
        if (string.IsNullOrEmpty(text) || text.Length < 2 || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || text[0] == '0')
        {
            problem = $"`ttl` is `{text}`; write a whole number above zero and a unit, such as `90s`, `5m` or `2h`.";
            return false;
        }

        var unit = text[^1] switch { 's' => TimeSpan.FromSeconds(count), 'm' => TimeSpan.FromMinutes(count), 'h' => TimeSpan.FromHours(count), _ => (TimeSpan?)null };
        if (unit is not { } span)
        {
            problem = $"`ttl` is `{text}`; the unit is `s`, `m` or `h`.";
            return false;
        }

        if (span > MaxTtl)
        {
            problem = $"`ttl` is `{text}`; a cached result lives at most 24 hours (`24h`).";
            return false;
        }

        ttl = span;
        return true;
    }

    public static bool TryParseScope(string? text, out CacheScope scope)
    {
        scope = CacheScope.Caller;
        if (text is null || !ScopeNames.Contains(text)) return false;
        scope = Enum.Parse<CacheScope>(char.ToUpperInvariant(text[0]) + text[1..]);
        return true;
    }

    /// <summary>The scope's name as a file writes it.</summary>
    public static string Name(CacheScope scope) => scope.ToString().ToLowerInvariant();

    /// <summary>Reads the section from a node of a strict YAML file; null with diagnostics (file, line, column) when it is not valid. A missing node is the caller's business.</summary>
    public static CacheSection? Read(YamlNode node, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null) =>
        new Reader(file, diagnostics, catalog).Read(node);

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog) : YamlFieldReader(file, diagnostics, catalog)
    {
        public CacheSection? Read(YamlNode node)
        {
            if (node is not YamlMapping map)
            {
                Add(SharedCodes.InvalidValue, node, "`cache` is a mapping: `enabled`, `ttl`, `scope` and `dataset`.", "Write `cache: { enabled: true, ttl: 5m }`.", "cache");
                return null;
            }

            var before = Diagnostics.Count;
            CheckKeys(map, Keys, "`cache`");
            var enabled = false;
            if (map.Get("enabled") is { } e)
            {
                if (e is YamlScalar { Quoted: false, Value: "true" or "false" } b) enabled = b.Value == "true";
                else Add(SharedCodes.InvalidValue, e, "`cache.enabled` is `true` or `false`.", keyPath: "cache.enabled");
            }

            var ttl = DefaultTtl;
            if (map.Get("ttl") is { } t)
            {
                if (t is not YamlScalar ts) Add(SharedCodes.InvalidValue, t, "`cache.ttl` is a duration such as `5m`.", keyPath: "cache.ttl");
                else if (!TryParseTtl(ts.Value, out ttl, out var problem)) Add(SharedCodes.InvalidValue, t, problem!.Replace("`ttl`", "`cache.ttl`", StringComparison.Ordinal), "Write a whole number and `s`, `m` or `h`, at most `24h`.", "cache.ttl");
            }

            var scope = CacheScope.Caller;
            if (map.Get("scope") is { } s)
            {
                if (s is not YamlScalar ss || !TryParseScope(ss.Value, out scope))
                    Add(SharedCodes.InvalidValue, s, $"`cache.scope` is `{(s as YamlScalar)?.Value}`; it is one of {string.Join(", ", ScopeNames.Select(n => $"`{n}`"))}.", keyPath: "cache.scope");
            }

            string? dataset = null;
            if (map.Get("dataset") is { } d)
            {
                if (d is YamlScalar { Value.Length: > 0 } ds) dataset = ds.Value;
                else Add(SharedCodes.InvalidValue, d, "`cache.dataset` names a dataset of the definition, as text.", keyPath: "cache.dataset");
            }

            return Diagnostics.Skip(before).Any(x => x.Severity == ConfigSeverity.Error) ? null : new CacheSection(enabled, ttl, scope, dataset);
        }
    }
}
