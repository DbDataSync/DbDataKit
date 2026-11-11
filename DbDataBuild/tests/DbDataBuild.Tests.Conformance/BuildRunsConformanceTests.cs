using DbDataBuild.State;
using DbDataKit.State;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>UI09 on the state engines: the <c>build_runs</c> module follows the conventions, applies once, and the store records and lists runs newest first on SQL Server and PostgreSQL (SQLite is in the unit suite, <c>BuildSchedulerTests</c>).</summary>
public class BuildRunsConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private static StateDatabase Open(Engine e) =>
        StateOpener.Open(new StateSettings(e.Name == "sqlserver" ? StateEngineIds.MsSql : StateEngineIds.Postgres, e.ConnectionString), "unused.db", [BuildRunsModule.Module], BuildTrackingState.Scope);

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_module_follows_the_conventions_and_runs_are_recorded_and_listed_newest_first(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        Assert.Empty(StateConventions.Violations(BuildRunsModule.Module));
        var store = new PlanRunStore(Open(engine), "dbdatabuild");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        store.Record("sales", "sqlserver/hourly", first, at, at.AddSeconds(30), 0);
        store.Record("sales", "sqlserver/hourly", second, at.AddHours(1), at.AddHours(1).AddMinutes(2), 1);
        store.Record("other", "pg/daily", Guid.NewGuid(), at.AddHours(2), at.AddHours(2).AddSeconds(5), -1);

        var all = store.Recent(10);
        Assert.Equal(["pg/daily", "sqlserver/hourly", "sqlserver/hourly"], all.Select(r => r.Subject));
        Assert.Equal([second, first], store.Recent(10, "sales").Select(r => r.RunId));
        Assert.Equal(at.AddHours(1), store.Recent(10, "sales")[0].Started);
        Assert.Equal([-1], store.Recent(1).Select(r => r.Exit));
        // another service sharing the database sees none of them, and opening again applies nothing twice
        Assert.Empty(new PlanRunStore(Open(engine), "dbdatasync").Recent(10));
        Assert.Equal(3, new PlanRunStore(Open(engine), "dbdatabuild").Recent(10).Count);
    }
}
