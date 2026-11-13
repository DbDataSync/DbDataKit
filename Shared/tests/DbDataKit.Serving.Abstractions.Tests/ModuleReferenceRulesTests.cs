using DbDataKit.Serving.Conformance;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>
/// Plan 22a GP14, the reference test: no module project references a connection provider or the semantic engine's executors, and none names a type through which a statement runs, so the only way to rows is <see cref="IServingQueries"/>
/// and <see cref="IServingEntities"/>. Every project that references a serving library is either a module that is checked or the core, by name, so a new module is not forgotten. The negative modules at the end are known to fail the check.
/// </summary>
public sealed class ModuleReferenceRulesTests : IDisposable
{
    /// <summary>The projects of the shared libraries that serve people: each reaches data, when it does, through the serving core.</summary>
    private static readonly string[] Modules =
    [
        "Serving.FlightSql", "Serving.Notify", "Views", "Views.Hosting", "Views.Export", "Delivery", "Delivery.Runner", "Apps.Host", "Apps.Instances", "Apps.Manifest", "Agents", "Agents.Providers", "Knowledge", "Knowledge.Index", "Identity", "Authoring.Server",
    ];

    /// <summary>
    /// The projects that are the serving core or its test library: they hold the one way to rows. <c>Serving.Semantic</c> is the semantic half of the core (it runs the engine over the data sources the host gives it), <c>Serving.Abstractions</c> its contract, and
    /// <c>Serving.Conformance</c> the suite. The Store's SQL (<c>DbDataKit.Store</c>, <c>DbDataKit.DataModel</c>) is the entity half and is not a module: a host reads an entity through it with the caller's row and column rules (see <c>ModuleAccessConformanceTests</c> in Gateway).
    /// </summary>
    private static readonly string[] Core = ["Serving.Abstractions", "Serving.Semantic", "Serving.Conformance"];

    private readonly string _temp = Directory.CreateTempSubdirectory("module-rules-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string SharedSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "DbDataKit.Serving.Abstractions"))) return Path.Combine(dir.FullName, "src");
        throw new InvalidOperationException("The shared libraries' folder (src/DbDataKit.Serving.Abstractions) was not found above " + AppContext.BaseDirectory);
    }

    private static string Project(string name) => Path.Combine(SharedSource(), "DbDataKit." + name, "DbDataKit." + name + ".csproj");

    [Theory, MemberData(nameof(ModuleNames))]
    public void A_module_project_reaches_data_only_through_the_serving_core(string module)
    {
        Assert.True(File.Exists(Project(module)), $"The module project {module} is not where this test looks for it; rename it here too.");
        Assert.Empty(ModuleReferenceRules.CheckProject(SharedSource(), Project(module)));
    }

    public static IEnumerable<object[]> ModuleNames => Modules.Select(m => new object[] { m });

    [Fact]
    public void Every_project_that_references_a_serving_library_is_a_checked_module_or_the_core()
    {
        var known = Modules.Concat(Core).Select(n => "DbDataKit." + n).ToHashSet(StringComparer.Ordinal);
        var servingLibraries = new[] { "Serving.Abstractions", "Serving.Semantic", "Serving.FlightSql", "Serving.Notify" };
        var unclassified = new List<string>();
        foreach (var project in Directory.EnumerateFiles(SharedSource(), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (known.Contains(name) || project.Contains("/obj/") || project.Contains("\\obj\\") || project.Contains("/tests/") || project.Contains("\\tests\\")) continue;
            var text = File.ReadAllText(project);
            if (servingLibraries.Any(l => text.Contains("DbDataKit." + l + ".csproj", StringComparison.Ordinal) || text.Contains("Include=\"" + l + "\"", StringComparison.Ordinal))) unclassified.Add(name);
        }

        Assert.True(unclassified.Count == 0, $"{string.Join(", ", unclassified)} reference a serving library and are neither checked as a module nor named as the core; add each to Modules (it serves people) or Core in ModuleReferenceRulesTests.");
    }

    [Fact]
    public void A_module_that_references_a_connection_provider_fails_directly_and_through_another_project()
    {
        var direct = Module("direct", "<DbDataKitReference Include=\"Connections.SqlServer\" />");
        Assert.Contains(ModuleReferenceRules.CheckProject(SharedSource(), direct), f => f.Rule == "project" && f.Detail.Contains("DbDataKit.Connections.SqlServer", StringComparison.Ordinal));

        var registry = Module("registry", "<DbDataKitReference Include=\"Connections\" />");
        Assert.Contains(ModuleReferenceRules.CheckProject(SharedSource(), registry), f => f.Rule == "project" && f.Detail.Contains("DbDataKit.Connections ", StringComparison.Ordinal));

        var library = Module("library", "<DbDataKitReference Include=\"Connections.Postgres\" />");
        var viaLibrary = Module("via-library", "<ProjectReference Include=\"..\\library\\library.csproj\" />");
        var found = ModuleReferenceRules.CheckProject(SharedSource(), viaLibrary);
        Assert.Contains(found, f => f.Rule == "project" && f.Detail.Contains("via-library -> library -> DbDataKit.Connections.Postgres", StringComparison.Ordinal));
        Assert.NotEmpty(ModuleReferenceRules.CheckProject(SharedSource(), library));

        var engine = Module("engine", "<DbDataKitReference Include=\"Semantic.Connections\" />");
        Assert.NotEmpty(ModuleReferenceRules.CheckProject(SharedSource(), engine));
    }

    [Fact]
    public void A_module_that_opens_a_connection_or_runs_the_engine_in_its_own_source_fails()
    {
        var module = Module("source", "<DbDataKitReference Include=\"Serving.Abstractions\" />");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(module)!, "Direct.cs"), """
            namespace Rogue;
            public sealed class RogueModule
            {
                // a module that reads a table itself, whatever rules the core would have applied
                public async Task<long> Count(string connectionString)
                {
                    await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                    await connection.OpenAsync();
                    return 0;
                }
            }
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(module)!, "Engine.cs"), "namespace Rogue; public sealed class Scan { public void Run(DbDataKit.Semantic.ISourceExecutor executor) { var engine = new DbDataKit.Semantic.QisEngine(null!, executor, null!); } }");
        var found = ModuleReferenceRules.CheckProject(SharedSource(), module);

        Assert.Contains(found, f => f.Rule == "source" && f.Detail.StartsWith("Direct.cs uses SqlConnection", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Rule == "source" && f.Detail.Contains("ISourceExecutor, QisEngine", StringComparison.Ordinal));
    }

    [Fact]
    public void The_words_of_a_comment_or_a_message_are_not_a_use()
    {
        var module = Module("words", "<DbDataKitReference Include=\"Serving.Abstractions\" />");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(module)!, "Words.cs"), "namespace Fine; public static class Words { /* an SqlConnection is what a provider opens */ public const string Message = \"no SqlConnection here, ask IServingQueries\"; /// <summary>QisEngine</summary>\n public static void Ask() { } }");

        Assert.Empty(ModuleReferenceRules.CheckProject(SharedSource(), module));
    }

    private string Module(string name, string references)
    {
        var directory = Path.Combine(_temp, name);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name + ".csproj");
        File.WriteAllText(file, $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{references}</ItemGroup></Project>");
        return file;
    }
}
