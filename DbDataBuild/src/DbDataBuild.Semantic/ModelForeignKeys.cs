using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Semantic;

/// <summary>
/// The foreign keys a built model declares (`foreign_keys:`, plan 19 J29), checked against the other models of the project: the target is a model, the referenced columns are columns it declares, and they are its
/// `grain`, so the key is many-to-one. The key is a claim about the data and is never created in a database; <see cref="SemanticChecks"/> tests it (`connection check`) and a semantic model reads it as a relationship.
/// A mapped model's keys describe a table that exists and are not checked here (they may point at tables the project does not have).
/// </summary>
public static class ModelForeignKeys
{
    /// <summary>DDB-618 for every key of a built model that does not match its target.</summary>
    public static void Validate(IReadOnlyList<ProjectModel> models, List<Diagnostic> diagnostics)
    {
        var byName = models.GroupBy(m => m.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var model in models.Where(m => m.IsBuilt))
            foreach (var key in model.ForeignKeys)
            {
                void Problem(string message) => diagnostics.Add(new Diagnostic(DiagnosticCatalog.ForeignKeyInvalid, new(model.DefinitionFile, key.Line, 0), $"Foreign key `{key.Name}` of `{model.Name}` {message}"));
                if (!byName.TryGetValue(key.Table, out var target)) { Problem($"references `{key.Table}`, which is not a model of the project."); continue; }
                var missing = key.ReferencedColumns.Where(c => target.Columns.Count > 0 && !target.Columns.Any(t => string.Equals(t.Name, c, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0) { Problem($"references {string.Join(", ", missing.Select(c => $"`{c}`"))}, which `{target.Name}` does not declare."); continue; }
                if (target.Grain.Count == 0) { Problem($"references `{target.Name}`, which has no `grain`: a foreign key points at the columns that identify a row."); continue; }
                if (!target.Grain.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(key.ReferencedColumns))
                    Problem($"references ({string.Join(", ", key.ReferencedColumns)}), but the `grain` of `{target.Name}` is ({string.Join(", ", target.Grain)}).");
            }
    }
}
