using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Quality;

/// <summary>
/// The <c>checks:</c> section of a definition (format 2 of the semantic package, of DbDataStore's <c>model</c> and of the serving data source, plan 24a D5) and the <c>checks</c> of a <c>check/1</c> file: a list of mappings, one check each, read strictly. Every problem is a diagnostic
/// with the file, line and column of what is wrong, and a suggestion for a near key or kind. The shapes are in <c>schemas/check.schema.json</c>.
/// </summary>
public static partial class CheckSection
{
    public const string Key = "checks";

    public const string UnknownKind = "DBD-QLT-001";
    public const string BadParameter = "DBD-QLT-002";
    public const string NoDataset = "DBD-QLT-003";
    public const string BadSetting = "DBD-QLT-004";
    public const string DuplicateId = "DBD-QLT-005";
    public const string BadFile = "DBD-QLT-006";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    public static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://\S.*$")]
    private static partial Regex IdentityPattern();

    private static readonly string[] CommonKeys = ["id", "kind", "dataset", "severity", "on_error", "description"];

    /// <summary>The keys each kind accepts besides the common ones.</summary>
    public static IReadOnlyList<string> KeysOf(CheckKind kind) => kind switch
    {
        CheckKind.NotNull or CheckKind.Unique => ["column", "columns"],
        CheckKind.AcceptedValues => ["column", "values"],
        CheckKind.Range => ["column", "min", "max"],
        CheckKind.Referential => ["column", "columns", "references"],
        CheckKind.Freshness => ["column", "max_age"],
        _ => ["max_increase_percent", "max_decrease_percent"],
    };

