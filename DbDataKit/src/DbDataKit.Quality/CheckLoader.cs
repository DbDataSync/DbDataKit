using DbDataKit.Config.Yaml;

namespace DbDataKit.Quality;

/// <summary>The result of reading a <c>check/1</c> file: the checks, or the diagnostics (file, line, column) that say why there are none.</summary>
public sealed class CheckLoadResult(IReadOnlyList<CheckDefinition>? checks, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public IReadOnlyList<CheckDefinition>? Checks { get; } = checks;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Checks is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of <c>check/1</c> files: <c>schema: check/1</c>, an optional <c>dataset</c> that the checks which name none are about, and <c>checks</c>, the same section a definition holds in format 2 (<see cref="CheckSection"/>). A newer format is refused naming the file and both
/// numbers, as every format of the suite is.
/// </summary>
public static class CheckLoader
{
    public const string SchemaId = "check/1";
    public const int Version = 1;

    public static CheckLoadResult LoadFile(string path) => Load(File.ReadAllText(path), path);

    public static CheckLoadResult Load(string yaml, string file = "checks.yaml")
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diagnostics);
        if (diagnostics.HasErrors()) return new(null, diagnostics);
        var catalog = DefaultDiagnosticCatalog.Instance;
        if (root is not YamlMapping map)
        {
            diagnostics.Add(catalog.Create(CheckSection.BadFile, file, root?.Line ?? 1, root?.Column ?? 1, "A check file is a mapping with `schema: check/1` and `checks`.", "Start the file with `schema: check/1`."));
            return new(null, diagnostics);
        }

        var schema = map.Get("schema");
        if (schema is not YamlScalar { Value: var text })
        {
            diagnostics.Add(catalog.Create(CheckSection.BadFile, file, (schema ?? map).Line, (schema ?? map).Column, schema is null ? "`schema` is missing." : "`schema` is text.", $"Add `schema: {SchemaId}`."));
            return new(null, diagnostics);
        }

        if (text != SchemaId)
        {
            if (text.StartsWith("check/", StringComparison.Ordinal) && int.TryParse(text["check/".Length..], out var number) && number > Version)
                diagnostics.Add(catalog.Create(SharedCodes.SchemaVersionNewer, file, schema.Line, schema.Column, $"`schema` is `{text}`, but this tool understands `check` files up to version {Version}.", "Upgrade the tool; this file was written by a newer version."));
            else diagnostics.Add(catalog.Create(CheckSection.BadFile, file, schema.Line, schema.Column, $"`schema` is `{text}`.", $"Use `schema: {SchemaId}`."));
            return new(null, diagnostics);
        }

        var allowed = new[] { "schema", "dataset", "checks" };
        foreach (var entry in map.Entries.Where(e => !allowed.Contains(e.Key.Value)))
        {
            var near = DidYouMean.Closest(entry.Key.Value, allowed);
            diagnostics.Add(catalog.Create(SharedCodes.UnknownKey, file, entry.Key.Line, entry.Key.Column, $"Unknown key `{entry.Key.Value}` in the check file." + (near is null ? "" : $" Did you mean `{near}`?"), $"Keys: {string.Join(", ", allowed)}."));
        }

        string? dataset = null;
        if (map.Get("dataset") is { } d)
        {
            if (d is YamlScalar { Value.Length: > 0 } ds) dataset = ds.Value;
            else diagnostics.Add(catalog.Create(SharedCodes.InvalidValue, file, d.Line, d.Column, "`dataset` is a dataset's lineage identity or its name, as text."));
        }

        if (map.Get(CheckSection.Key) is not { } section)
        {
            diagnostics.Add(catalog.Create(SharedCodes.MissingKey, file, map.Line, map.Column, "`checks` is missing.", "Add `checks:` with a `- ` entry for each check."));
            return new(null, diagnostics);
        }

        var checks = CheckSection.Read(section, file, diagnostics, dataset);
        return new(diagnostics.HasErrors() ? null : checks, diagnostics);
    }
}

/// <summary>The JSON Schema of the <c>check/1</c> file and of the <c>checks</c> section (embedded in the assembly).</summary>
public static class CheckSchema
{
    /// <summary>The schema's text. Its <c>$defs.check</c> is one entry of a <c>checks</c> list; the root is a whole file.</summary>
    public static string Text()
    {
        var assembly = typeof(CheckSchema).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("check.schema.json", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}
