using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DbDataKit.Serving.Conformance;

/// <summary>One way a module could reach data without the serving core, found in a project file or a source file.</summary>
/// <param name="Module">The project or folder that was checked.</param>
/// <param name="Rule"><c>project</c> (a reference to a connection provider or the semantic engine's executors) or <c>source</c> (a file that names one of their types).</param>
/// <param name="Detail">What was found and where, in words.</param>
public sealed record ReferenceFinding(string Module, string Rule, string Detail)
{
    public override string ToString() => $"{Module}: {Rule}: {Detail}";
}

/// <summary>
/// What a module may not reach by itself (plan 22a GP14): the projects that open a data source (the connection providers and the registry that finds them, the semantic engine's executors) and the types through which a statement runs or the engine scans.
/// A module reaches rows through <see cref="IServingQueries"/> and <see cref="IServingEntities"/> only; the serving core is the one place the rules are applied, so a module that holds its own way to a source has a way around them.
/// </summary>
public sealed class ModuleReferencePolicy
{
    /// <summary>Projects (by file name without <c>.csproj</c>) that a module must not reach, directly or through the projects it references.</summary>
    public IReadOnlySet<string> ForbiddenProjects { get; init; } = DefaultForbiddenProjects;

    /// <summary>Projects a module must not reference itself (the registry that finds the providers: a library such as lineage reaches it for the names of engines, which gives a module no way to a source; the types it holds are <see cref="ForbiddenTokens"/>).</summary>
    public IReadOnlySet<string> ForbiddenDirectProjects { get; init; } = DefaultForbiddenDirectProjects;

    /// <summary>Identifiers a module's source must not use (whole words).</summary>
    public IReadOnlyList<string> ForbiddenTokens { get; init; } = DefaultForbiddenTokens;

    /// <summary>The providers and the executors of the semantic engine: what opens a data source.</summary>
    public static readonly IReadOnlySet<string> DefaultForbiddenProjects = new HashSet<string>(StringComparer.Ordinal)
    {
        "DbDataKit.Connections.SqlServer", "DbDataKit.Connections.Postgres", "DbDataKit.Connections.MySql", "DbDataKit.Connections.Oracle", "DbDataKit.Connections.Odbc",
        "DbDataKit.Connections.DuckDb", "DbDataKit.Connections.FlightSql", "DbDataKit.Connections.Files", "DbDataKit.Semantic.Connections", "DbDataKit.Semantic.Ssas",
    };

    /// <summary>The registry that finds the providers, on top of <see cref="DefaultForbiddenProjects"/>.</summary>
    public static readonly IReadOnlySet<string> DefaultForbiddenDirectProjects = new HashSet<string>(DefaultForbiddenProjects, StringComparer.Ordinal) { "DbDataKit.Connections" };

    /// <summary>The types through which a statement runs against a data source or the semantic engine scans one, and the driver's own connections.</summary>
    public static readonly IReadOnlyList<string> DefaultForbiddenTokens =
    [
        "IDbProvider", "DbProviderBase", "ProviderRegistry", "ISourceExecutor", "QisEngine", "SqlEmitter", "IQueryExecutor", "DataSourceCatalog", "ResolvedDataSource", "ExecuteQueryAsync", "ExecuteNonQueryAsync",
        "SqlConnection", "NpgsqlConnection", "DuckDBConnection", "OdbcConnection", "MySqlConnection", "OracleConnection",
    ];
}

