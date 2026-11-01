using DbDataBuild.Models;
using DbDataKit.Dialect;

namespace DbDataBuild.Semantic;

/// <summary>
/// The collision guard of a hashed surrogate key (plan 25 section 1, K06): every key a model owns is tested for being on one row. The key is 64 bits of a hash, so two business keys can in theory share one, and a
/// text part that holds the separator can make two different keys one; the same value on two rows is also just a business key that appears twice. The check does not tell them apart, and says both. A key with history is
/// tested through its version key (the durable key repeats by design). A failing check is a failed test, which stops a build that runs `connection check`.
/// </summary>
public static class KeyChecks
{
    /// <summary>The checks of the keys of every model that declares the key's column, on every connection the model is on.</summary>
    public static IReadOnlyList<SemanticCheck> Generate(IReadOnlyList<ProjectModel> models, ProjectConfig config)
    {
        var checks = new List<SemanticCheck>();
        foreach (var model in models.Where(m => m.IsBuilt).OrderBy(m => m.Name, StringComparer.Ordinal))
            foreach (var key in model.Keys.Where(k => k.Method == KeyMethods.Hash))
            {
                var column = key.History is not null ? key.VersionKey : key.Name;
                if (column is null || !model.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))) continue;     // the model does not hold the key as a column, so there is nothing here to count
                foreach (var connection in model.Connections.Order(StringComparer.Ordinal))
                {
                    if (!config.Connections.TryGetValue(connection, out var settings) || !settings.Kind.IsBuildTarget || SharedQuoting.For(settings.Engine) is not { } dialect) continue;
                    checks.Add(new SemanticCheck($"model.{model.Name}.key.{column}.unique@{connection}", "key_unique", "models", connection, connection, settings.Engine,
                        $"every value of `{column}` is on one row of `{model.Name}` (a repeat is a business key that appears twice, or two business keys whose hashes collided, or a part that holds the separator U+001F)",
                        [model.Name], model.DefinitionFile, key.Line, 0, UniquenessSql(dialect, model.Name, column)));
                }
            }
        return checks;
    }

    /// <summary>The key values that more than one row has (a NULL key is not a repeat: it is the missing member), with how many rows each has.</summary>
    public static string UniquenessSql(ISqlDialect dialect, string model, string column)
    {
        var table = string.Join('.', model.Split('.').Select(dialect.Quote));
        var key = dialect.Quote(column);
        return $"SELECT {key} AS {dialect.Quote("key")}, COUNT(*) AS {dialect.Quote("row_count")} FROM {table} WHERE {key} IS NOT NULL GROUP BY {key} HAVING COUNT(*) > 1";
    }
}
