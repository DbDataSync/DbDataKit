using DbDataBuild.Models;
using DbDataKit.Dialect;

namespace DbDataBuild.Semantic;

/// <summary>
/// One data check generated from a semantic model (plan 14 R1, plan 19 J09): a single SELECT, in the SQL of its connection's engine, that returns the violations. No row means the check passes.
/// </summary>
/// <param name="Name">`semantic.&lt;catalog&gt;.hierarchy.&lt;hierarchy&gt;.&lt;child level&gt;@&lt;connection&gt;`, or `semantic.&lt;catalog&gt;.relationship.&lt;relationship&gt;.orphans|grain@&lt;connection&gt;`.</param>
/// <param name="Kind">`hierarchy`, `relationship_orphans` or `relationship_grain`.</param>
/// <param name="Line">Where the hierarchy or relationship is written in the semantic file.</param>
public sealed record SemanticCheck(string Name, string Kind, string Catalog, string Source, string Connection, string Engine, string Description, IReadOnlyList<string> Models, string File, int Line, int Column, string Sql);

/// <summary>A hierarchy level pair that no check was generated for, and why (the same "unchecked" the semantic layer's own hierarchy check reports).</summary>
public sealed record SemanticCheckSkipped(string Catalog, string Description, string Reason, string File, int Line);

/// <summary>The checks of one semantic model, and what could not be checked.</summary>
public sealed record GeneratedSemanticChecks(IReadOnlyList<SemanticCheck> Checks, IReadOnlyList<SemanticCheckSkipped> Skipped);