/// <summary>
/// The reference test of the access rules (plan 22a GP14): no module project references a connection provider or the semantic engine's executors, and no module's source names a type through which a statement runs, so the only way to rows is
/// <see cref="IServingQueries"/> and <see cref="IServingEntities"/>. A module that reaches data without the core fails it, and <c>ModuleReferenceRulesTests</c> keeps a negative module that does, so the check is known to fail.
/// </summary>
public static partial class ModuleReferenceRules
{
    /// <summary>The findings for one project: forbidden projects in the closure of its references, and forbidden types in its own source files (the files under its folder, not <c>bin</c> or <c>obj</c>).</summary>
    public static IReadOnlyList<ReferenceFinding> CheckProject(string sharedSource, string projectFile, ModuleReferencePolicy? policy = null)
    {
        policy ??= new ModuleReferencePolicy();
        var findings = new List<ReferenceFinding>();
        var name = Path.GetFileNameWithoutExtension(projectFile);
        foreach (var (project, chain) in Closure(sharedSource, projectFile))
            if (project != name && (policy.ForbiddenProjects.Contains(project) || (chain.Count == 2 && policy.ForbiddenDirectProjects.Contains(project))))
                findings.Add(new ReferenceFinding(name, "project", $"references {project} ({string.Join(" -> ", chain)}), which opens a data source; read through IServingQueries or IServingEntities instead"));
        findings.AddRange(CheckSources(name, Path.GetDirectoryName(projectFile)!, _ => false, policy));
        return findings;
    }

    /// <summary>The findings in the source files of a folder: a file (not one that <paramref name="allowed"/> names, by its path relative to the folder with <c>/</c>) that uses a forbidden type as a word, outside comments and strings.</summary>
    public static IReadOnlyList<ReferenceFinding> CheckSources(string module, string directory, Func<string, bool> allowed, ModuleReferencePolicy? policy = null)
    {
        policy ??= new ModuleReferencePolicy();
        var tokens = new Regex(@"\b(" + string.Join("|", policy.ForbiddenTokens.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
        var findings = new List<ReferenceFinding>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (allowed(relative)) continue;
            var found = tokens.Matches(Code(File.ReadAllText(file))).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToList();
            if (found.Count > 0) findings.Add(new ReferenceFinding(module, "source", $"{relative} uses {string.Join(", ", found)}, which runs a statement against a data source or scans one; ask IServingQueries or IServingEntities"));
        }

        return findings;
    }

    /// <summary>The projects a project reaches through <c>ProjectReference</c> and <c>DbDataKitReference</c> (the shared libraries), each with the chain from the project to it.</summary>
    public static IEnumerable<(string Project, IReadOnlyList<string> Chain)> Closure(string sharedSource, string projectFile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { Path.GetFileNameWithoutExtension(projectFile) };
        var queue = new Queue<(string File, IReadOnlyList<string> Chain)>();
        queue.Enqueue((projectFile, [Path.GetFileNameWithoutExtension(projectFile)]));
        while (queue.Count > 0)
        {
            var (file, chain) = queue.Dequeue();
            foreach (var reference in References(sharedSource, file))
            {
                var name = Path.GetFileNameWithoutExtension(reference);
                if (!seen.Add(name)) continue;
                IReadOnlyList<string> next = [.. chain, name];
                yield return (name, next);
                if (File.Exists(reference)) queue.Enqueue((reference, next));
            }
        }
    }

    private static IEnumerable<string> References(string sharedSource, string projectFile)
    {
        if (!File.Exists(projectFile)) yield break;
        var directory = Path.GetDirectoryName(projectFile)!;
        var document = XDocument.Load(projectFile);
        foreach (var element in document.Descendants())
        {
            var include = (string?)element.Attribute("Include");
            if (include is null) continue;
            if (element.Name.LocalName == "ProjectReference")
                yield return Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/')));
            else if (element.Name.LocalName == "DbDataKitReference")
                yield return Path.Combine(sharedSource, "DbDataKit." + include, "DbDataKit." + include + ".csproj");
        }
    }

    /// <summary>The source without comments, string and character literals, so a word in a sentence or a message is not taken for a use of a type.</summary>
    private static string Code(string text) => Literals().Replace(text, " ");

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/|@""(?:[^""]|"""")*""|\$?""""""[\s\S]*?""""""|\$?""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])'", RegexOptions.Singleline)]
    private static partial Regex Literals();
}
