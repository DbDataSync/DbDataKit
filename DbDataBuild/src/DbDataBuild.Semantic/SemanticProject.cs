using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataKit.Semantic.Authoring;

namespace DbDataBuild.Semantic;

/// <summary>One semantic file of a project: what was read, and what it compiled to (null when it has problems).</summary>
/// <param name="File">Project-relative path with `/`.</param>
public sealed record SemanticModelFile(string File, string Name, AuthoredCatalog? Authored, AuthoredCompilation? Compilation)
{
    /// <summary>The connection each source of the file stands for (`default` is the project's default connection).</summary>
    public IReadOnlyDictionary<string, string> Connections { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The semantic files of a project (`semantic/**/*.yml`, plan 14 R1): found, read strictly, and compiled against the project's models. A file is a catalog written without its physical layer; the layer is
/// generated from the models, so a column that is renamed or a model that is removed is a diagnostic at the line of the semantic file that used it.
/// </summary>
public sealed class SemanticProject
{
    /// <summary>The folder of the project that holds the semantic files.</summary>
    public const string Folder = "semantic";

    private SemanticProject(IReadOnlyList<SemanticModelFile> models, ProjectPhysicalLayer layer) => (Models, Layer) = (models, layer);

    /// <summary>Every semantic file, in path order.</summary>
    public IReadOnlyList<SemanticModelFile> Models { get; }

    /// <summary>The layer they were compiled against.</summary>
    public ProjectPhysicalLayer Layer { get; }

    /// <summary>The project-relative paths of the semantic files, in path order.</summary>
    public static IReadOnlyList<string> Discover(string root)
    {
        var folder = Path.Combine(root, Folder);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.yml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads and compiles every semantic file; what is wrong goes to <paramref name="diagnostics"/>, never an exception.</summary>
    public static SemanticProject Load(string root, IReadOnlyList<ProjectModel> models, ProjectConfig config, List<Diagnostic> diagnostics)
    {
        var layer = new ProjectPhysicalLayer(models, config);
        var loaded = new List<SemanticModelFile>();
        foreach (var file in Discover(root))
        {
            var problems = new List<AuthoredProblem>();
            string text;
            try { text = File.ReadAllText(Path.Combine(root, file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(DiagnosticCatalog.SemanticFileInvalid, new(file, 0, 0), $"the file cannot be read ({e.GetType().Name})."));
                continue;
            }
            var authored = AuthoredCatalog.Read(text, file, problems);
            AuthoredCompilation? compiled = null;
            if (authored != null)
            {
                if (loaded.FirstOrDefault(m => m.Name == authored.Name) is { } same)
                    problems.Add(new AuthoredProblem("semantic_name_duplicate", file, authored.Locate("$.name").Line, authored.Locate("$.name").Column,
                        $"the name `{authored.Name}` is the name of `{same.File}` too: a semantic model's name is unique in the project."));
                else compiled = authored.Compile(layer, problems);
            }
            foreach (var p in problems) diagnostics.Add(ToDiagnostic(p));
            var connections = authored?.SourceUses.Select(u => u.Source).Distinct(StringComparer.Ordinal)
                .Where(s => layer.ConnectionOf(s) != null).ToDictionary(s => s, s => layer.ConnectionOf(s)!, StringComparer.Ordinal) ?? [];
            loaded.Add(new SemanticModelFile(file, authored?.Name ?? Path.GetFileNameWithoutExtension(file), authored, compiled) { Connections = connections });
        }
        return new SemanticProject(loaded, layer);
    }

    /// <summary>The diagnostic for a problem of a semantic file: the code says what kind, the location is the file and line.</summary>
    public static Diagnostic ToDiagnostic(AuthoredProblem problem)
    {
        var at = new SourceLocation(problem.File, problem.Line, problem.Column);
        return problem.Code switch
        {
            "unknown_model" => new Diagnostic(DiagnosticCatalog.SemanticUnknownModel, at, problem.Message),
            "model_not_on_source" => new Diagnostic(DiagnosticCatalog.SemanticModelNotOnConnection, at, problem.Message),
            "unknown_column" => new Diagnostic(DiagnosticCatalog.SemanticUnknownColumn, at, problem.Message),
            "binding_type_mismatch" => new Diagnostic(DiagnosticCatalog.SemanticBindingTypeMismatch, at, problem.Message),
            "relationship_not_many_to_one" => new Diagnostic(DiagnosticCatalog.SemanticRelationshipInvalid, at, problem.Message),
            "unknown_source" => new Diagnostic(DiagnosticCatalog.SemanticUnknownConnection, at, problem.Message),
            "semantic_yaml_invalid" or "semantic_schema_invalid" or "physical_layer_is_generated" or "semantic_name_duplicate" => new Diagnostic(DiagnosticCatalog.SemanticFileInvalid, at, problem.Message),
            _ => new Diagnostic(DiagnosticCatalog.SemanticModelInvalid, at, $"[{problem.Code}] {problem.Message}", Fix: problem.Suggestions is { Count: > 0 } s ? $"One of: {string.Join(", ", s)}." : null),
        };
    }
}
