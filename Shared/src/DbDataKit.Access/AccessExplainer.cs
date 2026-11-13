using System.Globalization;
using System.Text.Json.Nodes;

namespace DbDataKit.Access;

/// <summary>A row of a rules table as the explanation shows it: the name it holds in the subject column and its mapped columns, as text (null is NULL), keyed by the rules table's column names.</summary>
public sealed record RulesTableRow(string Subject, IReadOnlyDictionary<string, string?> Values);

/// <summary>The values of a filtered dataset that no rule row covers: the number of distinct combinations and the first of them (keyed by the dataset's columns; a NULL is a null value), with the number of rows of the dataset each stands for.</summary>
public sealed record UncoveredValues(int Count, IReadOnlyList<UncoveredValue> Values, bool Truncated);

/// <summary>One combination of the dataset's values that no row of the rules table has, and how many rows of the dataset carry it (so nobody sees them).</summary>
public sealed record UncoveredValue(IReadOnlyDictionary<string, string?> Values, long Rows);

/// <summary>
/// What a host reads from the data for <c>access explain</c> and <c>access coverage</c> (plan 22a GP15). It reads the rules table's rows that name the caller and the values of the dataset that no row covers, in the terms of the data
/// source the rules table is on; it holds no rule of its own (the resolver and <see cref="RowPredicate"/> decide what applies).
/// </summary>
public interface IRulesTableReader
{
    /// <summary>The rows of the rules table of <paramref name="rule"/> whose subject is one of <paramref name="names"/> (as the table spells them), at most <paramref name="limit"/>. Throws when the table cannot be read.</summary>
    IReadOnlyList<RulesTableRow> Rows(RowRule rule, IReadOnlyCollection<string> names, int limit);

    /// <summary>The combinations of the rule's mapped columns in the dataset that no row of the rules table has (any subject), NULL counted: a NULL combination is uncovered unless the table has a NULL row. At most <paramref name="limit"/> combinations are listed; all are counted.</summary>
    UncoveredValues Uncovered(string dataset, RowRule rule, int limit);
}

/// <summary>A rule as it applies to the person explained: <c>Names</c> are the caller's names that appear in the rules table as it spells them (empty when it has no row for the caller), <c>Rows</c> the rows of the table that name the caller (at most <see cref="AccessExplainer.RowLimit"/>), <c>Holds</c> whether the rule holds for the row asked about (null when none was).</summary>
public sealed record RuleExplanation(string Id, AccessEffect Effect, string Table, string Subject, SubjectKind SubjectKind, IReadOnlyList<RuleColumn> Columns, IReadOnlyList<string> Names, IReadOnlyList<RulesTableRow> Rows, bool RowsTruncated, bool? Holds, string? ReadProblem);

/// <summary>A group as it applies to the person explained; <c>Included</c> is whether it includes the row asked about (its includes combined by its match, no exclude holding), null when no row was asked about.</summary>
public sealed record GroupExplanation(string Id, AccessMatch Match, IReadOnlyList<RuleExplanation> Rules, bool? Included);

/// <summary>The answer for one row: whether it is included and the rules that decided (<c>group/rule</c>).</summary>
public sealed record RowVerdict(bool Included, IReadOnlyList<string> DecidedBy, string Text, IReadOnlyDictionary<string, string?> Row);

public sealed record DatasetExplanation(string Dataset, AccessMatch Match, string? Refusal, IReadOnlyList<GroupExplanation> Groups, string Summary, RowVerdict? Verdict, IReadOnlyList<string> MissingColumns);

/// <summary>A column rule that names the caller, and the treatment that wins for the column.</summary>
public sealed record ColumnExplanation(string Dataset, string Column, string Treatment, IReadOnlyList<ColumnRule> Rules);

/// <summary>The state of a rules table in the explanation.</summary>
public sealed record TableExplanation(string Table, string Key, RulesTableState State, TimeSpan Age, TimeSpan MaxAge, DateTimeOffset CountedFrom, DateTimeOffset? LastChanged);

