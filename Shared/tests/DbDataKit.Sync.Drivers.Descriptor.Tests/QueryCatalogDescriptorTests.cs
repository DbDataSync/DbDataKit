using System.Data.Common;
using DbDataKit.Sync.Drivers.Generic;
using Xunit;

namespace DbDataKit.Sync.Drivers.Descriptor.Tests;

/// <summary>Phase 167V's <c>catalog: query</c> strategy, at the descriptor-parsing level — the row
/// contract itself (required/optional fields, defaults, column-name matching) is
/// <c>DbDataKit.Sync.Drivers.Generic.Tests.QueryCatalogTests</c>, against a real connection.</summary>
public sealed class QueryCatalogDescriptorTests
{
    private sealed class StubDbProviderFactory : DbProviderFactory;

    private const string BaseYaml = """
        id: fake.query
        displayName: Fake (query catalog)
        library: fake
        dialect:
          quoteIdentifier: doubleQuote
          parameterPrefix: "@"
          rowLimit: limitOffset
          catalog: query
        capabilities:
          readers: [Watermark, BatchReload]
          staging: [StagingTable]
          writers: [DeleteInsert]
        """;

    [Fact]
    public void CatalogQuery_WithNoMetadataQueriesBlock_FailsNamingWhatIsMissing()
    {
        var descriptor = DriverDescriptors.Deserialize(BaseYaml);

        var ex = Assert.Throws<NotSupportedException>(
            () => DriverDescriptors.ToSpec(descriptor, new StubDbProviderFactory()));

        Assert.Contains("fake.query", ex.Message);
        Assert.Contains("metadataQueries", ex.Message);
    }

    [Fact]
    public void CatalogQuery_WithAMetadataQueriesBlock_ProducesAQueryCatalog()
    {
        var yaml = BaseYaml + """

            metadataQueries:
              tableQuery: "SELECT table_name, table_schema FROM sys.tables"
              columnQuery: "SELECT column_name, data_type FROM sys.columns WHERE table_schema = {{schema}} AND table_name = {{table}}"
            """;
        var descriptor = DriverDescriptors.Deserialize(yaml);

        var spec = DriverDescriptors.ToSpec(descriptor, new StubDbProviderFactory());

        Assert.IsType<QueryCatalog>(spec.Catalog);
    }

    [Fact]
    public void AnUnknownCatalogStrategy_FailsNamingWhatIsSupported()
    {
        var descriptor = DriverDescriptors.Deserialize(BaseYaml.Replace("catalog: query", "catalog: nonsense"));

        var ex = Assert.Throws<NotSupportedException>(
            () => DriverDescriptors.ToSpec(descriptor, new StubDbProviderFactory()));

        Assert.Contains("nonsense", ex.Message);
        Assert.Contains("default", ex.Message);
        Assert.Contains("query", ex.Message);
    }
}
