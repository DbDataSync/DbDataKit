using DbDataKit.Connections;
using DbDataKit.Connections.DuckDb;
using DbDataKit.Lineage;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Quality.Runner.Tests;

/// <summary>The datasets of connection definitions that carry checks (the serving layer's data sources): identified like the lineage graph, evaluated over the provider's read-only connection.</summary>
public sealed class ConnectionFilesSourceTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly QualityData _data = new();

    public void Dispose()
    {
        _data.Dispose();
        _engines.Dispose();
    }

    [Fact]
    public async Task Each_table_a_check_names_is_a_dataset_under_the_identity_the_lineage_graph_gives_it()
    {
        var definition = _data.Definition();
        definition.CheckDefinitions =
        [
            QualityData.NotNullCustomer(),
            QualityData.Unique(),
            new CheckDefinition("ref", CheckKind.Referential, new CheckAttachment("orders", ["customer_id"]), new ReferentialParameters("main.orders", ["id"])),
        ];
        var source = new ConnectionFilesQualitySource([definition], new ProviderRegistry([new DuckDbProvider()]), (_, _) => ValueTask.FromResult<ResolvedLogin?>(null));
        var store = new QualityStore(_engines.Provision(StateEngineIds.Sqlite).Open([QualityModule.Module], time: new StateTestClock(DateTimeOffset.Parse("2026-11-08T12:00:00Z"))));

        var report = await new QualityRunService(source, store).RunAsync("orders");

        var identity = LineageIdentity.Dataset(definition, null, "orders").ToString();
        var dataset = Assert.Single(report.Datasets);
        Assert.Equal(identity, dataset.Dataset);
        Assert.Equal(3, dataset.Checks.Count);
        Assert.Equal(EvaluationStatus.Fail, dataset.Checks[0].Evaluation.Status);
        Assert.Equal(EvaluationStatus.Pass, dataset.Checks[1].Evaluation.Status);
        // a referential check names its target by the name inside the definition, here with the schema: the null customers are not checked, 10 12 15 are not ids
        Assert.Equal(EvaluationStatus.Fail, dataset.Checks[2].Evaluation.Status);
        Assert.Equal(3, dataset.Checks[2].Evaluation.Measured);
        Assert.Equal(CheckOutcome.Fail, store.Status(identity, "customer_present")!.Outcome);
    }

    [Fact]
    public async Task A_connection_without_checks_is_no_dataset()
    {
        var source = new ConnectionFilesQualitySource([_data.Definition()], new ProviderRegistry([new DuckDbProvider()]), (_, _) => ValueTask.FromResult<ResolvedLogin?>(null));

        Assert.Empty(await source.DatasetsAsync());
    }
}