/// <summary>Why the caller can or cannot see rows and columns of a definition (plan 22a GP15).</summary>
public sealed record AccessExplanation(string Definition, string Subject, SubjectKind SubjectKind, IReadOnlyList<string> Names, string Fingerprint, IReadOnlyList<DatasetExplanation> Datasets,
    IReadOnlyList<ColumnExplanation> Columns, IReadOnlyList<TableExplanation> Tables, IReadOnlyList<string> Warnings)
{
    public JsonObject ToJson() => new()
    {
        ["definition"] = Definition,
        ["subject"] = Subject,
        ["subject_kind"] = SubjectKind.ToString().ToLowerInvariant(),
        ["names"] = new JsonArray([.. Names.Select(n => (JsonNode)n)]),
        ["fingerprint"] = Fingerprint,
        ["datasets"] = new JsonArray([.. Datasets.Select(DatasetJson)]),
        ["columns"] = new JsonArray([.. Columns.Select(c => (JsonNode)new JsonObject
        {
            ["dataset"] = c.Dataset, ["column"] = c.Column, ["treatment"] = c.Treatment,
            ["rules"] = new JsonArray([.. c.Rules.Select(r => (JsonNode)new JsonObject { ["subject"] = r.Subject, ["subject_kind"] = r.SubjectKind.ToString().ToLowerInvariant(), ["treatment"] = r.Treatment.ToString().ToLowerInvariant(), ["mask"] = r.Mask?.ToString().ToLowerInvariant() })]),
        })]),
        ["tables"] = new JsonArray([.. Tables.Select(t => (JsonNode)AccessJson.Table(t))]),
        ["warnings"] = new JsonArray([.. Warnings.Select(w => (JsonNode)w)]),
    };

    private static JsonNode DatasetJson(DatasetExplanation d) => new JsonObject
    {
        ["dataset"] = d.Dataset,
        ["match"] = d.Match.ToString().ToLowerInvariant(),
        ["refusal"] = d.Refusal,
        ["summary"] = d.Summary,
        ["groups"] = new JsonArray([.. d.Groups.Select(g => (JsonNode)new JsonObject
        {
            ["id"] = g.Id, ["match"] = g.Match.ToString().ToLowerInvariant(), ["included"] = g.Included,
            ["rules"] = new JsonArray([.. g.Rules.Select(RuleJson)]),
        })]),
        ["verdict"] = d.Verdict is null ? null : new JsonObject
        {
            ["included"] = d.Verdict.Included, ["decided_by"] = new JsonArray([.. d.Verdict.DecidedBy.Select(x => (JsonNode)x)]), ["text"] = d.Verdict.Text,
            ["row"] = AccessJson.Values(d.Verdict.Row),
        },
        ["missing_columns"] = new JsonArray([.. d.MissingColumns.Select(m => (JsonNode)m)]),
    };

    private static JsonNode RuleJson(RuleExplanation r) => new JsonObject
    {
        ["id"] = r.Id,
        ["effect"] = r.Effect.ToString().ToLowerInvariant(),
        ["table"] = r.Table,
        ["subject"] = r.Subject,
        ["subject_kind"] = r.SubjectKind.ToString().ToLowerInvariant(),
        ["columns"] = new JsonArray([.. r.Columns.Select(c => (JsonNode)new JsonObject { ["dataset_column"] = c.DatasetColumn, ["rules_column"] = c.RulesColumn })]),
        ["names"] = new JsonArray([.. r.Names.Select(n => (JsonNode)n)]),
        ["rows"] = new JsonArray([.. r.Rows.Select(row => (JsonNode)new JsonObject { ["subject"] = row.Subject, ["values"] = AccessJson.Values(row.Values) })]),
        ["rows_truncated"] = r.RowsTruncated,
        ["holds"] = r.Holds,
        ["read_problem"] = r.ReadProblem,
    };
}

