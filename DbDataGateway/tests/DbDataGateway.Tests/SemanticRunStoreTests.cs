using System.Text.Json.Nodes;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;

namespace DbDataGateway.Tests;

/// <summary>
/// Plan 14a SL23 (D21): the <c>semantic_runs</c> module and its store, on SQLite always and on SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> are set (testenv): the module's
/// conventions, a record written and read back, a person reading only their own (an administrator everyone's), counts by error code, and the 30 days of retention.
/// </summary>
public sealed class SemanticRunStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-runs-").FullName;
    private ScratchStateDatabase? _scratch;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _scratch?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private StateDatabase Open(string engine)
    {
        if (engine == "sqlite") return GatewayState.Open(_dir);
        _scratch = ScratchStateDatabase.For(engine);
        var scratch = _scratch;
        return GatewayState.Open(_dir, new StateSettings(engine == "mssql" ? StateEngineIds.MsSql : "postgres", scratch.Connection), text => text.Replace("env:" + scratch.PasswordVariable, scratch.Environment(scratch.PasswordVariable)));
    }

    public static IEnumerable<object[]> Engines => [["sqlite"], ["mssql"], ["postgres"]];

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static SemanticRun Run(string caller, string status = "complete", string? code = null, DateTimeOffset? at = null, long? replayOf = null) =>
        new(caller, "sales_catalog", 3, "query", "SELECT customers.region FROM sales_catalog GROUP BY ALL", new JsonObject { ["kind"] = "query" }, "abc123", status, code, code is null ? null : "refused", "[\"w\"]", null, at ?? Now, 12, 4, replayOf);

    [Fact]
    public void The_module_follows_the_conventions_is_kept_per_group_and_ages_out_after_thirty_days()
    {
        Assert.Empty(StateConventions.Violations(SemanticRunsModule.Module));
        var rule = Assert.Single(SemanticRunsModule.Module.Retention);
        Assert.Equal(("run", "started_at", 30d), (rule.Table, rule.TimeColumn, rule.DefaultDays));
        Assert.Equal((StateScopeKind.Group, "semantic_runs", 1), (SemanticRunsModule.Module.Scope, SemanticRunsModule.Module.Name, SemanticRunsModule.Module.Version));
        Assert.Contains(SemanticRunsModule.Module, GatewayState.Modules);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_record_is_read_back_by_its_owner_and_an_administrator_and_by_no_one_else(string engine)
    {
        var store = new SemanticRunStore(Open(engine));
        var id = store.Add(Run("ada@example.invalid"));
        store.Add(Run("cy@example.invalid", "refused", "unknown_attribute"));

        var mine = store.Get(id, "ada@example.invalid", all: false)!;
        Assert.Equal(("sales_catalog", 3, "query", "abc123", 4L), (mine.Run.Model, mine.Run.ModelVersion, mine.Run.Kind, mine.Run.RequestHash, mine.Run.Rows));
        Assert.Equal("query", (string?)mine.Run.Request!["kind"]);
        Assert.Null(store.Get(id, "cy@example.invalid", all: false));
        Assert.NotNull(store.Get(id, "cy@example.invalid", all: true));

        Assert.Equal(["ada@example.invalid"], store.List("ada@example.invalid", all: false).Select(r => r.Run.Caller));
        Assert.Equal(2, store.List("someone@example.invalid", all: true).Count);
        Assert.Equal(["cy@example.invalid", "ada@example.invalid"], store.List("x", all: true, limit: 10).Select(r => r.Run.Caller));
        Assert.Empty(store.List("ada@example.invalid", all: false, model: "another"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Counts_by_error_code_cover_the_refusals_in_the_window_and_a_replay_names_its_record(string engine)
    {
        var store = new SemanticRunStore(Open(engine));
        store.Add(Run("ada@example.invalid", "refused", "unknown_attribute"));
        store.Add(Run("ada@example.invalid", "refused", "unknown_attribute"));
        store.Add(Run("cy@example.invalid", "partial", "level_not_implemented"));
        store.Add(Run("ada@example.invalid", "refused", "old_code", Now.AddDays(-9)));
        var first = store.Add(Run("ada@example.invalid"));
        var replay = store.Add(Run("ada@example.invalid", replayOf: first));

        Assert.Equal(new Dictionary<string, long> { ["unknown_attribute"] = 2 }, store.CountsByCode(Now.AddDays(-7), "ada@example.invalid", all: false));
        Assert.Equal(new Dictionary<string, long> { ["level_not_implemented"] = 1, ["unknown_attribute"] = 2 }, store.CountsByCode(Now.AddDays(-7), "x", all: true));
        Assert.Equal(first, store.Get(replay, "ada@example.invalid", false)!.Run.ReplayOf);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Records_older_than_thirty_days_are_removed_by_the_retention_job_and_newer_ones_stay(string engine)
    {
        var database = Open(engine);
        var store = new SemanticRunStore(database);
        var old = store.Add(Run("ada@example.invalid", at: DateTimeOffset.UtcNow.AddDays(-31)));
        var recent = store.Add(Run("ada@example.invalid", at: DateTimeOffset.UtcNow.AddDays(-29)));

        Assert.Equal(1, new RetentionJob(database).Run().Removed["semantic_runs.run"]);
        Assert.Null(store.Get(old, "ada@example.invalid", false));
        Assert.NotNull(store.Get(recent, "ada@example.invalid", false));
    }
}
