using DbDataBuild.Models;
using DbDataKit.Dialect;

namespace DbDataBuild.Semantic;

/// <summary>
/// The data checks of the foreign keys the models declare (plan 19 J29), the same two a semantic relationship gets: every non-NULL key of the model that declares it exists in the target (orphans), and the target has one row
/// for each key (its declared grain is true). Whether or not a semantic file reads the key as a relationship, it is tested. Run by `connection check`, listed by `project tests list`.
/// </summary>
public static class ForeignKeyChecks
{
    /// <summary>The checks of every foreign key whose two models are in the project, on every connection both are on.</summary>
    public static IReadOnlyList<SemanticCheck> Generate(IReadOnlyList<ProjectModel> models, ProjectConfig config)
    {
        var byName = models.GroupBy(m => m.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var checks = new List<SemanticCheck>();
        foreach (var model in models.OrderBy(m => m.Name, StringComparer.Ordinal))
            foreach (var key in model.ForeignKeys)
            {
                if (!byName.TryGetValue(key.Table, out var target)) continue;
                foreach (var connection in model.Connections.Intersect(target.Connections, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if (!config.Connections.TryGetValue(connection, out var settings) || !settings.Kind.IsBuildTarget || SharedQuoting.For(settings.Engine) is not { } dialect) continue;
                    var (from, to) = (model.Name, target.Name);
                    checks.Add(new SemanticCheck($"model.{from}.foreign_key.{key.Name}.orphans@{connection}", "foreign_key_orphans", "models", connection, connection, settings.Engine,
                        $"every key of `{from}` ({string.Join(", ", key.Columns)}) is a key of `{to}` (foreign key `{key.Name}`)", [from, to], model.DefinitionFile, key.Line, 0,
                        SemanticChecks.OrphansSql(dialect, from, key.Columns, to, key.ReferencedColumns)));
                    checks.Add(new SemanticCheck($"model.{from}.foreign_key.{key.Name}.grain@{connection}", "foreign_key_grain", "models", connection, connection, settings.Engine,
                        $"`{to}` has one row for each ({string.Join(", ", key.ReferencedColumns)}) (foreign key `{key.Name}`)", [to], model.DefinitionFile, key.Line, 0,
                        SemanticChecks.GrainSql(dialect, to, key.ReferencedColumns)));
                }
            }
        return checks;
    }
}