/// <summary>The JSON the management API and the command line use for the access explanation and coverage.</summary>
public static class AccessJson
{
    public static JsonObject Values(IReadOnlyDictionary<string, string?> values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values) node[key] = value;
        return node;
    }

    public static JsonObject Table(TableExplanation t) => new()
    {
        ["table"] = t.Table, ["key"] = t.Key, ["state"] = t.State.ToString().ToLowerInvariant(),
        ["age_seconds"] = (long)t.Age.TotalSeconds, ["max_age_seconds"] = (long)t.MaxAge.TotalSeconds,
        ["counted_from"] = t.CountedFrom.ToString("o", CultureInfo.InvariantCulture),
        ["last_changed"] = t.LastChanged?.ToString("o", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// <c>access explain</c> (plan 22a GP15): the explanation of what a person or group can read of a definition. It asks the one <see cref="AccessResolver"/> what applies and reads the combination of the rules from the one
/// <see cref="RowPredicate"/> the hosts render as SQL, so the explanation is of the enforcement itself and never a second implementation of it. The only thing it adds is reading the rows of the rules tables that matched.
/// </summary>
public sealed class AccessExplainer(AccessResolver resolver, IRulesTableReader reader)
{
    /// <summary>The most rows of a rules table one rule's explanation lists.</summary>
    public const int RowLimit = 200;

    /// <summary>
    /// Explains <paramref name="caller"/> on a definition. <paramref name="dataset"/> limits the answer to one dataset (null: every dataset that has rules); <paramref name="row"/> is the row asked about (dataset column to value, null for NULL), for
    /// a dataset that has row rules. <paramref name="warnings"/> are the host's own (for a Store entity: that rules limit reads only).
    /// </summary>
    public AccessExplanation Explain(AccessCaller caller, AccessScope scope, string? dataset = null, IReadOnlyDictionary<string, string?>? row = null, IEnumerable<string>? warnings = null, string? subject = null, SubjectKind? kind = null)
    {
        var decision = resolver.Resolve(caller, scope);
        var datasets = new List<DatasetExplanation>();
        foreach (var resolution in decision.Datasets.Where(d => dataset is null || d.Dataset == dataset))
            datasets.Add(ExplainDataset(resolution, row));
        if (dataset is not null && datasets.Count == 0)
            datasets.Add(new DatasetExplanation(dataset, AccessMatch.All, null, [], $"'{dataset}' has no row rules in {scope.Definition}: every row of it is read as it is.", null, []));

        var effective = ColumnTreatments.For(scope.Section, caller.Id, caller.Name, caller.Groups);
        var columns = effective.All.Where(c => dataset is null || c.Dataset == dataset).OrderBy(c => c.Dataset, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal)
            .Select(c => new ColumnExplanation(c.Dataset, c.Column, c.Treatment.ToString(), [.. decision.Columns.Where(r => r.Dataset == c.Dataset && r.Column == c.Column)])).ToList();
        var tables = decision.Tables.Select(t => new TableExplanation(t.Table, t.Key, t.State, t.Age, t.MaxAge, t.Since, t.ChangedAt)).ToList();
        var names = caller.NamesOf(SubjectKind.Person).Concat(caller.NamesOf(SubjectKind.Group)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new AccessExplanation(scope.Definition, subject ?? (string.IsNullOrEmpty(caller.Name) ? caller.Id : caller.Name), kind ?? (caller.Groups.Count == 1 && string.IsNullOrEmpty(caller.Id) ? SubjectKind.Group : SubjectKind.Person),
            names, decision.Fingerprint, datasets, columns, tables, [.. warnings ?? []]);
    }

    private DatasetExplanation ExplainDataset(DatasetResolution resolution, IReadOnlyDictionary<string, string?>? row)
    {
        var read = new Dictionary<RuleResolution, (IReadOnlyList<RulesTableRow> Rows, bool Truncated, string? Problem)>(ReferenceEqualityComparer.Instance);
        foreach (var group in resolution.Groups)
            foreach (var rule in group.Rules)
            {
                if (rule.Names.Count == 0) { read[rule] = ([], false, null); continue; }
                try
                {
                    var rows = reader.Rows(rule.Rule, rule.Names, RowLimit + 1);
                    read[rule] = (rows.Take(RowLimit).ToList(), rows.Count > RowLimit, null);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    read[rule] = ([], false, $"The rules table '{rule.Rule.Table}' could not be read: {ex.Message}");
                }
            }

        // a row asked about: which rules hold for it (the rows of the table that name the caller, compared on the mapped columns)
        var missing = new List<string>();
        var holds = new Dictionary<RuleResolution, bool>(ReferenceEqualityComparer.Instance);
        if (row is not null)
        {
            missing.AddRange(resolution.Groups.SelectMany(g => g.Rules).SelectMany(r => r.Rule.Columns).Select(c => c.DatasetColumn).Distinct(StringComparer.Ordinal).Where(c => !row.ContainsKey(c)).Order(StringComparer.Ordinal));
            foreach (var group in resolution.Groups)
                foreach (var rule in group.Rules)
                    holds[rule] = rule.Names.Count > 0 && read[rule].Rows.Any(r => rule.Rule.Columns.All(c => row.ContainsKey(c.DatasetColumn) && RuleValues.Equal(row[c.DatasetColumn], r.Values.GetValueOrDefault(c.RulesColumn))));
        }

        var evaluated = row is not null && missing.Count == 0;
        bool Holds(RuleResolution rule) => holds.GetValueOrDefault(rule);
        var groups = resolution.Groups.Select(g => new GroupExplanation(g.Group.Id, g.Group.Match,
            [.. g.Rules.Select(r => new RuleExplanation(r.Rule.Id, r.Rule.Effect, r.Rule.Table, r.Rule.Subject, r.Rule.SubjectKind, r.Rule.Columns, r.Names, read[r].Rows, read[r].Truncated, evaluated ? Holds(r) : null, read[r].Problem))],
            evaluated ? RowPredicate.Of(g).Evaluate(Holds) : null)).ToList();

        var verdict = evaluated ? Verdict(resolution, row!, Holds, read) : null;
        return new DatasetExplanation(resolution.Dataset, resolution.Rules.Match, resolution.Refusal, groups, Summary(resolution, read), verdict, missing);
    }

    private static string Summary(DatasetResolution resolution, Dictionary<RuleResolution, (IReadOnlyList<RulesTableRow> Rows, bool Truncated, string? Problem)> read)
    {
        var dataset = resolution.Dataset;
        if (resolution.Refusal is not null) return $"'{dataset}' is not served to them: {resolution.Refusal}";
        var includes = resolution.Groups.SelectMany(g => g.Rules).Where(r => r.Rule.Effect == AccessEffect.Include).ToList();
        if (includes.Count > 0 && includes.All(r => r.Names.Count == 0))
        {
            var tables = string.Join(", ", includes.Select(r => $"'{r.Rule.Table}'").Distinct());
            return $"No row of {tables} names them, so no include rule can hold and they see no rows of '{dataset}'.";
        }

        var holding = includes.Where(r => r.Names.Count > 0).Select(r => $"'{r.Rule.Id}' ({RowsText(read[r].Rows.Count, read[r].Truncated)} of '{r.Rule.Table}')");
        var excluding = resolution.Groups.SelectMany(g => g.Rules).Where(r => r.Rule.Effect == AccessEffect.Exclude && r.Names.Count > 0).Select(r => $"'{r.Rule.Id}'");
        var text = $"They read the rows of '{dataset}' that the rule rows select: {string.Join(", ", holding)}";
        var removed = excluding.ToList();
        return removed.Count == 0 ? text + "." : text + $"; the rows that {string.Join(", ", removed)} match are left out.";
    }

    private static string RowsText(int count, bool truncated) => (truncated ? "more than " : "") + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " row" : " rows");

    private static RowVerdict Verdict(DatasetResolution resolution, IReadOnlyDictionary<string, string?> row, Func<RuleResolution, bool> holds,
        Dictionary<RuleResolution, (IReadOnlyList<RulesTableRow> Rows, bool Truncated, string? Problem)> read)
    {
        string Id(GroupResolution group, RuleResolution rule) => group.Group.Id + "/" + rule.Rule.Id;
        if (resolution.Refusal is not null) return new RowVerdict(false, [], $"The row is not included: '{resolution.Dataset}' is not served to them. {resolution.Refusal}", row);
        var passing = resolution.Groups.Where(g => RowPredicate.Of(g).Evaluate(holds)).ToList();
        var included = RowPredicate.Of(resolution).Evaluate(holds);
        if (included)
        {
            var by = passing.SelectMany(g => g.Rules.Where(r => r.Rule.Effect == AccessEffect.Include && holds(r)).Select(r => (Group: g, Rule: r))).ToList();
            var text = "The row is included: " + string.Join("; ", by.Select(b => $"rule '{Id(b.Group, b.Rule)}' holds ({MatchText(b.Rule, row, read[b.Rule].Rows)})")) + ".";
            return new RowVerdict(true, [.. by.Select(b => Id(b.Group, b.Rule))], text, row);
        }

        // excluded: a group whose includes hold but an exclude rule holds too
        var excluded = resolution.Groups.SelectMany(g => g.Rules.Where(r => r.Rule.Effect == AccessEffect.Exclude && holds(r)).Select(r => (Group: g, Rule: r)))
            .Where(x => RowPredicate.Of(new GroupResolution(x.Group.Group, [.. x.Group.Rules.Where(r => r.Rule.Effect == AccessEffect.Include)])).Evaluate(holds)).ToList();
        if (excluded.Count > 0)
            return new RowVerdict(false, [.. excluded.Select(x => Id(x.Group, x.Rule))],
                "The row is not included: " + string.Join("; ", excluded.Select(x => $"rule '{Id(x.Group, x.Rule)}' removes it ({MatchText(x.Rule, row, read[x.Rule].Rows)})")) + ".", row);

        var includes = resolution.Groups.SelectMany(g => g.Rules.Where(r => r.Rule.Effect == AccessEffect.Include).Select(r => (Group: g, Rule: r))).ToList();
        if (includes.All(i => i.Rule.Names.Count == 0))
            return new RowVerdict(false, [], $"The row is not included: no row of {string.Join(", ", includes.Select(i => $"'{i.Rule.Rule.Table}'").Distinct())} names them, so no include rule holds and they see no rows of '{resolution.Dataset}'.", row);
        var reasons = includes.Select(i => i.Rule.Names.Count == 0
            ? $"rule '{Id(i.Group, i.Rule)}': no row of '{i.Rule.Rule.Table}' names them"
            : $"rule '{Id(i.Group, i.Rule)}': none of the {RowsText(read[i.Rule].Rows.Count, read[i.Rule].Truncated)} of '{i.Rule.Rule.Table}' that name them has the values of the row ({string.Join(", ", i.Rule.Rule.Columns.Select(c => $"{c.DatasetColumn} = {RuleValues.Show(row.GetValueOrDefault(c.DatasetColumn))}"))})"
              + (read[i.Rule].Truncated ? ", and the table has more rows than were read" : ""));
        return new RowVerdict(false, [], "The row is not included: no include rule holds for it. " + string.Join("; ", reasons) + ".", row);
    }

    private static string MatchText(RuleResolution rule, IReadOnlyDictionary<string, string?> row, IReadOnlyList<RulesTableRow> rows)
    {
        var found = rows.FirstOrDefault(r => rule.Rule.Columns.All(c => RuleValues.Equal(row.GetValueOrDefault(c.DatasetColumn), r.Values.GetValueOrDefault(c.RulesColumn))));
        var values = string.Join(", ", rule.Rule.Columns.Select(c => $"{c.RulesColumn} = {RuleValues.Show(found?.Values.GetValueOrDefault(c.RulesColumn))}"));
        return found is null ? $"'{rule.Rule.Table}'" : $"'{rule.Rule.Table}' has a row for '{found.Subject}' with {values}";
    }
}

/// <summary>How the explanation compares a value of a row with a value of a rules table, as text: NULL equals only NULL (QG2, <c>IS NOT DISTINCT FROM</c>); numbers compare as numbers, so <c>10</c> equals <c>10.0</c>; everything else compares exactly, case included (a database's collation may be less strict).</summary>
public static class RuleValues
{
    public static bool Equal(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y)) return x == y;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public static string Show(string? value) => value is null ? "NULL" : "'" + value + "'";
}

/// <summary>
/// <c>access coverage</c> (plan 22a GP15): per rule, the values of the filtered dataset that no row of the rules table covers, NULL counted. Such a value is read by nobody the rule would include, whichever person or group it names; the report says which, so the
/// owner of the rules can add a row (or decide nobody should read them).
/// </summary>
public sealed record RuleCoverage(string Dataset, string Group, string Rule, AccessEffect Effect, string Table, IReadOnlyList<RuleColumn> Columns, int Uncovered, IReadOnlyList<UncoveredValue> Values, bool Truncated, string? Problem)
{
    public JsonObject ToJson() => new()
    {
        ["dataset"] = Dataset, ["group"] = Group, ["rule"] = Rule, ["effect"] = Effect.ToString().ToLowerInvariant(), ["table"] = Table,
        ["columns"] = new JsonArray([.. Columns.Select(c => (JsonNode)new JsonObject { ["dataset_column"] = c.DatasetColumn, ["rules_column"] = c.RulesColumn })]),
        ["uncovered"] = Uncovered,
        ["values"] = new JsonArray([.. Values.Select(v => (JsonNode)new JsonObject { ["values"] = AccessJson.Values(v.Values), ["rows"] = v.Rows })]),
        ["truncated"] = Truncated,
        ["problem"] = Problem,
    };
}

public static class AccessCoverage
{
    /// <summary>The most combinations one rule's report lists.</summary>
    public const int ValueLimit = 100;

    /// <summary>The coverage of every rule of the section (of one dataset when <paramref name="dataset"/> is given). A rule whose rules table or dataset cannot be read reports the problem and no count.</summary>
    public static IReadOnlyList<RuleCoverage> Report(AccessSection section, IRulesTableReader reader, string? dataset = null)
    {
        var report = new List<RuleCoverage>();
        foreach (var rules in section.Row.Where(r => dataset is null || r.Dataset == dataset).OrderBy(r => r.Dataset, StringComparer.Ordinal))
            foreach (var group in rules.Groups)
                foreach (var rule in group.Rules)
                {
                    try
                    {
                        var found = reader.Uncovered(rules.Dataset, rule, ValueLimit);
                        report.Add(new RuleCoverage(rules.Dataset, group.Id, rule.Id, rule.Effect, rule.Table, rule.Columns, found.Count, found.Values, found.Truncated, null));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        report.Add(new RuleCoverage(rules.Dataset, group.Id, rule.Id, rule.Effect, rule.Table, rule.Columns, 0, [], false, ex.Message));
                    }
                }

        return report;
    }
}
