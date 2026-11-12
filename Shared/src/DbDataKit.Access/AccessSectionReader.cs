using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Access;

/// <summary>
/// Reads the <c>access:</c> section of a definition (<c>access/1</c>, plan 22a GP01) from its YAML node, strictly: every problem is a diagnostic with the file, line and column of what is wrong, and a suggestion for a near key.
/// The shape is in <c>schemas/access.schema.json</c>. With an <see cref="IAccessTarget"/> the section is checked against the definition's datasets and columns as well.
/// </summary>
public static partial class AccessSectionReader
{
    public const string UnknownDataset = "DBD-ACC-001";
    public const string UnknownColumn = "DBD-ACC-002";
    public const string BadRulesTable = "DBD-ACC-003";
    public const string NoIncludeRule = "DBD-ACC-004";
    public const string BadMapping = "DBD-ACC-005";
    public const string TypeMismatch = "DBD-ACC-006";
    public const string Duplicate = "DBD-ACC-007";
    public const string MaskNotText = "DBD-ACC-008";
    public const string SharedCache = "DBD-ACC-009";
    public const string CannotFilter = "DBD-ACC-010";
    public const string BadSetting = "DBD-ACC-011";
    public const string NotInThisFormat = "DBD-ACC-012";

    private static readonly string[] SectionKeys = ["max_age", "row", "columns"];
    private static readonly string[] DatasetKeys = ["dataset", "match", "groups"];
    private static readonly string[] GroupKeys = ["id", "match", "rules"];
    private static readonly string[] RuleKeys = ["id", "effect", "table", "subject", "subject_kind", "columns", "max_age"];
    private static readonly string[] ColumnRuleKeys = ["subject", "subject_kind", "dataset", "column", "treatment", "mask"];
    private static readonly string[] Matches = ["all", "any"];
    private static readonly string[] Effects = ["include", "exclude"];
    private static readonly string[] Kinds = ["group", "person"];
    private static readonly string[] Treatments = ["hide", "mask", "null"];
    private static readonly string[] Masks = ["fixed", "hash"];

    /// <summary>The longest maximum age of a rules table: a rules table older than this is never meant.</summary>
    public static readonly TimeSpan LongestMaxAge = TimeSpan.FromDays(31);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    public static partial Regex IdPattern();

    [GeneratedRegex("^([1-9][0-9]{0,5})(s|m|h|d)$")]
    private static partial Regex AgePattern();

