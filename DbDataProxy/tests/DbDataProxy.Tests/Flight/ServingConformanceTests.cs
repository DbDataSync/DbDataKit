using DbDataKit.Serving;
using DbDataKit.Serving.Conformance;
using DbDataProxy.Tests.Support;

namespace DbDataProxy.Tests.Flight;

/// <summary>The serving layer's conformance suite (plan 19, N03) over Proxy's own services and a DuckDB data source: what the Flight SQL module relies on holds for the real host as it does for the fake one.</summary>
public class ServingConformanceTests(ProxyFixture proxy) : ServingServicesConformanceTests, IClassFixture<ProxyFixture>
{
    protected override Task<ServingTarget> CreateAsync()
    {
        var alice = new ServingCredentials.Password("alice", ProxyFixture.AlicePassword);
        return Task.FromResult(new ServingTarget
        {
            Services = proxy.Services.GetRequiredService<IServingServices>(),
            Full = alice,
            Wrong = new ServingCredentials.Password("alice", "not-her-password"),
            Source = "duck",
            Open = new ServingRequest.Sql("duck", "SELECT id, name FROM numbers WHERE id < 5"),
            OpenRows = 5,
            VisibleTable = "numbers",
            Missing = new ServingRequest.Sql("duck", "SELECT * FROM no_such_table_zz"),
            UnknownSource = new ServingRequest.Sql("nope", "SELECT 1"),
            Refused = new ServingRefusal(alice, new ServingRequest.Sql("other", "SELECT 1")),         // a source alice is not authorized for
            Write = new ServingRequest.Sql("duck", "UPDATE prices SET note = 'a' WHERE id = 1"),
            RefusedWrite = new ServingRefusal(alice, new ServingRequest.Sql("duck_ro", "UPDATE prices SET note = 'a' WHERE id = 1")),   // a source without AllowWrites
            SourceReadOnly = false,
        });
    }
}
