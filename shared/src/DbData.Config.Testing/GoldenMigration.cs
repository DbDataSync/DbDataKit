using DbData.Config.Yaml;

namespace DbData.Config.Testing;

/// <summary>
/// Golden before/after pairs for migrations (migrations are pure functions of the text). A pair passes when the engine turns <c>before</c> into exactly
/// <c>after</c> (comments and ordering included), the result is valid strict YAML, and running the engine again on <c>after</c> changes nothing.
/// Set the environment variable <c>DBDATA_UPDATE_GOLDEN=1</c> to rewrite the <c>.after</c> files from the current engine output (review the diff).
/// </summary>
public static class GoldenMigration
{
    public const string UpdateVariable = "DBDATA_UPDATE_GOLDEN";

    public static void AssertPair(MigrationEngine engine, string kind, string before, string after, MigrationOptions? options = null, string file = "golden.yml")
    {
        var r = engine.Plan(kind, before, file, options);
        if (r.Status != MigrationStatus.Migrated)
            throw new ConfigTestException($"Expected `{kind}` to migrate but the status was {r.Status}.\n{string.Join("\n", r.Diagnostics)}");
        if (r.NewText != after)
            throw new ConfigTestException($"Migrated text differs from the golden file.\n{UnifiedDiff.Create(after, r.NewText, file)}");

        var d = new List<ConfigDiagnostic>();
        StrictYamlReader.Read(r.NewText, file, d);
        if (d.HasErrors()) throw new ConfigTestException("The migrated document is not valid strict YAML: " + d.First());

        var again = engine.Plan(kind, after, file, (options ?? new MigrationOptions()) with { TargetVersion = r.ToVersion });
        if (again.Status != MigrationStatus.UpToDate || again.NewText != after)
            throw new ConfigTestException($"Migration is not idempotent: planning again on the golden output gave {again.Status}.\n{again.Diff}");
    }

    /// <summary>Checks every <c>name.before.yml</c> in <paramref name="directory"/> against <c>name.after.yml</c>. Returns the number of pairs checked.</summary>
    public static int AssertDirectory(MigrationEngine engine, string kind, string directory, MigrationOptions? options = null)
    {
        var update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        var befores = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.before.yml").Order(StringComparer.Ordinal).ToList() : [];
        if (befores.Count == 0) throw new ConfigTestException($"No `*.before.yml` golden files found in `{directory}`.");
        foreach (var b in befores)
        {
            var afterPath = b[..^".before.yml".Length] + ".after.yml";
            var before = File.ReadAllText(b);
            if (update)
            {
                var r = engine.Plan(kind, before, b, options);
                if (r.Status == MigrationStatus.Migrated) File.WriteAllText(afterPath, r.NewText, new System.Text.UTF8Encoding(false));
            }
            if (!File.Exists(afterPath)) throw new ConfigTestException($"Golden file `{afterPath}` is missing (set {UpdateVariable}=1 to create it).");
            try { AssertPair(engine, kind, before, File.ReadAllText(afterPath), options, Path.GetFileName(b)); }
            catch (ConfigTestException ex) { throw new ConfigTestException($"{Path.GetFileName(b)}: {ex.Message}", ex); }
        }
        return befores.Count;
    }
}
