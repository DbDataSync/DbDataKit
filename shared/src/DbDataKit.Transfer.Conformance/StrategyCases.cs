using DbDataKit.Dialect;

namespace DbDataKit.Transfer.Conformance;

/// <summary>The shared scenarios. Each is derived from the strategy semantics in docs/plans/07a-strategy-comparison.md.</summary>
public static class StrategyCases
{
    private static readonly TransferSchema Items = new(
    [
        new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), Nullable: true, IsKey: true),
        new TransferColumn("name", CanonicalType.String(50)),
        new TransferColumn("amount", CanonicalType.Decimal(10, 2)),
    ]);

    private static readonly TransferSchema ByDay = new(
    [
        new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), IsKey: true),
        new TransferColumn("day", CanonicalType.Of(CanonicalKind.Date)),
        new TransferColumn("note", CanonicalType.String(50)),
    ]);

    private static readonly TransferSchema Events = new(
    [
        new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), IsKey: true),
        new TransferColumn("at", CanonicalType.Timestamp(6)),
        new TransferColumn("note", CanonicalType.String(50)),
    ]);

    private static object?[] I(int? id, string name, decimal amount) => [id, name, amount];
    private static object?[] D(int id, int day, string note) => [id, new DateOnly(2024, 1, day), note];
    private static object?[] E(int id, int hour, string note) => [id, new DateTime(2024, 1, 1, hour, 0, 0, DateTimeKind.Unspecified), note];

    public static IReadOnlyList<StrategyCase> All { get; } =
    [
        new("full_replace replaces every row", StrategyNames.FullReplace, Items,
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m)],
            [I(2, "b2", 20m), I(4, "d", 4m)],
            [I(2, "b2", 20m), I(4, "d", 4m)]),

        new("full_replace with an empty stage empties the target", StrategyNames.FullReplace, Items,
            [I(1, "a", 1m)], [], []),

        new("delete_insert_by_key replaces matching keys and keeps the rest", StrategyNames.DeleteInsertByKey, Items,
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m)],
            [I(2, "b2", 20m), I(4, "d", 4m)],
            [I(1, "a", 1m), I(2, "b2", 20m), I(3, "c", 3m), I(4, "d", 4m)]),

        new("delete_insert_by_key never matches a NULL key", StrategyNames.DeleteInsertByKey, Items,
            [I(null, "orphan", 9m), I(1, "a", 1m)],
            [I(1, "a2", 10m)],
            [I(null, "orphan", 9m), I(1, "a2", 10m)]),

        new("merge_by_key updates matches and inserts the rest", StrategyNames.MergeByKey, Items,
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m)],
            [I(2, "b2", 20m), I(4, "d", 4m)],
            [I(1, "a", 1m), I(2, "b2", 20m), I(3, "c", 3m), I(4, "d", 4m)]) { UniqueKeyOnTarget = true },

        new("delete_insert_by_range replaces only the window", StrategyNames.DeleteInsertByRange, ByDay,
            [D(1, 1, "x"), D(2, 2, "x"), D(3, 3, "x"), D(4, 4, "x"), D(5, 5, "x")],
            [D(3, 3, "new"), D(4, 4, "new"), D(6, 6, "outside the window")],
            [D(1, 1, "x"), D(2, 2, "x"), D(3, 3, "new"), D(4, 4, "new"), D(5, 5, "x")])
        {
            Configure = r => r with { RangeColumn = "day", RangeStart = new DateOnly(2024, 1, 3), RangeEnd = new DateOnly(2024, 1, 5) },
        },

        new("watermark_append appends only newer rows", StrategyNames.WatermarkAppend, Events,
            [E(1, 1, "old"), E(2, 2, "old"), E(3, 3, "old")],
            [E(2, 2, "dup"), E(3, 3, "dup"), E(4, 4, "new"), E(5, 5, "new")],
            [E(1, 1, "old"), E(2, 2, "old"), E(3, 3, "old"), E(4, 4, "new"), E(5, 5, "new")])
        {
            Configure = r => r with { WatermarkColumn = "at" },
            Resolve = async (r, d, scalar) =>
                r with { Watermark = await scalar(WatermarkAppendStrategy.ResolverStatement(r, d)) },
        },

        new("watermark_append with a lookback replaces the window", StrategyNames.WatermarkAppend, Events,
            [E(1, 1, "old"), E(2, 2, "old"), E(3, 3, "old")],
            [E(2, 2, "fresh"), E(3, 3, "fresh"), E(4, 4, "new")],
            [E(1, 1, "old"), E(2, 2, "fresh"), E(3, 3, "fresh"), E(4, 4, "new")])
        {
            Configure = r => r with { WatermarkColumn = "at", WatermarkInclusive = true },
            // lookback of one hour behind the newest row: the host subtracts it, the strategy only replaces [watermark, +inf)
            Resolve = async (r, d, scalar) =>
            {
                var max = await scalar(WatermarkAppendStrategy.ResolverStatement(r, d));
                return r with { Watermark = ((DateTime)max!).AddHours(-1) };
            },
        },

        new("key_reconcile_delete removes keys absent from the stage", StrategyNames.KeyReconcileDelete, Items,
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m), I(4, "d", 4m)],
            [I(1, "a", 1m), I(2, "b", 2m)],
            [I(1, "a", 1m), I(2, "b", 2m)]),

        new("key_reconcile_delete guard refuses a runaway delete and rolls back", StrategyNames.KeyReconcileDelete, Items,
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m), I(4, "d", 4m)],
            [I(1, "a", 1m)],
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m), I(4, "d", 4m)])
        { ExpectedFailure = typeof(DeleteGuardExceededException) },

        new("key_reconcile_delete with the guard disabled deletes anyway", StrategyNames.KeyReconcileDelete, Items,
            [I(1, "a", 1m), I(2, "b", 2m), I(3, "c", 3m), I(4, "d", 4m)],
            [I(1, "a", 1m)],
            [I(1, "a", 1m)])
        { Configure = r => r with { Options = new ApplyOptions { Guard = DeleteGuard.None } } },
    ];
}

