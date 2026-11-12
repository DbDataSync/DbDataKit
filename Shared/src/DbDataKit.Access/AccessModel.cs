namespace DbDataKit.Access;

/// <summary>What a rule does to the rows it matches: an include rule selects them, an exclude rule removes them.</summary>
public enum AccessEffect { Include, Exclude }

/// <summary>How the parts of a group (or the groups of a dataset) combine: all of them must hold, or any one of them.</summary>
public enum AccessMatch { All, Any }

/// <summary>Whether the names in a rules table's subject column are groups or people.</summary>
public enum SubjectKind { Group, Person }

/// <summary>What a column rule does with a column: <c>Hide</c> removes it, <c>Mask</c> returns a mask of the value, <c>Null</c> returns NULL of the column's type.</summary>
public enum ColumnTreatment { Hide, Mask, Null }

/// <summary>The kinds of mask (plan 22a GP13): <c>Fixed</c> is one constant for every value, so the length is hidden; <c>Hash</c> is a keyed hash, deterministic per kind and key and never reversible.</summary>
public enum MaskKind { Fixed, Hash }

/// <summary>One pair of a rule: the column of the filtered dataset and the column of the rules table it is matched with. Several pairs of one rule match together, as a tuple.</summary>
public sealed record RuleColumn(string DatasetColumn, string RulesColumn);

/// <summary>
/// One rule: the rows of <see cref="Table"/> that name the caller (in <see cref="Subject"/>, as a person or a group) and whose <see cref="Columns"/> equal the row's values (<c>IS NOT DISTINCT FROM</c>, so NULL matches only NULL).
/// <see cref="MaxAge"/> overrides the section's maximum age of the rules table.
/// </summary>
public sealed record RowRule(string Id, AccessEffect Effect, string Table, string Subject, SubjectKind SubjectKind, IReadOnlyList<RuleColumn> Columns, TimeSpan? MaxAge = null);

/// <summary>A group of rules: its <see cref="Match"/> combines its include rules; its exclude rules always subtract (plan 22a QR1).</summary>
public sealed record RuleGroup(string Id, AccessMatch Match, IReadOnlyList<RowRule> Rules);

/// <summary>The row rules of one dataset (a table of a catalog, an entity of a model): <see cref="Match"/> combines its groups.</summary>
public sealed record DatasetRowRules(string Dataset, AccessMatch Match, IReadOnlyList<RuleGroup> Groups);

/// <summary>A column rule: for the person or group <see cref="Subject"/>, <see cref="Column"/> of <see cref="Dataset"/> is treated as <see cref="Treatment"/>. <see cref="Mask"/> is set for a mask.</summary>
public sealed record ColumnRule(SubjectKind SubjectKind, string Subject, string Dataset, string Column, ColumnTreatment Treatment, MaskKind? Mask = null);

/// <summary>
/// The <c>access:</c> section of a definition (<c>access/1</c>): the default maximum age of a rules table, the row rules per dataset and the column rules. It is part of format 2 of the semantic package and of DbDataStore's <c>model</c>.
/// </summary>
public sealed record AccessSection(TimeSpan MaxAge, IReadOnlyList<DatasetRowRules> Row, IReadOnlyList<ColumnRule> Columns)
{
    /// <summary>The maximum age of a rules table when the section says nothing.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(1);

    public const string Key = "access";

    /// <summary>The row rules of a dataset, or null when it has none.</summary>
    public DatasetRowRules? RowRulesOf(string dataset) => Row.FirstOrDefault(r => r.Dataset == dataset);

    /// <summary>The rules tables the section names, each once, with the maximum age that applies to it (the smallest of the rules that name it).</summary>
    public IReadOnlyDictionary<string, TimeSpan> RulesTables()
    {
        var tables = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var rule in Row.SelectMany(r => r.Groups).SelectMany(g => g.Rules))
        {
            var age = rule.MaxAge ?? MaxAge;
            tables[rule.Table] = tables.TryGetValue(rule.Table, out var known) && known < age ? known : age;
        }
        return tables;
    }

    /// <summary>Whether the section has any rule at all.</summary>
    public bool IsEmpty => Row.Count == 0 && Columns.Count == 0;
}
