namespace DbDataBuild.Models;

/// <summary>One seed: the DuckDB query (`seeds/&lt;schema&gt;/&lt;table&gt;.sql`) that generates the rows of the source of the same name.</summary>
public sealed record SeedFile(string Name, string File, string Sql);

/// <summary>
/// The project's seeds (DESIGN.md 15.6): queries in DuckDB's dialect that generate the data of the sources, deterministically from a `seed` and a `scale` (read in the queries with
/// `getvariable('seed')` and `getvariable('scale')`). `seeds/macros.sql`, if there is one, runs first and holds the helpers the seeds share. A seed may read other seeds' tables.
/// </summary>
public sealed record SeedSet(string? Macros, string? MacrosFile, IReadOnlyList<SeedFile> Seeds)
{
    public static readonly SeedSet Empty = new(null, null, []);
    public SeedFile? Find(string name) => Seeds.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class SeedLoader
{
    public const string Directory = "seeds";
    public const string MacrosFile = "seeds/macros.sql";

    public static SeedSet Load(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, Directory);
        if (!System.IO.Directory.Exists(dir)) return SeedSet.Empty;
        var macrosPath = Path.Combine(projectRoot, MacrosFile);
        var macros = File.Exists(macrosPath) ? File.ReadAllText(macrosPath) : null;
        var seeds = System.IO.Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .Where(f => f != MacrosFile)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new SeedFile(f[(Directory.Length + 1)..^".sql".Length].Replace('/', '.'), f, File.ReadAllText(Path.Combine(projectRoot, f))))
            .ToList();
        return new SeedSet(macros, macros == null ? null : MacrosFile, seeds);
    }
}
