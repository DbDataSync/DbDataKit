using DbDataKit.Monitoring;

namespace DbDataKit.State.Tests;

/// <summary>Plan 23a FQ16: the <c>quality</c> module read as monitoring findings (<see cref="QualityHealth"/>), on every state engine.</summary>
public sealed class QualityHealthTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    private const string Orders = "sqlserver://sql1/sales.dbo.orders";
    private const string Customers = "postgres://pg1/dwh.public.customers";

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private QualityStore Open(string engine) => new(_engines.Provision(engine).Open([QualityModule.Module], time: new StateTestClock(Start)));

    private static CheckResultRecord Result(string dataset, string id, CheckOutcome outcome, string severity = "block") => new(id, dataset, outcome, Start, 3, 0, null, "run-1", severity, "alert");

    [Theory, MemberData(nameof(Engines))]
    public void A_failing_check_is_a_finding_with_the_dataset_until_it_passes(string engine)
    {
        var store = Open(engine);
        store.Record(Result(Orders, "not_null", CheckOutcome.Fail));
        store.Record(Result(Orders, "unique", CheckOutcome.Pass));
        store.Record(Result(Customers, "fresh", CheckOutcome.Fail, severity: "warn"));

        var findings = store.Findings();
        Assert.Equal([Customers, Orders], findings.Select(f => f.JobId));
        var orders = findings.Single(f => f.JobId == Orders);
        var finding = Assert.Single(orders.Findings);
        Assert.Equal((HealthRule.CheckFailed, Severity.Error), (finding.Rule, finding.Severity));
        Assert.Contains("not_null", finding.Message);
        Assert.Equal(Severity.Warning, findings.Single(f => f.JobId == Customers).Findings.Single().Severity);

        store.Record(new CheckResultRecord("not_null", Orders, CheckOutcome.Pass, Start.AddMinutes(5), 0, 0, null, "run-2", "block", "alert"));
        Assert.Equal([Customers], store.Findings().Select(f => f.JobId));
    }
}
