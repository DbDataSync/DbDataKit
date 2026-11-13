using DbDataKit.Serving.Conformance;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 22a GP14, the reference test for the Gateway's own project: the modules of the Gateway (Flight SQL's host glue, the feeds, the <c>mcp</c> tools, the views module and its subscriptions, the apps host's calls, the Store's API) reach rows through the serving core, so no file of
/// the server project outside the core's names a type through which a statement runs against a data source or the semantic engine scans one. A file that does fails the test until it is either changed to ask <c>IServingQueries</c> or <c>IServingEntities</c> or named below with its reason,
/// which is a decision someone has to make and write down.
/// </summary>
public sealed class ModuleReferenceTests
{
    /// <summary>The files (and folders, with a trailing <c>/</c>) that are the core or do not serve rows, and why. Everything else is a module.</summary>
    private static readonly (string Path, string Why)[] Core =
    [
        ("Data/", "the data sources of the server: the catalog that resolves a source for a caller, the limits and the running statements"),
        ("Serving/GatewayServing.cs", "the serving core: the query and catalog services over the data sources"),
        ("Serving/GatewayServing.Queries.cs", "the serving core: the statements run against a data source as the caller"),
        ("Serving/GatewayServing.Catalog.cs", "the serving core: what a data source's catalog shows the caller"),
        ("Serving/GatewayAccess.cs", "the access rules' resolver: reads the names of a rules table with the Gateway's own login"),
        ("Serving/GatewayCache.cs", "the serving cache's policy: asks which sources a caller may use"),
        ("Serving/PassThroughGuard.cs", "plan 22a GP12: the core's guard in front of a statement to a data source (it refuses a table a model puts rules on); present once GP12 is merged"),
        ("Serving/GroupStoreApi.cs", "the Store's own reads and writes of a published entity model's tables, which carry the caller's row and column rules (StoreAccessConformanceTests holds every module that reads an entity to them)"),
        ("Hosting/GatewayHost.cs", "the composition root: registers the data source catalog and the core"),
        ("Management/GatewayDataSources.cs", "an administrator's test of a data source's connection (SELECT 1), not a read of rows"),
        ("Management/GatewaySemanticModels.cs", "an administrator's check that a model's tables exist when it is published, not a read of rows"),
        ("Management/GatewayManagementHost.cs", "the composition root of the management API"),
    ];

    private static string ServerSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "DbDataGateway.Server"))) return Path.Combine(dir.FullName, "src", "DbDataGateway.Server");
        throw new InvalidOperationException("The server project (src/DbDataGateway.Server) was not found above " + AppContext.BaseDirectory);
    }

    private static string Why(string path) => Core.Single(c => c.Path == path).Why;

    private static bool IsCore(string relative) => Core.Any(c => c.Path.EndsWith('/') ? relative.StartsWith(c.Path, StringComparison.Ordinal) : relative == c.Path);

    [Fact]
    public void No_module_file_of_the_server_project_reaches_a_data_source_or_the_engine_outside_the_serving_core()
    {
        var findings = ModuleReferenceRules.CheckSources("DbDataGateway.Server", ServerSource(), IsCore);

        Assert.True(findings.Count == 0, "A module reaches data without the serving core (plan 22a GP14):\n" + string.Join("\n", findings));
    }

    [Fact]
    public void Every_file_the_core_list_names_exists_so_the_list_cannot_go_stale()
    {
        foreach (var (path, _) in Core)
            Assert.True(path.EndsWith('/') ? Directory.Exists(Path.Combine(ServerSource(), path)) : File.Exists(Path.Combine(ServerSource(), path)) || Why(path).Contains("present once", StringComparison.Ordinal), $"{path} is named as the core and is not there; remove it from the list or rename it.");
    }

    [Fact]
    public void A_new_module_file_that_opens_a_connection_fails_the_check()
    {
        var folder = Directory.CreateTempSubdirectory("gateway-module-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "RogueModule.cs"), "namespace Rogue; public sealed class RogueModule(DbDataGateway.Server.Data.DataSourceCatalog sources) { public object Read(string name) => sources.Resolve(null!, name).Executor; }");
            File.WriteAllText(Path.Combine(folder, "Fine.cs"), "namespace Fine; public sealed class FineModule(DbDataKit.Serving.IServingServices serving) { public object Read() => serving.Queries; }");
            var findings = ModuleReferenceRules.CheckSources("rogue", folder, _ => false);

            var found = Assert.Single(findings);
            Assert.Contains("RogueModule.cs uses DataSourceCatalog", found.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