    /// <summary>
    /// Reads the section from its node. Null with diagnostics when the section or any check is not valid. <paramref name="defaultDataset"/> is the dataset a check that names none is about (a definition with one dataset of its own); without it each check names its
    /// <c>dataset</c>.
    /// </summary>
    public static IReadOnlyList<CheckDefinition>? Read(YamlNode node, string file, List<ConfigDiagnostic> diagnostics, string? defaultDataset = null, IDiagnosticCatalog? catalog = null) =>
        new Reader(file, diagnostics, catalog, defaultDataset).ReadSection(node);

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog, string? defaultDataset) : YamlFieldReader(file, diagnostics, catalog)
    {
        public IReadOnlyList<CheckDefinition>? ReadSection(YamlNode node)
        {
            if (node is not YamlSequence list)
            {
                Add(SharedCodes.InvalidValue, node, "`checks` is a list of checks, one mapping each.", "Write `checks:` with a `- ` entry for each check.", Key);
                return null;
            }

            var before = Diagnostics.Count;
            var checks = new List<CheckDefinition>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping map) { Add(SharedCodes.InvalidValue, item, "Each entry of `checks` is a mapping.", "Write `- { id: ..., kind: ..., ... }`.", Key); continue; }
                if (ReadCheck(map) is not { } check) continue;
                if (!ids.Add(check.Id)) Add(DuplicateId, map.Get("id")!, $"The check id `{check.Id}` is used twice in `checks`.", "Give each check its own id; results and blocks are kept by it.", "checks.id");
                else checks.Add(check);
            }

            return Diagnostics.Skip(before).HasErrors() ? null : checks;
        }

        private CheckDefinition? ReadCheck(YamlMapping map)
        {
            var before = Diagnostics.Count;
            string? id = null;
            if (map.Get("id") is not { } idNode) Add(SharedCodes.MissingKey, map, "A check needs an `id`.", "Add `id:` with a name such as `orders_customer_not_null`.", "checks.id");
            else if (idNode is not YamlScalar { Value: var idText } || !IdPattern().IsMatch(idText))
                Add(SharedCodes.InvalidValue, idNode, "`id` starts with a letter and has letters, digits, `_`, `.` and `-`, at most 64 characters.", keyPath: "checks.id");
            else id = idText;

            CheckKind? kind = null;
            if (map.Get("kind") is not { } kindNode) Add(SharedCodes.MissingKey, map, $"A check needs a `kind`: {string.Join(", ", CheckNames.Kinds.Select(k => $"`{k}`"))}.", "Add `kind:`.", "checks.kind");
            else if (kindNode is not YamlScalar { Value: var kindText } || !CheckNames.TryKind(kindText, out var parsed))
            {
                var text = (kindNode as YamlScalar)?.Value ?? "";
                var near = DidYouMean.Closest(text, CheckNames.Kinds);
                Add(UnknownKind, kindNode, $"`{text}` is not a kind of check." + (near is null ? "" : $" Did you mean `{near}`?"), $"The kinds are {string.Join(", ", CheckNames.Kinds.Select(k => $"`{k}`"))}.", "checks.kind");
            }
            else kind = parsed;

            if (kind is null)
            {
                // the keys cannot be told without the kind; the rest of the entry is not read
                return null;
            }

            CheckKeys(map, [.. CommonKeys, .. KeysOf(kind.Value)], $"a `{CheckNames.Name(kind.Value)}` check");

            var severity = CheckSeverity.Warn;
            if (map.Get("severity") is { } sev) severity = Setting(sev, CheckNames.Severities, "severity", CheckSeverity.Warn, i => (CheckSeverity)i);
            var onError = CheckOnError.Alert;
            if (map.Get("on_error") is { } oe) onError = Setting(oe, CheckNames.OnErrors, "on_error", CheckOnError.Alert, i => (CheckOnError)i);
            string? description = null;
            if (map.Get("description") is { } d)
            {
                if (d is YamlScalar { Value.Length: > 0 } ds) description = ds.Value;
                else Add(SharedCodes.InvalidValue, d, "`description` is text.", keyPath: "checks.description");
            }

            var dataset = Dataset(map.Get("dataset"), map, "dataset", defaultDataset);
            var columns = ReadColumns(map, kind.Value);
            var parameters = ReadParameters(map, kind.Value, columns);

            if (id is null || dataset is null || columns is null || parameters is null || Diagnostics.Skip(before).HasErrors()) return null;
            return new CheckDefinition(id, kind.Value, new CheckAttachment(dataset, columns), parameters, severity, onError, description);
        }

        private T Setting<T>(YamlNode node, IReadOnlyList<string> names, string key, T fallback, Func<int, T> make)
        {
            if (node is YamlScalar { Value: var text } && names.ToList().IndexOf(text) is var at and >= 0) return make(at);
            var shown = (node as YamlScalar)?.Value;
            var near = shown is null ? null : DidYouMean.Closest(shown, names);
            Add(BadSetting, node, $"`{key}` is `{shown}`; it is {string.Join(" or ", names.Select(n => $"`{n}`"))}." + (near is null ? "" : $" Did you mean `{near}`?"), keyPath: $"checks.{key}");
            return fallback;
        }

        private string? Dataset(YamlNode? node, YamlMapping map, string key, string? fallback)
        {
            if (node is null)
            {
                if (fallback is not null) return fallback;
                Add(NoDataset, map, key == "dataset" ? "The check names no dataset." : "`references` names no dataset to look the values up in.",
                    key == "dataset" ? "Add `dataset:` with the lineage identity of the dataset or entity (or, in a definition, its name there)." : "Add `dataset:` under `references`.", $"checks.{key}");
                return null;
            }

            if (node is not YamlScalar { Value.Length: > 0 } scalar || scalar.Value != scalar.Value.Trim())
            {
                Add(SharedCodes.InvalidValue, node, "`dataset` is a dataset's lineage identity or its name, as text without spaces around it.", keyPath: $"checks.{key}");
                return null;
            }

            if (scalar.Value.Contains("://", StringComparison.Ordinal) && !IdentityPattern().IsMatch(scalar.Value))
            {
                Add(SharedCodes.InvalidValue, node, $"`{scalar.Value}` is not a lineage identity: it is `<scheme>://<server>/<name>`.", keyPath: $"checks.{key}");
                return null;
            }

            return scalar.Value;
        }

        /// <summary>The columns of a check: <c>column</c> or <c>columns</c> (not both) for the kinds that take columns, none for <c>row_count_change</c>.</summary>
        private IReadOnlyList<string>? ReadColumns(YamlMapping map, CheckKind kind)
        {
            var single = map.Get("column");
            var many = map.Get("columns");
            var needs = kind is CheckKind.NotNull or CheckKind.Unique or CheckKind.AcceptedValues or CheckKind.Range or CheckKind.Referential;
            if (kind == CheckKind.RowCountChange || kind == CheckKind.Freshness && many is null && single is null) return [];
            if (single is not null && many is not null)
            {
                Add(SharedCodes.InvalidValue, many, "Write `column` or `columns`, not both.", keyPath: "checks.columns");
                return null;
            }

            if (single is null && many is null)
            {
                if (needs) Add(SharedCodes.MissingKey, map, $"A `{CheckNames.Name(kind)}` check needs the column it is about.", kind is CheckKind.AcceptedValues or CheckKind.Range ? "Add `column:`." : "Add `column:` (one) or `columns:` (a list).", "checks.column");
                return needs ? null : [];
            }

            if (single is not null)
            {
                if (single is YamlScalar { Value.Length: > 0 } s) return [s.Value];
                Add(SharedCodes.InvalidValue, single, "`column` is a column name, as text.", keyPath: "checks.column");
                return null;
            }

            if (kind is CheckKind.AcceptedValues or CheckKind.Range or CheckKind.Freshness)
            {
                Add(SharedCodes.UnknownKey, many!, $"A `{CheckNames.Name(kind)}` check is about one column: write `column`, not `columns`.", keyPath: "checks.columns");
                return null;
            }

            if (many is not YamlSequence { Items.Count: > 0 } seq)
            {
                Add(SharedCodes.InvalidValue, many!, "`columns` is a list of one or more column names.", keyPath: "checks.columns");
                return null;
            }

            var names = new List<string>();
            foreach (var item in seq.Items)
            {
                if (item is YamlScalar { Value.Length: > 0 } c)
                {
                    if (names.Contains(c.Value)) Add(SharedCodes.InvalidValue, item, $"`{c.Value}` is listed more than once in `columns`.", keyPath: "checks.columns");
                    else names.Add(c.Value);
                }
                else Add(SharedCodes.InvalidValue, item, "Entries of `columns` are column names, as text.", keyPath: "checks.columns");
            }

            return names;
        }

        private CheckParameters? ReadParameters(YamlMapping map, CheckKind kind, IReadOnlyList<string>? columns)
        {
            switch (kind)
            {
                case CheckKind.NotNull or CheckKind.Unique:
                    return NoParameters.Instance;
                case CheckKind.AcceptedValues:
                    return Values(map);
                case CheckKind.Range:
                    return Range(map);
                case CheckKind.Referential:
                    return Referential(map, columns);
                case CheckKind.Freshness:
                    if (map.Get("max_age") is not { } age) { Add(SharedCodes.MissingKey, map, "A `freshness` check needs `max_age`: how old the newest data may be.", "Add `max_age:` such as `6h` or `1d`.", "checks.max_age"); return null; }
                    if (age is YamlScalar { Value: var text } && FreshnessParameters.TryParse(text, out var span)) return new FreshnessParameters(span);
                    Add(BadParameter, age, $"`max_age` is `{(age as YamlScalar)?.Value}`; write a whole number above zero and a unit: `s`, `m`, `h` or `d` (`90m`, `6h`, `1d`).", keyPath: "checks.max_age");
                    return null;
                default:
                    return RowCountChange(map);
            }
        }

        private AcceptedValuesParameters? Values(YamlMapping map)
        {
            if (map.Get("values") is not { } node) { Add(SharedCodes.MissingKey, map, "An `accepted_values` check needs `values`: the list of values the column may hold.", "Add `values: [a, b]`.", "checks.values"); return null; }
            if (node is not YamlSequence { Items.Count: > 0 } seq) { Add(BadParameter, node, "`values` is a list of one or more values.", keyPath: "checks.values"); return null; }
            var values = new List<string>();
            var ok = true;
            foreach (var item in seq.Items)
            {
                if (item is not YamlScalar value) { Add(BadParameter, item, "Entries of `values` are single values, not lists or mappings.", keyPath: "checks.values"); ok = false; }
                else if (values.Contains(value.Value)) { Add(BadParameter, item, $"`{value.Value}` is listed more than once in `values`.", keyPath: "checks.values"); ok = false; }
                else values.Add(value.Value);
            }

            return ok ? new AcceptedValuesParameters(values) : null;
        }

        private RangeParameters? Range(YamlMapping map)
        {
            var min = map.Get("min");
            var max = map.Get("max");
            if (min is null && max is null) { Add(SharedCodes.MissingKey, map, "A `range` check needs `min`, `max` or both.", "Add `min:` or `max:`.", "checks.min"); return null; }
            var low = Bound(min, "min");
            var high = Bound(max, "max");
            if (min is not null && low is null || max is not null && high is null) return null;
            if (low is not null && high is not null)
            {
                if (low.IsNumber != high.IsNumber) { Add(BadParameter, max!, "`min` and `max` are both numbers or both dates.", keyPath: "checks.max"); return null; }
                var above = low.IsNumber ? low.Number > high.Number : low.Moment > high.Moment;
                if (above) { Add(BadParameter, min!, $"`min` ({low.Text}) is above `max` ({high.Text}): no value could pass.", "Swap them, or correct the one that is wrong.", "checks.min"); return null; }
            }

            return new RangeParameters(low, high);
        }

        private RangeBound? Bound(YamlNode? node, string key)
        {
            if (node is null) return null;
            if (node is YamlScalar scalar && RangeBound.TryParse(scalar.Value, out var bound)) return bound;
            Add(BadParameter, node, $"`{key}` is `{(node as YamlScalar)?.Value}`; write a number, or a date as `2026-01-31` or `2026-01-31T12:00:00`.", keyPath: $"checks.{key}");
            return null;
        }

        private ReferentialParameters? Referential(YamlMapping map, IReadOnlyList<string>? columns)
        {
            if (map.Get("references") is not { } node)
            {
                Add(NoDataset, map, "A `referential` check names no target: the dataset and columns the values must exist in.", "Add `references: { dataset: ..., columns: [...] }`.", "checks.references");
                return null;
            }

            if (node is not YamlMapping target) { Add(BadParameter, node, "`references` is a mapping: `dataset` and `column` or `columns`.", keyPath: "checks.references"); return null; }
            CheckKeys(target, ["dataset", "column", "columns"], "`references`");
            var dataset = Dataset(target.Get("dataset"), target, "references.dataset", null);
            List<string>? names = null;
            var one = target.Get("column");
            var many = target.Get("columns");
            if (one is not null && many is not null) Add(SharedCodes.InvalidValue, many, "Write `column` or `columns`, not both.", keyPath: "checks.references.columns");
            else if (one is YamlScalar { Value.Length: > 0 } s) names = [s.Value];
            else if (many is YamlSequence { Items.Count: > 0 } seq && seq.Items.All(i => i is YamlScalar { Value.Length: > 0 })) names = [.. seq.Items.Cast<YamlScalar>().Select(i => i.Value)];
            else if (one is null && many is null) Add(SharedCodes.MissingKey, target, "`references` names no column of the target dataset.", "Add `column:` or `columns:`.", "checks.references.columns");
            else Add(SharedCodes.InvalidValue, (one ?? many)!, "`column` is a column name; `columns` is a list of column names.", keyPath: "checks.references.columns");

            if (dataset is null || names is null) return null;
            if (columns is { Count: > 0 } own && own.Count != names.Count)
            {
                Add(BadParameter, (many ?? one)!, $"The check has {own.Count} column(s) and `references` names {names.Count}: they pair one to one.", keyPath: "checks.references.columns");
                return null;
            }

            return new ReferentialParameters(dataset, names);
        }

        private RowCountChangeParameters? RowCountChange(YamlMapping map)
        {
            var up = map.Get("max_increase_percent");
            var down = map.Get("max_decrease_percent");
            if (up is null && down is null)
            {
                Add(SharedCodes.MissingKey, map, "A `row_count_change` check needs `max_increase_percent`, `max_decrease_percent` or both.", "Add `max_decrease_percent: 20`.", "checks.max_decrease_percent");
                return null;
            }

            var a = Percent(up, "max_increase_percent");
            var b = Percent(down, "max_decrease_percent");
            if (up is not null && a is null || down is not null && b is null) return null;
            return new RowCountChangeParameters(a, b);
        }

        private decimal? Percent(YamlNode? node, string key)
        {
            if (node is null) return null;
            if (node is YamlScalar scalar && decimal.TryParse(scalar.Value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0 && (key == "max_increase_percent" || value <= 100)) return value;
            Add(BadParameter, node, $"`{key}` is `{(node as YamlScalar)?.Value}`; write a percentage, a number from 0{(key == "max_increase_percent" ? "" : " to 100")}.", keyPath: $"checks.{key}");
            return null;
        }
    }
}
