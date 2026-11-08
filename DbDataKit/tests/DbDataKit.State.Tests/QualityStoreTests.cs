using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 23a FQ06: the <c>quality</c> module on every state engine: its conventions, layout and retention, a result and the block it sets in one transaction, and a format change that is refused naming the database.
/// </summary>
public sealed class QualityStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
    private const string Orders = "sqlserver://sql1/sales.dbo.orders";
    private const string Customers = "suite://store-1/entity/customer";

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private (QualityStore Store, StateDatabase Database, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([QualityModule.Module], time: clock);
        return (new QualityStore(database), database, clock);
    }

    private static CheckResultRecord Result(CheckOutcome outcome, DateTimeOffset at, string id = "orders_customer_present", string dataset = Orders, string severity = "block", string onError = "alert",
        decimal? measured = null, IReadOnlyList<string>? samples = null, string? message = null) =>
        new(id, dataset, outcome, at, measured, 0, samples, "run-1", severity, onError, message);

    private static long Count(StateDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command(QualityModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    // ---- convention, layout, retention declared in the module ----

    [Fact]
    public void The_module_follows_the_conventions_in_both_scopes()
    {
        Assert.Empty(StateConventions.Violations(QualityModule.Module));
        Assert.Empty(StateConventions.Violations(QualityModule.GroupModule));
    }

    [Fact]
    public void The_module_declares_ninety_days_of_history_and_no_rule_for_the_status()
    {
        foreach (var module in new[] { QualityModule.Module, QualityModule.GroupModule })
        {
            var rule = Assert.Single(module.Retention);
            Assert.Equal(("check_result", "evaluated_at", 90d), (rule.Table, rule.TimeColumn, rule.DefaultDays));
            Assert.DoesNotContain(module.Retention, r => r.Table == "check_status");
            Assert.Equal("quality", module.Name);
            Assert.Equal(1, module.Version);
        }

        Assert.Equal(StateScopeKind.Install, QualityModule.Module.Scope);
        Assert.Equal(StateScopeKind.Group, QualityModule.GroupModule.Scope);
        Assert.Equal(["check_result", "check_status"], StateTableDefinitions.Read(QualityModule.Module).Select(t => t.Name).Order());
        Assert.All(StateTableDefinitions.Read(QualityModule.Module), t => Assert.NotEmpty(t.PrimaryKey));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_layout_has_both_tables_in_the_namespace_of_the_module(string engine)
    {
        var (_, database, _) = Open(engine);
        var layout = database.Layout!;
        var ns = layout.Namespace(QualityModule.Name);
        Assert.Equal("quality", ns.Module.Name);
        Assert.Equal(1, Assert.Single(database.RegisteredModules(), m => m.Module == "quality").Version);
        Assert.Equal(0, Count(database, "check_result"));
        Assert.Equal(0, Count(database, "check_status"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Two_groups_on_one_database_each_keep_their_own_copy(string engine)
    {
        var location = _engines.Provision(engine);
        var one = new QualityStore(location.Open([QualityModule.GroupModule], new StateScope(Group: "serving-a"), time: new StateTestClock(Start)));
        var two = new QualityStore(location.Open([QualityModule.GroupModule], new StateScope(Group: "serving-b"), time: new StateTestClock(Start)));

        one.Record(Result(CheckOutcome.Fail, Start));

        Assert.Single(one.Blocks());
        Assert.Empty(two.Blocks());
        Assert.Null(two.Status(Orders, "orders_customer_present"));
        // the instances of one group share the copy
        var again = new QualityStore(location.Open([QualityModule.GroupModule], new StateScope(Group: "serving-a"), time: new StateTestClock(Start)));
        Assert.True(again.Status(Orders, "orders_customer_present")!.Blocking);
    }

    // ---- a result and the block it sets ----

    [Theory, MemberData(nameof(Engines))]
    public void A_result_is_stored_with_every_field_and_the_status_follows_it(string engine)
    {
        var (store, _, _) = Open(engine);
        var status = store.Record(new CheckResultRecord("orders_fresh", Orders, CheckOutcome.Pass, Start, 1800.5m, 21600, null, "run-7", "warn"));

        Assert.Equal(("orders_fresh", Orders, "warn", "alert", CheckOutcome.Pass, 1800.5m, 21600m, Start, false, null), (status.CheckId, status.Dataset, status.Severity, status.OnError, status.Outcome, status.Measured, status.Threshold, status.EvaluatedAt, status.Blocking, status.BlockedSince));
        Assert.Equal(Start, status.StatusSince);

        var stored = Assert.Single(store.History(Orders, "orders_fresh"));
        Assert.Equal((CheckOutcome.Pass, 1800.5m, 21600m, "run-7", Start, status.LastResultId), (stored.Outcome, stored.Measured, stored.Threshold, stored.RunId, stored.EvaluatedAt, stored.Id));
        Assert.Empty(stored.SampleKeys);
        Assert.Null(stored.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_failing_block_check_sets_the_block_with_its_sample_keys_and_a_pass_clears_it(string engine)
    {
        var (store, _, _) = Open(engine);
        var keys = Enumerable.Range(1, 25).Select(i => $"order-{i}").ToList();

        var failed = store.Record(Result(CheckOutcome.Fail, Start, measured: 25, samples: keys));
        Assert.True(failed.Blocking);
        Assert.Equal(Start, failed.BlockedSince);
        Assert.Equal(CheckOutcome.Fail, failed.Outcome);
        Assert.Equal(Start, failed.StatusSince);
        Assert.Equal(keys.Take(QualityStore.MaxSampleKeys), Assert.Single(store.History(Orders, "orders_customer_present")).SampleKeys);
        Assert.Equal("orders_customer_present", Assert.Single(store.Blocks()).CheckId);
        Assert.Equal("orders_customer_present", Assert.Single(store.BlocksOf(Orders)).CheckId);
        Assert.Empty(store.BlocksOf(Customers));

        // a second failure keeps the block and the time it began; the status is as it was since the first failure
        var again = store.Record(Result(CheckOutcome.Fail, Start.AddHours(1), measured: 30));
        Assert.True(again.Blocking);
        Assert.Equal((Start, Start, Start.AddHours(1)), (again.BlockedSince, again.StatusSince, again.EvaluatedAt));
        Assert.Equal(30m, again.Measured);

        var passed = store.Record(Result(CheckOutcome.Pass, Start.AddHours(2), measured: 0));
        Assert.False(passed.Blocking);
        Assert.Null(passed.BlockedSince);
        Assert.Equal((CheckOutcome.Pass, Start.AddHours(2)), (passed.Outcome, passed.StatusSince));
        Assert.Empty(store.Blocks());
        Assert.Equal(3, store.History(Orders, "orders_customer_present").Count);

        // a new failure is a new block with its own start
        Assert.Equal(Start.AddHours(3), store.Record(Result(CheckOutcome.Fail, Start.AddHours(3))).BlockedSince);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_warn_check_never_blocks_and_a_check_whose_severity_became_warn_stops_blocking(string engine)
    {
        var (store, _, _) = Open(engine);
        Assert.False(store.Record(Result(CheckOutcome.Fail, Start, severity: "warn")).Blocking);
        Assert.Empty(store.Blocks());

        store.Record(Result(CheckOutcome.Fail, Start.AddMinutes(1), id: "other", severity: "block"));
        Assert.Single(store.Blocks());
        var softened = store.Record(Result(CheckOutcome.Fail, Start.AddMinutes(2), id: "other", severity: "warn"));
        Assert.False(softened.Blocking);
        Assert.Equal("warn", softened.Severity);
        Assert.Empty(store.Blocks());
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_error_does_not_block_unless_the_check_says_so_and_does_not_lift_a_block_that_stands(string engine)
    {
        var (store, _, _) = Open(engine);
        var error = store.Record(Result(CheckOutcome.Error, Start, message: "the connection is down"));
        Assert.Equal((CheckOutcome.Error, false), (error.Outcome, error.Blocking));
        Assert.Equal("the connection is down", Assert.Single(store.History(Orders, "orders_customer_present")).Message);

        var loud = store.Record(Result(CheckOutcome.Error, Start.AddMinutes(1), id: "strict", onError: "block"));
        Assert.True(loud.Blocking);
        Assert.Equal("block", loud.OnError);
        Assert.Equal(Start.AddMinutes(1), loud.BlockedSince);

        // a block that stands is not lifted by a check that could not run; the status says error, the block says since when
        store.Record(Result(CheckOutcome.Fail, Start.AddMinutes(2)));
        var kept = store.Record(Result(CheckOutcome.Error, Start.AddMinutes(3)));
        Assert.Equal((CheckOutcome.Error, true, (DateTimeOffset?)Start.AddMinutes(2)), (kept.Outcome, kept.Blocking, kept.BlockedSince));
        Assert.Equal(2, store.Blocks().Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_result_older_than_the_status_is_history_only(string engine)
    {
        var (store, _, _) = Open(engine);
        store.Record(Result(CheckOutcome.Pass, Start.AddHours(2)));

        var late = store.Record(Result(CheckOutcome.Fail, Start.AddHours(1)));

        Assert.Equal((CheckOutcome.Pass, false, Start.AddHours(2)), (late.Outcome, late.Blocking, late.EvaluatedAt));
        Assert.Equal([CheckOutcome.Pass, CheckOutcome.Fail], store.History(Orders, "orders_customer_present").Select(r => r.Outcome));
        Assert.Empty(store.Blocks());
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_same_check_id_on_two_datasets_is_two_checks(string engine)
    {
        var (store, _, _) = Open(engine);
        store.Record(Result(CheckOutcome.Fail, Start));
        store.Record(Result(CheckOutcome.Pass, Start, dataset: Customers));

        Assert.Equal((true, false), (store.Status(Orders, "orders_customer_present")!.Blocking, store.Status(Customers, "orders_customer_present")!.Blocking));
        Assert.Single(store.StatusesOf(Orders));
        Assert.Single(store.History(Customers, "orders_customer_present"));
        Assert.Equal(QualityStore.DatasetHash(Orders), QualityStore.DatasetHash(Orders));
        Assert.Equal(64, QualityStore.DatasetHash(Orders).Length);
        Assert.NotEqual(QualityStore.DatasetHash(Orders), QualityStore.DatasetHash(Customers));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_result_and_its_block_are_written_in_one_transaction(string engine)
    {
        var (store, database, _) = Open(engine);
        using (var connection = database.OpenConnection())
        {
            // the status table is gone: the status cannot be written, so the result must not stay behind
            using var drop = database.Command(QualityModule.Name, connection, "DROP TABLE {table:check_status};");
            drop.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => store.Record(Result(CheckOutcome.Fail, Start)));

        Assert.Equal(0, Count(database, "check_result"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Forgetting_a_check_removes_its_status_and_its_block_and_leaves_the_history(string engine)
    {
        var (store, _, _) = Open(engine);
        store.Record(Result(CheckOutcome.Fail, Start));

        Assert.True(store.Forget(Orders, "orders_customer_present"));
        Assert.False(store.Forget(Orders, "orders_customer_present"));

        Assert.Empty(store.Blocks());
        Assert.Null(store.Status(Orders, "orders_customer_present"));
        Assert.Single(store.History(Orders, "orders_customer_present"));
    }

    [Fact]
    public void A_bad_severity_or_on_error_is_refused_before_anything_is_written()
    {
        var store = new QualityStore(_engines.Provision(StateEngineIds.Sqlite).Open([QualityModule.Module]));
        Assert.Contains("warn or block", Assert.Throws<ArgumentException>(() => store.Record(Result(CheckOutcome.Pass, Start, severity: "high"))).Message);
        Assert.Contains("alert or block", Assert.Throws<ArgumentException>(() => store.Record(Result(CheckOutcome.Pass, Start, onError: "ignore"))).Message);
        Assert.Empty(store.History(Orders, "orders_customer_present"));
    }

    // ---- retention ----

    [Theory, MemberData(nameof(Engines))]
    public void Rows_older_than_ninety_days_go_and_the_status_with_its_block_stays(string engine)
    {
        var (store, database, clock) = Open(engine);
        store.Record(Result(CheckOutcome.Pass, Start.AddDays(-100)));
        store.Record(Result(CheckOutcome.Pass, Start.AddDays(-80)));
        store.Record(Result(CheckOutcome.Fail, Start.AddDays(-95), id: "old_block"));   // an old failure whose block still stands
        Assert.Equal(3, Count(database, "check_result"));

        var removed = new RetentionJob(database).Run();

        Assert.Equal(2, removed.Removed["quality.check_result"]);
        Assert.Equal([Start.AddDays(-80)], store.History(Orders, "orders_customer_present").Select(r => r.EvaluatedAt));
        Assert.Empty(store.History(Orders, "old_block"));
        Assert.Equal(2, Count(database, "check_status"));
        Assert.True(store.Status(Orders, "old_block")!.Blocking);

        clock.Advance(TimeSpan.FromDays(30));   // the 80-day-old row is 110 days old now
        new RetentionJob(database).Run();
        Assert.Equal(0, Count(database, "check_result"));
        Assert.Equal(2, Count(database, "check_status"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_quarter_of_a_day_is_six_hours_for_this_module_too(string engine)
    {
        var (store, database, _) = Open(engine);
        store.Record(Result(CheckOutcome.Pass, Start.AddHours(-5), id: "young"));
        store.Record(Result(CheckOutcome.Pass, Start.AddHours(-7), id: "old"));
        var settings = new RetentionSettings { Tables = new Dictionary<string, double> { ["quality.check_result"] = 0.25 } };

        Assert.Equal(1, new RetentionJob(database, settings).Run().Removed["quality.check_result"]);

        Assert.Single(store.History(Orders, "young"));
        Assert.Empty(store.History(Orders, "old"));
    }

    // ---- a format change ----

    [Theory, MemberData(nameof(Engines))]
    public void A_database_written_by_a_newer_quality_module_is_refused_naming_the_module_the_database_and_both_versions(string engine)
    {
        var location = _engines.Provision(engine);
        var newer = new StateModule("quality", StateScopeKind.Install, [.. QualityModule.Module.Migrations, "ALTER TABLE {table:check_result} {{addcolumn}} unit {{key}} NULL;"], retention: QualityModule.Module.Retention);
        location.Open([newer], appliedBy: "newer-quality 2.0");

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([QualityModule.Module]));

        Assert.Contains("'quality'", ex.Message);
        Assert.Contains("version 2", ex.Message);
        Assert.Contains("version 1", ex.Message);
        Assert.Contains("newer-quality 2.0", ex.Message);
        Assert.Contains("state database", ex.Message);
        if (engine == StateEngineIds.Sqlite) Assert.Contains(Path.GetFileName(location.Target), ex.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_edited_first_migration_is_refused_the_way_every_shipped_migration_is(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open([QualityModule.Module]);
        var edited = new StateModule("quality", StateScopeKind.Install, [QualityModule.Module.Migrations[0].Replace("evaluated_at  {{time}} NOT NULL", "evaluated_at  {{time}} NULL", StringComparison.Ordinal)]);
        Assert.NotEqual(QualityModule.Module.Migrations[0], edited.Migrations[0]);

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([edited]));

        Assert.Contains("'quality'", ex.Message);
        Assert.Contains("edited", ex.Message);
        Assert.Contains("state database", ex.Message);
    }
}
