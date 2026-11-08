namespace DbDataKit.State.Tests;

/// <summary>Plan 23a FQ17: what a server sends a console about its checks (<see cref="QualityDocuments"/>) and the store's list of every status, on every state engine.</summary>
public sealed class QualityDocumentsTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    private const string Orders = "sqlserver://sql1/sales.dbo.orders";
    private const string Customers = "postgres://pg1/dwh.public.customers";

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private QualityStore Open(string engine) => new(_engines.Provision(engine).Open([QualityModule.Module], time: new StateTestClock(Start)));

    private static CheckResultRecord Result(string dataset, string id, CheckOutcome outcome, DateTimeOffset at, string severity = "block", decimal? measured = null, string[]? samples = null) =>
        new(id, dataset, outcome, at, measured, 0, samples, "run-1", severity, "alert");

    [Theory, MemberData(nameof(Engines))]
    public void The_overview_lists_every_dataset_with_its_checks_the_block_and_how_to_run_them(string engine)
    {
        var store = Open(engine);
        store.Record(Result(Orders, "b_unique", CheckOutcome.Pass, Start));
        store.Record(Result(Orders, "a_not_null", CheckOutcome.Fail, Start, measured: 3, samples: ["order_id=3"]));
        store.Record(Result(Customers, "fresh", CheckOutcome.Pass, Start, severity: "warn"));

        var document = QualityDocuments.Overview(store, canRun: false, runNote: "Run a refresh.");

        Assert.False((bool)document["can_run"]!);
        Assert.Equal("Run a refresh.", (string)document["run_note"]!);
        var datasets = document["datasets"]!.AsArray();
        Assert.Equal([Customers, Orders], datasets.Select(d => (string)d!["dataset"]!));          // by identity
        var orders = datasets[1]!;
        Assert.Equal(1, (int)orders["blocking"]!);
        Assert.Equal(["a_not_null", "b_unique"], orders["checks"]!.AsArray().Select(c => (string)c!["check_id"]!));
        var failing = orders["checks"]![0]!;
        Assert.Equal(("fail", true, 3m), ((string)failing["status"]!, (bool)failing["blocking"]!, (decimal)failing["measured"]!));
        Assert.NotNull(failing["blocked_since"]);
        Assert.False((bool)datasets[0]!["checks"]![0]!["blocking"]!);
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_overview_of_a_store_with_no_result_is_empty_and_the_history_is_newest_first_with_the_sample_keys(string engine)
    {
        var store = Open(engine);
        Assert.Empty(QualityDocuments.Overview(store, true)["datasets"]!.AsArray());

        store.Record(Result(Orders, "a_not_null", CheckOutcome.Fail, Start, measured: 2, samples: ["order_id=3", "order_id=9"]));
        store.Record(Result(Orders, "a_not_null", CheckOutcome.Pass, Start.AddMinutes(5), measured: 0));
        var history = QualityDocuments.History(store, Orders, "a_not_null");

        Assert.Equal((Orders, "a_not_null"), ((string)history["dataset"]!, (string)history["check_id"]!));
        var results = history["results"]!.AsArray();
        Assert.Equal(["pass", "fail"], results.Select(r => (string)r!["status"]!));
        Assert.Equal(["order_id=3", "order_id=9"], results[1]!["sample_keys"]!.AsArray().Select(k => (string)k!));
        Assert.Empty(QualityDocuments.History(store, Orders, "no_such_check")["results"]!.AsArray());
    }
}
