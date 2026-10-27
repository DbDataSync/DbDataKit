using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public sealed partial class QisEngine
{
    /// <summary>The hidden measure holding a prior_period transform's earlier value.</summary>
    private static string PriorName(TransformRef t) => t.Alias + "__prior";

    /// <summary>
    /// Rolls a result set scanned at a finer grain (with the entity) up to the requested grain by the entity aggregate:
    /// average revenue per customer is the average of per-customer revenue, never an average of region averages.
    /// </summary>
    private ResultSet RollEntity(ResultSet raw, List<string> attrs, List<string> measures, EntityRollup via, string id, string hash,
        List<Filter> filters)
    {
        var cols = attrs.Select(d => $"\"{Catalog.ColName(d)}\"").ToList();
        string sql;
        if (!via.ZeroEmpty)
        {
            // One step per attribute, in order, each grouping by the query's attributes and the attributes still to go.
            var inner = $"\"{raw.Id}\"";
            for (int i = 0; i < via.Steps.Count; i++)
            {
                var keys = cols.Concat(via.Steps.Skip(i + 1).Select(s => $"\"{Catalog.ColName(s.Entity)}\"")).ToList();
                var aggs = measures.Select(m => $"{via.Steps[i].Agg.ToUpperInvariant()}(\"{Catalog.ColName(m)}\") AS \"{Catalog.ColName(m)}\"");
                inner = $"(SELECT {string.Join(", ", keys.Concat(aggs))} FROM {inner}" + (keys.Count > 0 ? $" GROUP BY {string.Join(", ", keys)}" : "") + ")";
            }
            sql = $"SELECT * FROM {inner}";
        }
        else
        {
            // Members with no value count as zero. The members are those the source has under the query's filters, so
            // "empty" means no value for a member that exists in this scope, not a member the data has never seen.
            var entityCol = $"\"{Catalog.ColName(via.Entity)}\"";
            var members = Scan(raw.Source!, [via.Entity], filters).Select(r => r[Catalog.ColName(via.Entity)]?.ToString()).Where(v => v is not null)
                .Distinct().Select(v => $"('{v!.Replace("'", "''")}')");
            var keys = cols.Count > 0 ? $"(SELECT DISTINCT {string.Join(", ", cols)} FROM \"{raw.Id}\")" : "(SELECT 1 AS one)";
            var on = string.Join("".PadLeft(0), cols.Select(c => $" AND x.{c} = k.{c}"));
            var aggs = measures.Select(m => $"{via.Agg.ToUpperInvariant()}(COALESCE(x.\"{Catalog.ColName(m)}\", 0)) AS \"{Catalog.ColName(m)}\"");
            var kcols = cols.Select(c => $"k.{c}");
            sql = $"SELECT {string.Join(", ", kcols.Concat(aggs))} FROM {keys} k CROSS JOIN (VALUES {string.Join(", ", members)}) mem(e) " +
                  $"LEFT JOIN \"{raw.Id}\" x ON x.{entityCol} = mem.e{on}" + (cols.Count > 0 ? $" GROUP BY {string.Join(", ", kcols)}" : "");
        }
        var rows = Sandbox.Run(sql, [raw], rowCap + 1);
        var descriptor = Descriptor(id, raw.Source ?? "", attrs, measures, rows.Rows.Count, false, hash);
        descriptor["x_provenance"] = new JsonObject
        {
            ["spec"] = "mashup", ["spec_version"] = SpecVersion, ["mode"] = "engine", ["request_hash"] = hash,
            ["inputs"] = new JsonArray(raw.Id), ["engine_level"] = level,
        };
        return new ResultSet(id, raw.Source, attrs, raw.Covers, descriptor, raw.NativeQuery, rows.Rows);
    }
}
