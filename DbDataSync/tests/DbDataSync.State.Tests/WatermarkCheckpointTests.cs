using DbDataSync.Core.Config;

namespace DbDataSync.State.Tests;

/// <summary>
/// Plan 28c, slice E15: Sync's position is a declared checkpoint of the progress store, on every engine: the seven parts per mapping and source table, a position that moves only from the version it was
/// read at, a rewind that is a new version, a package version that renames a part and is put back from history, and history that ages out while the position stays.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WatermarkCheckpointTests : IClassFixture<LibraryInstallFixture>, IDisposable
{
    private const string Task = "crm-sync";
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-watermark-").FullName;
    private readonly List<StateEngineFixture> _fixtures = [];
    private readonly DbDataSync.Libraries.LibraryRegistry _libraries;
    private readonly Clock _clock = new(DateTimeOffset.Parse("2026-11-07T10:00:00Z"));

    public WatermarkCheckpointTests(LibraryInstallFixture libraries) => _libraries = libraries.Registry;

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
            fixture.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private StateDatabase Open(string engine)
    {
        if (engine == StateEngineIds.Sqlite)
            return new StateDatabase(Path.Combine(_tempDir, $"{Guid.NewGuid():N}.db"), _clock);

        // the scratch database is made by the fixture, which opens it on the real clock: this test opens the same database again on its own
        var fixture = new StateEngineFixture(engine, _libraries);
        _fixtures.Add(fixture);
        return new StateDatabase(engine, fixture.ConnectionString, _clock);
    }

    public static TheoryData<string> Engines => [StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres];

    private static ProgressStore StoreOf(StateDatabase database, string task = Task) => new(database, WatermarkCheckpoint.KeyFor(task));

    [Fact]
    public void TheDeclarationHasTheSevenPartsPerMappingAndSourceTable()
    {
        var declaration = WatermarkCheckpoint.Declaration;

        Assert.Equal("watermark", declaration.Name);
        Assert.Equal(["mapping", "source_table"], declaration.Per);
        Assert.Equal(
            ["position", "position_time", "intent", "hold", "pending_position", "pending_position_time", "pending_batch"],
            declaration.Parts.Keys);
        Assert.Equal(ProgressType.Time, declaration.Parts["position_time"].Type);
        Assert.Equal("Changes", declaration.Parts["intent"].Default);
        Assert.Equal("None", declaration.Parts["hold"].Default);
    }

    [Fact]
    public void AKeyThatContainsTheSeparatorStaysOnePair()
    {
        var one = WatermarkCheckpoint.ItemKey("a|b", "c");
        var other = WatermarkCheckpoint.ItemKey("a", "b|c");

        Assert.NotEqual(one, other);
        Assert.Equal(("a|b", "c"), WatermarkCheckpoint.Split(one));
        Assert.Equal(("a", "b|c"), WatermarkCheckpoint.Split(other));
        Assert.Equal((@"back\slash", "dbo.Orders"), WatermarkCheckpoint.Split(WatermarkCheckpoint.ItemKey(@"back\slash", "dbo.Orders")));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryPartIsKeptAndEveryChangeIsInTheHistory(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        var time = DateTimeOffset.Parse("2026-11-07T09:30:00Z");

        store.SetReadIntentAndHold(Task, "orders", "dbo.Orders", ReadIntent.InitialLoad, ReadHold.None);
        store.SetPendingLoad(Task, "orders", "dbo.Orders", "0000002A:00000140:0003", time, "batch-1");
        var loading = store.GetReadState(Task, "orders", "dbo.Orders")!;
        Assert.Equal((ReadIntent.InitialLoad, ReadHold.Loading, (string?)null), (loading.Intent, loading.Hold, loading.Watermark));

        Assert.True(store.PromotePendingLoad("batch-1"));
        var promoted = store.GetReadState(Task, "orders", "dbo.Orders")!;
        Assert.Equal((ReadIntent.Changes, ReadHold.None, "0000002A:00000140:0003", (DateTimeOffset?)time), (promoted.Intent, promoted.Hold, promoted.Watermark, promoted.WatermarkTimeUtc));

        var checkpoint = StoreOf(database).GetCheckpoint(WatermarkCheckpoint.Name, WatermarkCheckpoint.ItemKey("orders", "dbo.Orders"))!;
        Assert.Equal(["hold", "intent", "position", "position_time"], checkpoint.Parts.Keys.Order(StringComparer.Ordinal));   // the pending parts are gone once promoted
        Assert.NotEmpty(store.History(Task, "orders", "dbo.Orders").Where(c => c.Part == "pending_batch"));
        Assert.Equal(3L, checkpoint.Version);   // intent and hold, the pending load, the promotion
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void FiveHundredMappingsKeepTheirOwnPositionAsTheEnginesWriteThem(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);

        string Position(int i) => (i % 3) switch
        {
            0 => $"{i * 4096:X8}:{i:X8}:0001",                                       // a log sequence number as hex
            1 => (1_000_000L + i).ToString(System.Globalization.CultureInfo.InvariantCulture),   // a change-tracking version in decimal
            _ => DateTimeOffset.Parse("2026-11-01T00:00:00Z").AddMinutes(i).ToString("O"),   // a time as text
        };

        for (var i = 0; i < 500; i++)
            store.SetWatermark(Task, $"mapping-{i}", $"dbo.Table{i}", Position(i), i % 3 == 2 ? DateTimeOffset.Parse("2026-11-01T00:00:00Z").AddMinutes(i) : null);

        Assert.Equal(500, StoreOf(database).ItemKeys(WatermarkCheckpoint.Name).Count);
        foreach (var i in new[] { 0, 1, 2, 3, 250, 251, 252, 499 })
        {
            Assert.Equal(Position(i), store.GetWatermark(Task, $"mapping-{i}", $"dbo.Table{i}"));
            Assert.Equal(i % 3 == 2, store.GetAppliedPosition(Task, $"mapping-{i}", $"dbo.Table{i}")!.WatermarkTimeUtc is not null);
        }

        Assert.Null(store.GetWatermark(Task, "mapping-1", "dbo.Table2"));   // the mapping and the table are both in the key
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void APositionMovesOnlyFromTheVersionItWasReadAt(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        store.SetWatermark(Task, "orders", "dbo.Orders", "100");
        var read = store.Version(Task, "orders", "dbo.Orders");

        // another pass got there first
        store.SetWatermark(Task, "orders", "dbo.Orders", "150");

        Assert.False(store.TryAdvancePosition(Task, "orders", "dbo.Orders", read, "200", null, "worker-1"));
        Assert.Equal("150", store.GetWatermark(Task, "orders", "dbo.Orders"));

        Assert.True(store.TryAdvancePosition(Task, "orders", "dbo.Orders", store.Version(Task, "orders", "dbo.Orders"), "200", null, "worker-1"));
        Assert.Equal("200", store.GetWatermark(Task, "orders", "dbo.Orders"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ThePositionMovesWithTheOutcomeOrNotAtAll(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        store.SetWatermark(Task, "orders", "dbo.Orders", "100");
        var version = store.Version(Task, "orders", "dbo.Orders");

        // the callback runs in the transaction of the advance: when it fails, the position stays where it was
        Assert.Throws<InvalidOperationException>(() => store.TryAdvancePosition(Task, "orders", "dbo.Orders", version, "200", null, "worker-1", alsoWrite: (_, _) => throw new InvalidOperationException("the outcome could not be written")));
        Assert.Equal("100", store.GetWatermark(Task, "orders", "dbo.Orders"));
        Assert.Equal(version, store.Version(Task, "orders", "dbo.Orders"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void WritersThatOverlapNeverLoseAPositionOrFail(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 4 }, writer =>
        {
            for (var i = 0; i < 10; i++)
                store.SetWatermark(Task, "orders", "dbo.Orders", $"{writer}-{i}");
        });

        // every write was an advance, in some order: 80 of them, and the last one is what is there
        Assert.Equal(80L, store.Version(Task, "orders", "dbo.Orders"));
        Assert.Matches(@"^\d-\d$", store.GetWatermark(Task, "orders", "dbo.Orders"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ARewindMovesAMappingBackAndTheNextPassReturnsToTheSameFinalState(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        foreach (var position in new[] { "100", "200", "300" })
            store.SetWatermark(Task, "orders", "dbo.Orders", position);
        var before = store.GetReadState(Task, "orders", "dbo.Orders")!;

        // "rewind a mapping to Monday": before the change that took the position to 300
        var toThreeHundred = store.History(Task, "orders", "dbo.Orders").First(c => c is { Part: "position", New: "300" });
        Assert.True(store.Rewind(Task, "orders", "dbo.Orders", toThreeHundred.Id, actor: "ada"));

        Assert.Equal("200", store.GetWatermark(Task, "orders", "dbo.Orders"));
        Assert.Equal("ada", store.History(Task, "orders", "dbo.Orders").First().Actor);   // the history names who rewound it

        // the next pass re-reads and lands on the same final state
        store.SetWatermark(Task, "orders", "dbo.Orders", "300");
        var after = store.GetReadState(Task, "orders", "dbo.Orders")!;
        Assert.Equal(before, after);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ClearingAPositionKeepsItsHistoryAndTheNextPassStartsAsANewMappingWould(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        store.SetWatermark(Task, "orders", "dbo.Orders", "100");

        Assert.True(store.ClearWatermark(Task, "orders", "dbo.Orders", actor: "ada"));
        Assert.False(store.ClearWatermark(Task, "orders", "dbo.Orders"));

        Assert.Null(store.GetReadState(Task, "orders", "dbo.Orders"));
        Assert.Contains(store.History(Task, "orders", "dbo.Orders"), c => c is { Part: "position", Actor: "ada", New: null });
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AVersionThatRenamesAPartMigratesInOneTransactionAndRollsBackFromHistory(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        var progress = StoreOf(database);
        store.SetPendingLoad(Task, "orders", "dbo.Orders", "0000002A:00000140:0003", null, "batch-7");
        store.SetWatermark(Task, "customers", "dbo.Customers", "42");

        // version 2 of the replication set calls pending_batch pending_load and adds a part with a default
        var renamed = WatermarkCheckpoint.Declaration with
        {
            Parts = WatermarkCheckpoint.Declaration.Parts
                .Where(p => p.Key != "pending_batch")
                .Append(KeyValuePair.Create("pending_load", new DeclaredPart(ProgressType.Text, RenamedFrom: "pending_batch")))
                .Append(KeyValuePair.Create("note", new DeclaredPart(ProgressType.Text, Default: "migrated")))
                .ToDictionary(p => p.Key, p => p.Value),
        };
        var v1 = WatermarkCheckpoint.ProgressDeclaration;
        var v2 = new ProgressDeclaration([renamed], []);

        var plan = ProgressMigration.Plan(v1, v2);
        Assert.Contains(plan.Steps, s => s is { Kind: MigrationKind.RenameValue, Target: "watermark.pending_load" });
        Assert.Empty(plan.Losses);

        // activation: everything in one transaction, which a failure rolls back whole
        using (var connection = database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            ProgressMigration.Apply(progress.Within(connection, transaction), v1, v2, actor: "activate:2.0.0");
            transaction.Rollback();
        }

        Assert.Equal("batch-7", ReadPart(progress, "orders", "dbo.Orders", "pending_batch"));

        using (var connection = database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            ProgressMigration.Apply(progress.Within(connection, transaction), v1, v2, actor: "activate:2.0.0");
            transaction.Commit();
        }

        Assert.Null(ReadPart(progress, "orders", "dbo.Orders", "pending_batch"));
        Assert.Equal("batch-7", ReadPart(progress, "orders", "dbo.Orders", "pending_load"));
        Assert.Equal("migrated", ReadPart(progress, "customers", "dbo.Customers", "note"));
        Assert.Equal("42", store.GetWatermark(Task, "customers", "dbo.Customers"));   // what was not renamed is untouched

        // the version is rolled back: the old values come back from the history
        Assert.True(ProgressMigration.CanRestore(progress, "activate:"));
        Assert.Equal(2, ProgressMigration.Restore(progress, "activate:", actor: "rollback:2.0.0"));
        Assert.Equal("batch-7", ReadPart(progress, "orders", "dbo.Orders", "pending_batch"));
        Assert.Null(ReadPart(progress, "orders", "dbo.Orders", "pending_load"));
        Assert.Null(ReadPart(progress, "customers", "dbo.Customers", "note"));
    }

    private static string? ReadPart(ProgressStore progress, string mapping, string table, string part) =>
        progress.GetCheckpoint(WatermarkCheckpoint.Name, WatermarkCheckpoint.ItemKey(mapping, table))!.Parts.TryGetValue(part, out var value) ? value.AsText : null;

    [Theory]
    [MemberData(nameof(Engines))]
    public void HistoryOlderThanThirtyDaysGoesAndThePositionStays(string engine)
    {
        var database = Open(engine);
        var store = new ChangeWatermarkStore(database);
        store.SetWatermark(Task, "orders", "dbo.Orders", "100");
        _clock.Advance(TimeSpan.FromDays(20));
        store.SetWatermark(Task, "orders", "dbo.Orders", "200");
        Assert.Equal(2, store.History(Task, "orders", "dbo.Orders").Count(c => c.Part == "position"));

        _clock.Advance(TimeSpan.FromDays(11));   // 31 days since the first change, 11 since the second
        var removed = new RetentionJob(database).Run();

        Assert.True(removed.Removed.GetValueOrDefault("runner_progress.checkpoint_history") > 0);
        var kept = store.History(Task, "orders", "dbo.Orders").Where(c => c.Part == "position").ToList();
        Assert.Equal(["200"], kept.Select(c => c.New));
        Assert.Equal("200", store.GetWatermark(Task, "orders", "dbo.Orders"));   // the position itself is current state: never aged out
    }

}