    /// <summary>Reads a duration such as <c>90s</c>, <c>30m</c>, <c>6h</c> or <c>2d</c> (a whole number and a unit, at most 31 days).</summary>
    public static bool TryParseAge(string text, out TimeSpan age)
    {
        age = default;
        if (AgePattern().Match(text) is not { Success: true } m) return false;
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        age = m.Groups[2].Value switch { "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) };
        return age <= LongestMaxAge;
    }

    /// <summary>The text of a duration the reader accepts, for a message and for writing a section.</summary>
    public static string AgeText(TimeSpan age) =>
        age.TotalSeconds % 86400 == 0 ? $"{(long)age.TotalDays}d" : age.TotalSeconds % 3600 == 0 ? $"{(long)age.TotalHours}h" : age.TotalSeconds % 60 == 0 ? $"{(long)age.TotalMinutes}m" : $"{(long)age.TotalSeconds}s";

    /// <summary>
    /// Reads the section from its node. Null with diagnostics when it is not valid. <paramref name="target"/> is what the definition says about its datasets, or null to read the shape only.
    /// </summary>
    public static AccessSection? Read(YamlNode node, string file, List<ConfigDiagnostic> diagnostics, IAccessTarget? target = null) =>
        new Reader(file, diagnostics, target).ReadSection(node);

    /// <summary>Reads a section from its own text (the mapping under <c>access:</c>, or the same as JSON): for a host that holds the section apart from the file it came from, and for tests.</summary>
    public static AccessSection? ReadText(string text, string file, List<ConfigDiagnostic> diagnostics, IAccessTarget? target = null) =>
        StrictYamlReader.Read(text, file, diagnostics) is { } node && !diagnostics.HasErrors() ? Read(node, file, diagnostics, target) : null;

    /// <summary>The diagnostic for an <c>access</c> section in a file of the format before it (a format-1 file): names the section and what to change.</summary>
    public static ConfigDiagnostic RefusedInFormat1(string file, YamlNode at, string formatName, string fix) =>
        DefaultDiagnosticCatalog.Instance.Create(NotInThisFormat, file, at.Line, at.Column, $"`access` is a section of format 2 ({formatName}), and this file is format 1.", fix, Key);

    private const string Key = AccessSection.Key;

    private sealed class Reader(string file, List<ConfigDiagnostic> diagnostics, IAccessTarget? target) : YamlFieldReader(file, diagnostics)
    {
        private readonly HashSet<string> _ruleIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _groupIds = new(StringComparer.Ordinal);

        public AccessSection? ReadSection(YamlNode node)
        {
            var before = Diagnostics.Count;
            if (node is not YamlMapping map)
            {
                Add(SharedCodes.InvalidValue, node, "`access` is a mapping with `row` and `columns`.", "Write `access:` with `row:` (row rules) and `columns:` (column rules).", Key);
                return null;
            }

            CheckKeys(map, SectionKeys, "`access`");
            var maxAge = AccessSection.DefaultMaxAge;
            if (map.Get("max_age") is { } ageNode) maxAge = Age(ageNode, "access.max_age") ?? maxAge;

            var row = new List<DatasetRowRules>();
            if (map.Get("row") is { } rowNode)
            {
                if (rowNode is not YamlSequence rows) Add(SharedCodes.InvalidValue, rowNode, "`row` is a list with one entry for each dataset that has row rules.", keyPath: "access.row");
                else
                    foreach (var item in rows.Items)
                        if (ReadDataset(item) is { } dataset) row.Add(dataset);
                foreach (var dup in row.GroupBy(r => r.Dataset).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)))
                    Add(Duplicate, ((YamlSequence)rowNode).Items.OfType<YamlMapping>().Last(m => (m.Get("dataset") as YamlScalar)?.Value == dup.Dataset), $"`{dup.Dataset}` has more than one entry in `row`.", "Give the dataset one entry; put its groups in it.", "access.row.dataset");
            }

            var columns = new List<ColumnRule>();
            if (map.Get("columns") is { } columnsNode)
            {
                if (columnsNode is not YamlSequence list) Add(SharedCodes.InvalidValue, columnsNode, "`columns` is a list of column rules, one mapping each.", keyPath: "access.columns");
                else
                    foreach (var item in list.Items)
                        if (ReadColumnRule(item) is { } rule) columns.Add(rule);
            }

            if (target is { CacheSharedAcrossCallers: true } && (row.Count > 0 || columns.Count > 0))
                Add(SharedCache, map, "`cache.scope` is `shared`, and an entry shared by every caller would show one caller's rows to another.", "Set `cache.scope` to `rules` (callers with the same rules share) or `caller`, or leave the cache off.", Key);

            return Diagnostics.Skip(before).HasErrors() ? null : new AccessSection(maxAge, row, columns);
        }

        private DatasetRowRules? ReadDataset(YamlNode item)
        {
            var before = Diagnostics.Count;
            if (item is not YamlMapping map) { Add(SharedCodes.InvalidValue, item, "Each entry of `row` is a mapping with `dataset`, `match` and `groups`.", keyPath: "access.row"); return null; }
            CheckKeys(map, DatasetKeys, "a `row` entry");
            var dataset = Scalar(map, "dataset", true, map)?.Value;
            var match = Choice(map.Get("match"), Matches, "match", AccessMatch.All, i => (AccessMatch)i, "access.row.match");
            AccessDataset? known = null;
            if (dataset is not null && target is not null)
            {
                known = target.Dataset(dataset);
                if (known is null)
                {
                    var near = DidYouMean.Closest(dataset, target.DatasetNames);
                    Add(UnknownDataset, map.Get("dataset")!, $"`{dataset}` is not a dataset of this definition." + (near is null ? "" : $" Did you mean `{near}`?"), $"Datasets: {string.Join(", ", target.DatasetNames.Take(8))}.", "access.row.dataset");
                }
                else if (target.WhyCannotFilter(known) is { } why) Add(CannotFilter, map.Get("dataset")!, $"Rules cannot filter `{dataset}`: {why}.", "Put the rules on a dataset whose source the serving layer queries.", "access.row.dataset");
            }

            var groups = new List<RuleGroup>();
            if (map.Get("groups") is not { } groupsNode) Add(SharedCodes.MissingKey, map, "A `row` entry needs `groups`.", "Add `groups:` with at least one group of rules.", "access.row.groups");
            else if (groupsNode is not YamlSequence list || list.Items.Count == 0) Add(SharedCodes.InvalidValue, groupsNode, "`groups` is a list with at least one group.", keyPath: "access.row.groups");
            else
                foreach (var g in list.Items)
                    if (ReadGroup(g, dataset, known) is { } group) groups.Add(group);
            return dataset is null || Diagnostics.Skip(before).HasErrors() ? null : new DatasetRowRules(dataset, match, groups);
        }

