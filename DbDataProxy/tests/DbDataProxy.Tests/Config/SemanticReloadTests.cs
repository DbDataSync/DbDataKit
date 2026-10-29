using DbDataKit.Serving.Semantic;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Serving;

namespace DbDataProxy.Tests.Config;

public sealed class SemanticReloadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("semantic-reload").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));

    private ProxyOptions Options(params string[] files)
    {
        var options = new ProxyOptions();
        foreach (var file in files) options.Semantic.Models.Add(new SemanticModelOptions { Catalog = file });
        return options;
    }

    [Fact]
    public void A_changed_catalog_replaces_the_model_a_broken_one_leaves_it_and_a_removed_one_stops_serving()
    {
        File.WriteAllText(Path.Combine(_directory, "sales.yaml"), Sample);
        var problems = new List<string>();
        var models = SemanticModelsLoader.Load(Options("sales.yaml"), _directory);
        var configured = SemanticModelsLoader.Reload(models, Options("sales.yaml"), _directory, new HashSet<string>(), problems.Add);
        var first = models.Find("sales_catalog")!;
        Assert.Contains("sales_catalog", configured);

        File.WriteAllText(Path.Combine(_directory, "sales.yaml"), Sample.Replace("Governed sales", "Sales, changed"));
        configured = SemanticModelsLoader.Reload(models, Options("sales.yaml"), _directory, configured, problems.Add);
        Assert.NotSame(first, models.Find("sales_catalog"));

        var second = models.Find("sales_catalog");
        File.WriteAllText(Path.Combine(_directory, "sales.yaml"), "not: [a catalog");
        configured = SemanticModelsLoader.Reload(models, Options("sales.yaml"), _directory, configured, problems.Add);
        Assert.Same(second, models.Find("sales_catalog"));
        Assert.Single(problems);

        SemanticModelsLoader.Reload(models, Options(), _directory, configured, problems.Add);
        Assert.Null(models.Find("sales_catalog"));
    }
}
