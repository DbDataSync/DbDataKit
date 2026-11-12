using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Access;

namespace DbDataKit.Semantic;

/// <summary>A rule in the terms of the source: the rules table and its subject column as the source names them, the caller's names that appear in the table, and the pairs of columns matched together (the dataset's, then the rules table's).</summary>
public sealed record PhysicalRule(string Table, string SubjectColumn, IReadOnlyList<string> Names, IReadOnlyList<(string Column, string RulesColumn)> Pairs);

/// <summary>What the rules of the caller do to one physical table: the predicate over its rows, or the reason it is refused (a rules table expired or unreadable).</summary>
public sealed record TableRowAccess(string Dataset, RowPredicate Predicate, Func<RuleResolution, PhysicalRule> Physical, string? Refusal = null)
{
    /// <summary>Throws when the table is refused to this caller (D2).</summary>
    public void ThrowIfRefused()
    {
        if (Refusal is not null) throw new RowAccessRefusedException(Refusal);
    }
}

/// <summary>A request touched a table the caller's rules refuse (a rules table past its maximum age, or one that cannot be read). The host answers it as a denial.</summary>
public sealed class RowAccessRefusedException(string message) : Exception(message);

/// <summary>
/// The row rules of one caller on a catalog, by physical table (plan 22a GP11): the query planner (<see cref="Emit.SqlEmitter"/>) adds each table's predicate as a semi-join to the rules table wherever it reads the table: the fact table, a joined
/// dimension (the join is then always made, so the rules apply whether or not the request names the dimension) and a dimension read for its members. A table with no entry is not filtered.
/// </summary>
public sealed class RowAccessPlan
{
    private readonly Dictionary<string, TableRowAccess> _tables = new(StringComparer.Ordinal);

    private static readonly Regex Plain = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The source a source id belongs to (<c>sales.fact_returns</c> is of <c>sales</c>): the engine names a source with several fact tables <c>source.fact</c>.</summary>
    internal static string BaseSource(string sourceId) => sourceId.Contains('.') ? sourceId[..sourceId.IndexOf('.')] : sourceId;

    /// <summary>The identity of a physical table in the plan: its name without quotes and in lower case.</summary>
    public static string Normalize(string table) => table.Replace("[", "").Replace("]", "").Replace("\"", "").Replace("`", "").Trim().ToLowerInvariant();

    private static string KeyOf(string source, string table) => BaseSource(source) + "\n" + Normalize(table);

    /// <summary>The entry of a physical table of a source (a source id of the engine, a table as the catalog writes it), or null when the caller's rules do not filter it.</summary>
    public TableRowAccess? For(string source, string table) => _tables.GetValueOrDefault(KeyOf(source, table));

    /// <summary>Whether any table of the source is filtered.</summary>
    public bool Touches(string source) => _tables.Keys.Any(k => k.StartsWith(BaseSource(source) + "\n", StringComparison.Ordinal));

    public int Count => _tables.Count;

    /// <summary>The datasets (tables of the catalog) the plan filters, for the explanation.</summary>
    public IEnumerable<string> Datasets => _tables.Values.Select(t => t.Dataset).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The plan of a decision on a catalog: for each dataset with row rules, the table the catalog says it reads and the predicate of the caller's rules. A decision that has no row rules gives an empty plan. A rule that cannot be put in terms of the
    /// source (a column that is an expression and not a name) refuses its dataset, so nothing is ever read unfiltered.
    /// </summary>
    public static RowAccessPlan Build(Catalog catalog, AccessDecision decision)
    {
        var plan = new RowAccessPlan();
        var tables = (catalog.Source["tables"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        JsonObject? Table(string name) => tables.FirstOrDefault(t => t["name"]?.GetValue<string>() == name);

        string? Physical(JsonObject table, string column)
        {
            var found = table["columns"]?.AsArray().OfType<JsonObject>().FirstOrDefault(c => c["name"]?.GetValue<string>() == column);
            var expression = found?["expression"]?["dialects"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => d["dialect"]?.GetValue<string>() == "ANSI_SQL")?["expression"]?.GetValue<string>();
            var name = expression ?? column;
            return Plain.IsMatch(name) ? name : null;
        }

        foreach (var dataset in decision.Datasets)
        {
            if (Table(dataset.Dataset) is not { } filtered) continue;
            var source = filtered["x_source"]?.GetValue<string>() ?? "";
            var physical = filtered["source"]?.GetValue<string>() ?? dataset.Dataset;
            string? refusal = dataset.Refusal;

            PhysicalRule Map(RuleResolution rule)
            {
                var rules = Table(rule.Rule.Table);
                var subject = rules is null ? null : Physical(rules, rule.Rule.Subject);
                var pairs = new List<(string, string)>();
                foreach (var pair in rule.Rule.Columns)
                {
                    var (a, b) = (Physical(filtered, pair.DatasetColumn), rules is null ? null : Physical(rules, pair.RulesColumn));
                    if (a is null || b is null) { refusal ??= $"The rule '{rule.Rule.Id}' of '{dataset.Dataset}' names a column that is an expression, not a column name of the source, so the rule cannot be applied."; continue; }
                    pairs.Add((a, b));
                }

                if (rules is null || subject is null) refusal ??= $"The rules table '{rule.Rule.Table}' of '{dataset.Dataset}' has no plain subject column '{rule.Rule.Subject}'.";
                return new PhysicalRule(rules?["source"]?.GetValue<string>() ?? rule.Rule.Table, subject ?? rule.Rule.Subject, rule.Names, pairs);
            }

            var predicate = RowPredicate.Of(dataset);
            foreach (var rule in predicate.Rules()) _ = Map(rule);   // finds a rule that cannot be put in the source's terms before the plan is used
            plan._tables[KeyOf(source, physical)] = new TableRowAccess(dataset.Dataset, predicate, Map, refusal);
        }

        return plan;
    }
}