        private RuleGroup? ReadGroup(YamlNode item, string? dataset, AccessDataset? filtered)
        {
            var before = Diagnostics.Count;
            if (item is not YamlMapping map) { Add(SharedCodes.InvalidValue, item, "Each group is a mapping with `id`, `match` and `rules`.", keyPath: "access.row.groups"); return null; }
            CheckKeys(map, GroupKeys, "a group");
            var id = Id(map, "group", _groupIds);
            var match = Choice(map.Get("match"), Matches, "match", AccessMatch.All, i => (AccessMatch)i, "access.row.groups.match");
            var rules = new List<RowRule>();
            if (map.Get("rules") is not { } rulesNode) Add(SharedCodes.MissingKey, map, "A group needs `rules`.", "Add `rules:` with at least one include rule.", "access.row.groups.rules");
            else if (rulesNode is not YamlSequence list || list.Items.Count == 0) Add(SharedCodes.InvalidValue, rulesNode, "`rules` is a list with at least one rule.", keyPath: "access.row.groups.rules");
            else
            {
                foreach (var r in list.Items)
                    if (ReadRule(r, filtered) is { } rule) rules.Add(rule);
                if (Diagnostics.Skip(before).HasErrors() is false && rules.All(r => r.Effect != AccessEffect.Include))
                    Add(NoIncludeRule, rulesNode, $"The group `{id}` has no include rule: a group of excludes alone would have nothing to remove rows from.", "Add an include rule (the rows the caller may see), then the excludes.", "access.row.groups.rules");
            }

            return id is null || Diagnostics.Skip(before).HasErrors() ? null : new RuleGroup(id, match, rules);
        }

        private RowRule? ReadRule(YamlNode item, AccessDataset? filtered)
        {
            var before = Diagnostics.Count;
            if (item is not YamlMapping map) { Add(SharedCodes.InvalidValue, item, "Each rule is a mapping with `id`, `effect`, `table`, `subject` and `columns`.", keyPath: "access.row.groups.rules"); return null; }
            CheckKeys(map, RuleKeys, "a rule");
            var id = Id(map, "rule", _ruleIds);
            var effect = Choice(map.Get("effect"), Effects, "effect", AccessEffect.Include, i => (AccessEffect)i, "access.row.groups.rules.effect", required: true, owner: map);
            var table = Scalar(map, "table", true, map)?.Value;
            var subject = Scalar(map, "subject", true, map)?.Value;
            var kind = Choice(map.Get("subject_kind"), Kinds, "subject_kind", SubjectKind.Group, i => (SubjectKind)i, "access.row.groups.rules.subject_kind");
            TimeSpan? maxAge = map.Get("max_age") is { } ageNode ? Age(ageNode, "access.row.groups.rules.max_age") : null;
            var columns = ReadMapping(map);

            if (table is not null && filtered is not null && target is not null)
                CheckTable(map, table, subject, columns, filtered);

            return id is null || effect is null || table is null || subject is null || columns is null || Diagnostics.Skip(before).HasErrors()
                ? null
                : new RowRule(id, effect.Value, table, subject, kind, columns, maxAge);
        }