/// <summary>
/// Generates the data checks of a semantic model, which the semantic layer relies on and cannot see in the model files: each hierarchy level is many-to-one onto the one above (a child member has one parent: what
/// <c>share_of_parent</c> and rolling up assume), a relationship's keys all exist on the one side (no orphan rows, which a join would drop or leave blank), and the one side's keys are unique (the grain a model declares is true).
/// The checks come from the compiled model, so a hierarchy or relationship added to the semantic file is checked without anything else being written. They read data, so they run against a connection
/// (<c>connection check</c>); they are listed offline by <c>project tests list</c>.
/// </summary>
public static class SemanticChecks
{
    /// <summary>The checks of a model that compiled, for every connection it reads. A model that did not compile has none.</summary>
    public static GeneratedSemanticChecks Generate(SemanticModelFile model, ProjectConfig config)
    {
        var checks = new List<SemanticCheck>();
        var skipped = new List<SemanticCheckSkipped>();
        if (model.Compilation is not { } compiled || model.Authored is not { } authored) return new(checks, skipped);

        string Connection(string source) => model.Connections[source];
        ISqlDialect? Dialect(string source) => SharedQuoting.For(config.Connections[Connection(source)].Engine);
        var catalog = model.Name;

        // hierarchies: each pair of adjacent levels, on every source that binds both to one model
        if (authored.Json["hierarchies"] is System.Text.Json.Nodes.JsonObject hierarchies)
            foreach (var (name, node) in hierarchies)
            {
                var levels = node!["levels"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
                var (line, column) = authored.Locate("$.hierarchies." + name);
                for (var i = 0; i + 1 < levels.Count; i++)
                {
                    var (parent, child) = (levels[i], levels[i + 1]);
                    var made = false;
                    var reason = $"`{parent}` and `{child}` are not bound on one connection";
                    foreach (var source in compiled.Sources.Keys.Order(StringComparer.Ordinal))
                    {
                        var p = compiled.Bindings.FirstOrDefault(b => b.Member == parent && b.Source == source);
                        var c = compiled.Bindings.FirstOrDefault(b => b.Member == child && b.Source == source);
                        if (p is null || c is null) continue;
                        if (p.Model != c.Model) { reason = $"`{parent}` ({p.Model}) and `{child}` ({c.Model}) are in two models, and the check does not join them"; continue; }
                        if (Dialect(source) is not { } dialect) { reason = $"no SQL dialect for the engine of `{Connection(source)}`"; continue; }
                        var table = compiled.Tables.First(t => t.Model == p.Model && t.Source == source).Physical.Name;
                        checks.Add(new SemanticCheck($"semantic.{catalog}.hierarchy.{name}.{child}@{Connection(source)}", "hierarchy", catalog, source, Connection(source), config.Connections[Connection(source)].Engine,
                            $"every member of `{child}` has one parent in `{parent}` (hierarchy `{name}`)", [p.Model], model.File, line, column, HierarchySql(dialect, table, p.Column, c.Column)));
                        made = true;
                    }
                    if (!made) skipped.Add(new SemanticCheckSkipped(catalog, $"hierarchy `{name}`: `{parent}` > `{child}`", reason, model.File, line));
                }
            }

        // relationships: orphan keys on the many side, repeated keys on the one side
        foreach (var r in compiled.Relationships)
        {
            if (r.DeclaredAt is not null) continue;       // a foreign key of a model: tested by ForeignKeyChecks, whether or not a semantic file reads it
            if (Dialect(r.Source) is not { } dialect) continue;
            var from = compiled.Tables.First(t => t.Model == r.FromModel && t.Source == r.Source).Physical.Name;
            var to = compiled.Tables.First(t => t.Model == r.ToModel && t.Source == r.Source).Physical.Name;
            var (line, column) = authored.Locate($"$.relationships[{r.AuthoredIndex}]");
            var baseName = r.Name.Contains('@') ? r.Name[..r.Name.IndexOf('@')] : r.Name;
            checks.Add(new SemanticCheck($"semantic.{catalog}.relationship.{baseName}.orphans@{Connection(r.Source)}", "relationship_orphans", catalog, r.Source, Connection(r.Source), config.Connections[Connection(r.Source)].Engine,
                $"every key of `{r.FromModel}` ({string.Join(", ", r.FromColumns)}) is a key of `{r.ToModel}` (relationship `{baseName}`)", [r.FromModel, r.ToModel], model.File, line, column, OrphansSql(dialect, from, r.FromColumns, to, r.ToColumns)));
            checks.Add(new SemanticCheck($"semantic.{catalog}.relationship.{baseName}.grain@{Connection(r.Source)}", "relationship_grain", catalog, r.Source, Connection(r.Source), config.Connections[Connection(r.Source)].Engine,
                $"`{r.ToModel}` has one row for each ({string.Join(", ", r.ToColumns)}) (relationship `{baseName}`)", [r.ToModel], model.File, line, column, GrainSql(dialect, to, r.ToColumns)));
        }
        return new(checks, skipped);
    }

    private static string Table(ISqlDialect dialect, string physical) => string.Join('.', physical.Split('.').Select(dialect.Quote));

    /// <summary>
    /// Children with more than one parent. A parent that is NULL counts as a parent of its own (a child seen both with and without a parent has two), as the semantic layer's own check counts it.
    /// </summary>
    public static string HierarchySql(ISqlDialect dialect, string table, string parentColumn, string childColumn)
    {
        var (p, c) = (dialect.Quote(parentColumn), dialect.Quote(childColumn));
        var parents = $"COUNT(DISTINCT {p}) + MAX(CASE WHEN {p} IS NULL THEN 1 ELSE 0 END)";
        return $"SELECT {c} AS {dialect.Quote("child")}, {parents} AS {dialect.Quote("parents")} FROM {Table(dialect, table)} WHERE {c} IS NOT NULL GROUP BY {c} HAVING {parents} > 1";
    }

    /// <summary>Keys of the many side (never NULL: a NULL key matches nothing, by design) that the one side lacks, with how many rows carry each.</summary>
    public static string OrphansSql(ISqlDialect dialect, string fromTable, IReadOnlyList<string> fromColumns, string toTable, IReadOnlyList<string> toColumns)
    {
        var select = string.Join(", ", fromColumns.Select(c => $"f.{dialect.Quote(c)} AS {dialect.Quote(c)}"));
        var on = string.Join(" AND ", fromColumns.Zip(toColumns, (f, t) => $"f.{dialect.Quote(f)} = t.{dialect.Quote(t)}"));
        var present = string.Join(" AND ", fromColumns.Select(c => $"f.{dialect.Quote(c)} IS NOT NULL"));
        var group = string.Join(", ", fromColumns.Select(c => $"f.{dialect.Quote(c)}"));
        return $"SELECT {select}, COUNT(*) AS {dialect.Quote("row_count")} FROM {Table(dialect, fromTable)} f LEFT JOIN {Table(dialect, toTable)} t ON {on} WHERE t.{dialect.Quote(toColumns[0])} IS NULL AND {present} GROUP BY {group}";
    }

    /// <summary>Keys of the one side that more than one row has.</summary>
    public static string GrainSql(ISqlDialect dialect, string table, IReadOnlyList<string> columns)
    {
        var select = string.Join(", ", columns.Select(dialect.Quote));
        return $"SELECT {select}, COUNT(*) AS {dialect.Quote("row_count")} FROM {Table(dialect, table)} GROUP BY {select} HAVING COUNT(*) > 1";
    }
}
