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

        return result;
    }
}