        private List<RuleColumn>? ReadMapping(YamlMapping rule)
        {
            if (rule.Get("columns") is not { } node) { Add(SharedCodes.MissingKey, rule, "A rule needs `columns`: the dataset's column and the rules table's column it is matched with.", "Add `columns: { region: region_code }`.", "access.row.groups.rules.columns"); return null; }
            if (node is not YamlMapping map || map.Entries.Count == 0)
            {
                Add(BadMapping, node, "`columns` is a mapping from a column of the dataset to a column of the rules table, with at least one pair.", "Write `columns: { region: region_code }`; several pairs are matched together.", "access.row.groups.rules.columns");
                return null;
            }

            var pairs = new List<RuleColumn>();
            foreach (var entry in map.Entries)
            {
                if (entry.Value is not YamlScalar { Value.Length: > 0 } value) { Add(BadMapping, entry.Value, $"`{entry.Key.Value}` is matched with the name of a column of the rules table, as text.", keyPath: "access.row.groups.rules.columns"); continue; }
                pairs.Add(new RuleColumn(entry.Key.Value, value.Value));
            }

            foreach (var dup in pairs.GroupBy(p => p.RulesColumn, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                var at = map.Entries.Last(e => (e.Value as YamlScalar)?.Value == dup.Key).Value;
                Add(BadMapping, at, $"The rules column `{dup.Key}` is matched with more than one dataset column.", "Each pair of a rule uses its own rules column.", "access.row.groups.rules.columns");
            }

            return pairs;
        }

        private void CheckTable(YamlMapping rule, string table, string? subject, List<RuleColumn>? columns, AccessDataset filtered)
        {
            var tableNode = rule.Get("table")!;
            if (target!.RulesTable(table, filtered) is not { } rules)
            {
                Add(BadRulesTable, tableNode, $"`{table}` is not a rules table of this definition: a rule's table is a table of the same catalog (or, in a model, `schema.table` on the model's connection) that a Build model produces.", "Add the rules table to the definition's tables, or correct the name.", "access.row.groups.rules.table");
                return;
            }

            if (!string.Equals(rules.Source, filtered.Source, StringComparison.Ordinal))
                Add(BadRulesTable, tableNode, $"The rules table `{table}` is in `{rules.Source}` and the dataset `{filtered.Name}` is in `{filtered.Source}`: a rule is a join, so both are in one target.", "Use a rules table in the same source as the dataset (a Build model builds it there).", "access.row.groups.rules.table");
            if (target.WhyNotRulesTable(rules) is { } why)
                Add(BadRulesTable, tableNode, $"`{table}` cannot be a rules table: {why}.", "Use a table that no dimension, measure or relationship reads.", "access.row.groups.rules.table");
            if (string.Equals(rules.Name, filtered.Name, StringComparison.Ordinal))
                Add(BadRulesTable, tableNode, $"`{table}` is the dataset the rule filters.", "A rules table is a table of its own.", "access.row.groups.rules.table");

            if (rules.Columns is { } ruleColumns && subject is not null && !ruleColumns.ContainsKey(subject))
                Add(UnknownColumn, rule.Get("subject")!, $"`{subject}` is not a column of the rules table `{table}`.{Near(subject, ruleColumns.Keys)}", $"Columns: {string.Join(", ", ruleColumns.Keys.Take(8))}.", "access.row.groups.rules.subject");
            if (columns is null) return;
            var columnsNode = (YamlMapping)rule.Get("columns")!;
            foreach (var pair in columns)
            {
                var entry = columnsNode.Entries.First(e => e.Key.Value == pair.DatasetColumn);
                if (filtered.Columns is { } own && !own.ContainsKey(pair.DatasetColumn))
                    Add(UnknownColumn, entry.Key, $"`{pair.DatasetColumn}` is not a column of `{filtered.Name}`.{Near(pair.DatasetColumn, own.Keys)}", $"Columns: {string.Join(", ", own.Keys.Take(8))}.", "access.row.groups.rules.columns");
                if (rules.Columns is { } theirs && !theirs.ContainsKey(pair.RulesColumn))
                    Add(UnknownColumn, entry.Value, $"`{pair.RulesColumn}` is not a column of the rules table `{table}`.{Near(pair.RulesColumn, theirs.Keys)}", $"Columns: {string.Join(", ", theirs.Keys.Take(8))}.", "access.row.groups.rules.columns");
                if (filtered.Columns?.GetValueOrDefault(pair.DatasetColumn) is { } a && a != AccessColumnKind.Unknown && rules.Columns?.GetValueOrDefault(pair.RulesColumn) is { } b && b != AccessColumnKind.Unknown && a != b)
                    Add(TypeMismatch, entry.Value, $"`{pair.DatasetColumn}` holds {a.ToString().ToLowerInvariant()} values and `{pair.RulesColumn}` holds {b.ToString().ToLowerInvariant()} values; a rule matches equal values.", "Make the rules table's column the kind of the dataset's column (a Build model can cast it).", "access.row.groups.rules.columns");
            }
        }

        private ColumnRule? ReadColumnRule(YamlNode item)
        {
            var before = Diagnostics.Count;
            if (item is not YamlMapping map) { Add(SharedCodes.InvalidValue, item, "Each column rule is a mapping with `subject`, `dataset`, `column` and `treatment`.", keyPath: "access.columns"); return null; }
            CheckKeys(map, ColumnRuleKeys, "a column rule");
            var subject = Scalar(map, "subject", true, map)?.Value;
            var kind = Choice(map.Get("subject_kind"), Kinds, "subject_kind", SubjectKind.Group, i => (SubjectKind)i, "access.columns.subject_kind");
            var dataset = Scalar(map, "dataset", true, map)?.Value;
            var column = Scalar(map, "column", true, map)?.Value;
            var treatment = Choice(map.Get("treatment"), Treatments, "treatment", ColumnTreatment.Hide, i => (ColumnTreatment)i, "access.columns.treatment", required: true, owner: map);
            MaskKind? mask = null;
            if (map.Get("mask") is { } maskNode)
            {
                if (treatment != ColumnTreatment.Mask) Add(BadSetting, maskNode, "`mask` belongs to `treatment: mask`.", "Remove `mask`, or set `treatment: mask`.", "access.columns.mask");
                else mask = Choice(maskNode, Masks, "mask", MaskKind.Fixed, i => (MaskKind)i, "access.columns.mask");
            }
            else if (treatment == ColumnTreatment.Mask) mask = MaskKind.Fixed;

            if (dataset is not null && column is not null && target is not null)
            {
                if (target.Dataset(dataset) is not { } known)
                {
                    var near = DidYouMean.Closest(dataset, target.DatasetNames);
                    Add(UnknownDataset, map.Get("dataset")!, $"`{dataset}` is not a dataset of this definition." + (near is null ? "" : $" Did you mean `{near}`?"), $"Datasets: {string.Join(", ", target.DatasetNames.Take(8))}.", "access.columns.dataset");
                }
                else if (known.Columns is { } columns)
                {
                    if (!columns.TryGetValue(column, out var kindOfColumn))
                        Add(UnknownColumn, map.Get("column")!, $"`{column}` is not a column of `{dataset}`.{Near(column, columns.Keys)}", $"Columns: {string.Join(", ", columns.Keys.Take(8))}.", "access.columns.column");
                    else if (treatment == ColumnTreatment.Mask && kindOfColumn != AccessColumnKind.Text && kindOfColumn != AccessColumnKind.Unknown)
                        Add(MaskNotText, map.Get("treatment")!, $"`{column}` holds {kindOfColumn.ToString().ToLowerInvariant()} values, and only text can be masked.", "Hide the column, or return it as null.", "access.columns.treatment");
                }
            }

            return subject is null || dataset is null || column is null || treatment is null || Diagnostics.Skip(before).HasErrors() ? null : new ColumnRule(kind, subject, dataset, column, treatment.Value, mask);
        }

        private string? Id(YamlMapping map, string what, HashSet<string> seen)
        {
            if (map.Get("id") is not { } node) { Add(SharedCodes.MissingKey, map, $"A {what} needs an `id`.", "Add `id:` with a name such as `by_region`; an explanation and an alert name the rule by it.", $"access.{what}.id"); return null; }
            if (node is not YamlScalar { Value: var text } || !IdPattern().IsMatch(text))
            {
                Add(SharedCodes.InvalidValue, node, "`id` starts with a letter and has letters, digits, `_`, `.` and `-`, at most 64 characters.", keyPath: $"access.{what}.id");
                return null;
            }

            if (!seen.Add(text)) { Add(Duplicate, node, $"The {what} id `{text}` is used twice in `access`.", $"Give each {what} its own id.", $"access.{what}.id"); return null; }
            return text;
        }

        private T Choice<T>(YamlNode? node, string[] names, string key, T fallback, Func<int, T> make, string keyPath) where T : struct => Choice(node, names, key, (T?)fallback, make, keyPath, false, null) ?? fallback;

        private T? Choice<T>(YamlNode? node, string[] names, string key, T? fallback, Func<int, T> make, string keyPath, bool required, YamlMapping? owner) where T : struct
        {
            if (node is null)
            {
                if (required) Add(SharedCodes.MissingKey, owner!, $"`{key}` is missing: {string.Join(" or ", names.Select(n => $"`{n}`"))}.", $"Add `{key}:`.", keyPath);
                return fallback;
            }

            if (node is YamlScalar { Value: var text } && Array.IndexOf(names, text) is var at and >= 0) return make(at);
            var shown = (node as YamlScalar)?.Value;
            var near = shown is null ? null : DidYouMean.Closest(shown, names);
            Add(BadSetting, node, $"`{key}` is `{shown}`; it is {string.Join(" or ", names.Select(n => $"`{n}`"))}." + (near is null ? "" : $" Did you mean `{near}`?"), keyPath: keyPath);
            return fallback;
        }

        private TimeSpan? Age(YamlNode node, string keyPath)
        {
            if (node is YamlScalar { Value: var text } && TryParseAge(text, out var age)) return age;
            Add(BadSetting, node, $"`max_age` is `{(node as YamlScalar)?.Value}`; write a whole number and `s`, `m`, `h` or `d` (at most 31d), for example `30m`.", keyPath: keyPath);
            return null;
        }

        private static string Near(string name, IEnumerable<string> candidates) => DidYouMean.Closest(name, candidates) is { } near ? $" Did you mean `{near}`?" : "";
    }
}
