using DbDataKit.State;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;

namespace DbDataGateway.Tests;

/// <summary>Plan 22a GP10: the group's record of when each rules table was first served and last refreshed survives a restart and is shared by the instances, on SQLite, SQL Server and PostgreSQL.</summary>
public sealed class RulesTableLedgerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-rules-ledger-").FullName;
    private ScratchStateDatabase? _scratch;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _scratch?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    public static IEnumerable<object[]> Engines => [["sqlite"], ["mssql"], ["postgres"]];

    private StateDatabase Open(string engine)
    {
        if (engine == "sqlite") return GatewayState.Open(_dir);
        _scratch = ScratchStateDatabase.For(engine);
        var scratch = _scratch;
        return GatewayState.Open(_dir, new StateSettings(engine == "mssql" ? StateEngineIds.MsSql : "postgres", scratch.Connection), text => text.Replace("env:" + scratch.PasswordVariable, scratch.Environment(scratch.PasswordVariable)));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_first_sight_and_the_last_change_of_a_table_are_kept_and_shared(string engine)
    {
        var state = Open(engine);
        var clock = new Clock(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
        var first = new RulesTableLedger(state, clock);
        var one = first.Touch("sales/dbo.region_access", clock.Now);
        Assert.Equal((clock.Now, (DateTimeOffset?)null), (one.FirstSeen, one.ChangedAt));

        // a second sight does not move the first
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal(one.FirstSeen, first.Touch("sales/dbo.region_access", clock.Now).FirstSeen);

        var changed = clock.Now;
        first.Changed("sales/dbo.region_access", changed, "build", "data.changed x");
        first.Changed("sales/dbo.region_access", changed - TimeSpan.FromMinutes(1), "build", "an older message");   // never moves the time back
        first.Changed("sales/dbo.never_seen", changed, "store", "data.changed y");   // a change of a table nobody touched yet makes its row

        // another instance of the group, after the read time: the same record
        clock.Now += RulesTableLedger.ReadFor + TimeSpan.FromSeconds(1);
        var second = new RulesTableLedger(state, clock);
        var seen = second.Touch("sales/dbo.region_access", clock.Now);
        Assert.Equal((one.FirstSeen, (DateTimeOffset?)changed), (seen.FirstSeen, seen.ChangedAt));
        Assert.Equal(changed, second.Touch("sales/dbo.never_seen", clock.Now).ChangedAt);
        Assert.Equal(2, second.All().Count);

        // a restart: a new database object over the same state
        var again = new RulesTableLedger(state, clock).Touch("sales/dbo.region_access", clock.Now);
        Assert.Equal(changed, again.ChangedAt);
    }
}
