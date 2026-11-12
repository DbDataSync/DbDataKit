using System.Text.RegularExpressions;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The columns a catalog's bindings, measures and joins name, by catalog source and physical table (plan 14a SL06): what a publish checks with one
/// <c>SELECT columns FROM table WHERE 1 = 0</c> per table. Only a plain column name is a column to check; an expression (a measure over
/// <c>a * b</c>, the <c>*</c> of a count) is the engine's to run and is left alone, as is a DAX binding.
/// </summary>
public static partial class MappedColumns
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Plain();

    /// <summary>The tables of one catalog source and the columns of each (ordered, case-insensitive), the variants of the source (<c>id.variant</c>) included.</summary>
    public static Dictionary<string, SortedSet<string>> Of(Catalog catalog, string source)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string table, string? column)
        {
            if (string.IsNullOrWhiteSpace(table)) return;
            if (!result.TryGetValue(table, out var set)) result[table] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (column is not null && Plain().IsMatch(column)) set.Add(column);
        }

        foreach (var (id, def) in catalog.Doc.Sources.Where(s => SemanticModel.BaseSource(s.Key) == source))
        {
            Add(def.Fact.Table, null);
            foreach (var join in def.Joins.Values)
            {
                Add(join.Table, null);
                Add(join.Table, join.DateColumn);
            }

            foreach (var attribute in catalog.Doc.Attributes.Values)
                if (attribute.Bindings.TryGetValue(id, out var binding) && binding.Dax is null)
                {
                    var table = binding.Join is { } key && def.Joins.TryGetValue(key, out var join) ? join.Table : def.Fact.Table;
                    Add(table, binding.Column);
                }

            foreach (var measure in catalog.Doc.Measures.Values)
                if (measure.Derived is null && measure.Dax is null && measure.Source == id) Add(def.Fact.Table, measure.Column);
        }

        AddAccess(catalog, source, Add);
        return result;
    }

    /// <summary>
    /// The tables and columns the catalog's access section (plan 22a GP01, GP02) reads on a source: each rules table with its subject column and the columns its rules match, and the filtered table with the columns the rules and the column rules
    /// name. They are read at publish like every table the model reads, so a rules table that does not exist on the data source, or lacks a column, is refused naming it, and a rule is never published over a table nobody built.
    /// </summary>
    private static void AddAccess(Catalog catalog, string source, Action<string, string?> add)
    {
        if (catalog.Access is not { } access || catalog.Source["tables"] is not System.Text.Json.Nodes.JsonArray tables) return;
        System.Text.Json.Nodes.JsonObject? Table(string name) => tables.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(t => t["name"]?.GetValue<string>() == name);
        bool OnSource(System.Text.Json.Nodes.JsonObject table) => SemanticModel.BaseSource(table["x_source"]?.GetValue<string>() ?? "") == source;

        // the physical column behind a catalog column: the plain name its ANSI expression gives, or the catalog column's own name
        string? Physical(System.Text.Json.Nodes.JsonObject table, string column)
        {
            var found = table["columns"]?.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(c => c["name"]?.GetValue<string>() == column);
            var expression = found?["expression"]?["dialects"]?.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(d => d["dialect"]?.GetValue<string>() == "ANSI_SQL")?["expression"]?.GetValue<string>();
            return expression is not null && Plain().IsMatch(expression) ? expression : column;
        }

        foreach (var dataset in access.Row)
        {
            if (Table(dataset.Dataset) is not { } filtered || !OnSource(filtered)) continue;
            add(filtered["source"]!.GetValue<string>(), null);
            foreach (var rule in dataset.Groups.SelectMany(g => g.Rules))
            {
                if (Table(rule.Table) is not { } rules) continue;
                add(rules["source"]!.GetValue<string>(), Physical(rules, rule.Subject));
                foreach (var pair in rule.Columns)
                {
                    add(filtered["source"]!.GetValue<string>(), Physical(filtered, pair.DatasetColumn));
                    add(rules["source"]!.GetValue<string>(), Physical(rules, pair.RulesColumn));
                }
            }
        }

        foreach (var rule in access.Columns)
            if (Table(rule.Dataset) is { } table && OnSource(table)) add(table["source"]!.GetValue<string>(), Physical(table, rule.Column));
    }
}
