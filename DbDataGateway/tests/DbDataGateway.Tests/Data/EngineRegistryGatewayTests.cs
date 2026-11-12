using DbDataKit.Connections.Descriptor;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Tests.Auth;
using Grpc.Core;

namespace DbDataGateway.Tests.Data;

/// <summary>Plan 14a SL02: the Gateway takes what it says about an engine from the suite's registry; the old provider list keeps working (adapter first).</summary>
public class EngineRegistryGatewayTests
{
    [Fact]
    public void Every_engine_the_registry_calls_a_gateway_source_has_a_provider_here()
    {
        var providers = GatewaySources.Providers();
        foreach (var engine in EngineRegistry.Builtin.Supporting(EngineRoles.GatewaySource))
            Assert.True(providers.TryGet(engine.Id, out _), $"{engine.Id}: the registry says the Gateway reads it, but GatewaySources registers no provider (engines/{engine.Id}.engine.yaml).");
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mssql")]
    [InlineData("postgres")]
    [InlineData("postgresql")]
    [InlineData("duckdb")]
    [InlineData("odbc:Sales")]
    [InlineData("flightsql")]
    public void The_providers_that_worked_before_still_resolve_by_every_name_they_had(string engine) =>
        Assert.True(GatewaySources.Providers().TryGet(engine, out _), engine);

    [Theory]
    [InlineData("mysql", "MySQL")]
    [InlineData("oracle", "Oracle Database")]
    public void A_data_source_on_an_engine_the_registry_does_not_list_is_refused_with_the_descriptors_words(string engine, string display)
    {
        var message = GatewaySources.Refusal("shop", engine);
        Assert.Contains("'shop'", message);
        Assert.Contains(display, message);
        Assert.Contains("cannot read", message);
        Assert.DoesNotContain("unknown provider", message);

        // the same sentence reaches the caller of a query
        var options = new GatewayOptions { DataSources = { ["shop"] = new DataSourceOptions { Provider = engine, ConnectionString = "x" } } };
        var catalog = new DataSourceCatalog(new StaticMonitor(options), GatewaySources.Providers());
        var ex = Assert.Throws<RpcException>(() => catalog.Resolve(new GatewayPrincipal("u", "password", ["shop"], "shop"), "shop"));
        Assert.Contains(display, ex.Status.Detail);
    }

    [Fact]
    public void A_name_that_is_not_an_engine_at_all_is_unknown()
    {
        Assert.Contains("unknown provider 'nosuch'", GatewaySources.Refusal("shop", "nosuch"));
    }
}
