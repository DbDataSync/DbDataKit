using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>The Kinds of the built-in drivers that are an engine's own, beside the generic ones of <see cref="GenericDriverKinds"/>. The drivers take
/// their Kinds from here, so the capability table and the drivers spell each one once.</summary>
public static class BuiltInDriverKinds
{
    public const string MsSqlChangeTracking = "MsSqlChangeTracking";
    public const string MsSqlCdc = "MsSqlCdc";

    /// <summary>The generic batch reload under SQL Server's older name, so a mapping saved with it still resolves.</summary>
    public const string MsSqlBatchReload = "MsSqlBatchReload";

    public const string MsSqlStagingTable = "MsSqlStagingTable";
    public const string MsSqlMerge = "MsSqlMerge";
    public const string MsSqlMergeReconcile = "MsSqlMergeReconcile";
    public const string MsSqlDeleteInsert = "MsSqlDeleteInsert";
    public const string MsSqlBulkInsert = "MsSqlBulkInsert";
    public const string PgLogicalSlot = "PgLogicalSlot";
    public const string PgCopyStaging = "PgCopyStaging";
    public const string OracleFlashback = "OracleFlashback";

    /// <summary>The reader a query-builder script supplies, which the engine's composition adds to every driver that names a dialect and a catalog.</summary>
    public const string ScriptedQuery = "ScriptedQuery";
}

/// <summary>The settings of the built-in drivers' own readers and writers, read by the readers and writers themselves and listed by the
/// capability table, so a setting is described once.</summary>
public static class BuiltInDriverOptions
{
    /// <summary>SQL Server change tracking: read the change window inside a snapshot transaction.</summary>
    public const string SnapshotIsolation = "snapshotIsolation";

    /// <summary>SQL Server bulk insert: how the target is cleared first (<see cref="ClearDelete"/> or <see cref="ClearTruncate"/>).</summary>
    public const string Clear = "clear";

    /// <summary>SQL Server bulk insert: clear and load in one transaction.</summary>
    public const string Transaction = "transaction";

    public const string ClearDelete = "Delete";
    public const string ClearTruncate = "Truncate";

    /// <summary>PostgreSQL logical slot: the slot a mapping reads.</summary>
    public const string SlotName = "slotName";

    /// <summary>PostgreSQL logical slot: move the slot forward after a successful pass.</summary>
    public const string AdvanceSlot = "advanceSlot";

    /// <summary>The scripted reader: the script that builds its query.</summary>
    public const string Script = "script";

    public static IReadOnlyList<ParameterDescriptor> MsSqlChangeTracking { get; } =
    [
        new()
        {
            Name = SnapshotIsolation,
            Label = "Snapshot isolation",
            Description =
                "Reads the change window inside a snapshot transaction, so a row changing mid-pass " +
                "cannot be read in two states. Requires the source database to allow snapshot isolation.",
            Type = ParameterType.Bool,
            Default = "false",
        },
        BoundedRead.CappedDescriptor,
    ];

    public static IReadOnlyList<ParameterDescriptor> MsSqlCdc { get; } = [BoundedRead.CappedDescriptor];

    public static IReadOnlyList<ParameterDescriptor> MsSqlMerge { get; } = [ApplyBatch.Descriptor];

    public static IReadOnlyList<ParameterDescriptor> MsSqlBulkInsert { get; } =
    [
        new ParameterDescriptor
        {
            Name = Clear,
            Label = "Clear the target first",
            Description =
                "Delete removes the rows in this pass's scope (the whole table, or just the segment when the " +
                "load is segmented). Truncate empties the whole table and is much cheaper, but is refused for a " +
                "segmented load and for a table a foreign key references.",
            Type = ParameterType.Dropdown,
            DropdownOptions = [ClearDelete, ClearTruncate],
            Default = ClearDelete,
        },
        new ParameterDescriptor
        {
            Name = Transaction,
            Label = "Clear and load in one transaction",
            Description =
                "On, a failed load leaves the previous rows in place and readers never see the table empty, " +
                "but locks are held for the whole load. Off, a failure leaves the table cleared or partly " +
                "loaded until the next run.",
            Type = ParameterType.Bool,
            Default = "true",
        },
    ];

    public static IReadOnlyList<ParameterDescriptor> PgLogicalSlot { get; } =
    [
        new()
        {
            Name = SlotName,
            Label = "Replication slot",
            Description =
                "The logical replication slot this mapping reads. Left empty, one named after the " +
                "source table. The slot has to exist — it is created by Apply on the Provisioning tab — " +
                "and a slot that no replication reads pins the source's WAL until it is dropped.",
            Required = false,
        },
        new()
        {
            Name = AdvanceSlot,
            Label = "Advance the slot after a successful pass",
            Description =
                "Lets the source recycle the WAL this slot is holding, once the pass that read it has " +
                "written and recorded its position. Without it the slot's WAL grows for as long as " +
                "the replication runs. Only turn this on when the slot belongs to this mapping alone: " +
                "advancing a shared slot discards changes the other mappings have not read.",
            Type = ParameterType.Bool,
            Default = "false",
        },
    ];

    /// <summary>Named rather than a dropdown of script names: which scripts exist is the project's, and a screen fills the choices in from the
    /// draft's scripts.</summary>
    public static IReadOnlyList<ParameterDescriptor> ScriptedQuery { get; } =
    [
        new()
        {
            Name = Script,
            Label = "Query builder script",
            Description = "The script that builds this reader's statements. Bound by name, not through the hierarchy.",
            Type = ParameterType.Dropdown,
            Required = true,
        },
    ];
}

/// <summary>
/// What each of Sync's built-in drivers offers, as the engine registers it (<see cref="DriverRegistry.Describe"/>): its readers, staging
/// providers and writers with their settings, the key each needs and the changes each writer makes, whether it tests a connection, what it provisions, whether a target keeps the state table. A screen and an
/// offline check ask this table, so they answer without the engine's database clients; Sync's own tests hold it equal to what each driver
/// registers. A driver a project carries (<c>driver.yaml</c>, a compiled driver) is not in it.
/// </summary>
public static class BuiltInDriverCapabilities
{
    private static readonly IReadOnlyList<ReadIntent> AllChanges = [ReadIntent.Changes, ReadIntent.ChangesFromEarliest, ReadIntent.ChangesFromLatest];

    private static readonly ReaderCapability Watermark =
        new(GenericDriverKinds.Watermark, false, false, WatermarkReader.Options, [ReadIntent.Changes, ReadIntent.ChangesFromLatest], DetectsKeyChanges: false);

    private static readonly ReaderCapability TriggerAudit = new(GenericDriverKinds.TriggerAudit, false, true, TriggerAuditReader.Options, AllChanges, NeedsKey: KeyNeed.SourceKey);

    private static ReaderCapability BatchReload(string kind = GenericDriverKinds.BatchReload) => new(kind, true, false, [], []);

    private static readonly ReaderCapability KeyReconcile = new(GenericDriverKinds.KeyReconcile, true, false, [], [], NeedsKey: KeyNeed.SourceKey);

    private static readonly ReaderCapability ScriptedQuery = new(BuiltInDriverKinds.ScriptedQuery, false, false, BuiltInDriverOptions.ScriptedQuery, [], DetectsKeyChanges: false);

    private static readonly StagingCapability StagingTable = new(GenericDriverKinds.StagingTable, []);

    /// <summary>The generic writers every SQL engine's driver registers, in its order.</summary>
    private static readonly IReadOnlyList<WriterCapability> GenericWriters =
    [
        new(GenericDriverKinds.ChangeApply, false, [ApplyBatch.Descriptor], KeyNeed.TargetKey, WritePrivileges.All),
        new(GenericDriverKinds.DeleteInsert, true, [ApplyBatch.Descriptor], KeyNeed.None, WritePrivileges.Insert | WritePrivileges.Delete),
        new(GenericDriverKinds.KeyReconcileDelete, true, [], KeyNeed.TargetKey, WritePrivileges.Delete),
        new(GenericDriverKinds.KeyReconcileScd2Close, true, [], KeyNeed.NaturalKey, WritePrivileges.Update),
        new(GenericDriverKinds.Snapshot, false, [], KeyNeed.None, WritePrivileges.Insert),
        new(GenericDriverKinds.Scd2, false, Scd2Writer.Options, KeyNeed.NaturalKey, WritePrivileges.Insert | WritePrivileges.Update),
    ];

    private static readonly IReadOnlyList<string> Provisions =
        [ProvisioningActions.EnableSourceChangeCapture, ProvisioningActions.CreateTargetTable, ProvisioningActions.AlterTargetTable];

    /// <summary>Every built-in driver, by its driver id (a connection file's <c>engine</c>, and <c>dialect</c> for Arrow Flight SQL, names one).</summary>
    public static IReadOnlyList<DriverCapabilities> All { get; } =
    [
        new(DriverIds.MsSql,
            [
                new(BuiltInDriverKinds.MsSqlChangeTracking, false, true, BuiltInDriverOptions.MsSqlChangeTracking, AllChanges, NeedsKey: KeyNeed.SourceKey),
                new(BuiltInDriverKinds.MsSqlCdc, false, true, BuiltInDriverOptions.MsSqlCdc, AllChanges),
                TriggerAudit,
                Watermark,
                BatchReload(),
                BatchReload(BuiltInDriverKinds.MsSqlBatchReload),
                KeyReconcile,
                ScriptedQuery,
            ],
            [new(BuiltInDriverKinds.MsSqlStagingTable, []), StagingTable, new(GenericDriverKinds.PassThrough, [])],
            [
                new(BuiltInDriverKinds.MsSqlMerge, false, BuiltInDriverOptions.MsSqlMerge, KeyNeed.TargetKey, WritePrivileges.All),
                new(BuiltInDriverKinds.MsSqlMergeReconcile, true, [], KeyNeed.TargetKey, WritePrivileges.All),
                new(BuiltInDriverKinds.MsSqlDeleteInsert, true, [], KeyNeed.None, WritePrivileges.Insert | WritePrivileges.Delete),
                new(BuiltInDriverKinds.MsSqlBulkInsert, true, BuiltInDriverOptions.MsSqlBulkInsert, KeyNeed.None, WritePrivileges.Insert | WritePrivileges.Delete),
                .. GenericWriters,
            ],
            SupportsConnectionTest: true, Provisions, SupportsLibraryValidation: true, DefaultTestQuery: "SELECT @@VERSION;", SupportsTieSafeRowLimit: true, KeepsStateTable: true),
        new(DriverIds.Postgres,
            [
                Watermark,
                TriggerAudit,
                BatchReload(),
                KeyReconcile,
                new(BuiltInDriverKinds.PgLogicalSlot, false, true, BuiltInDriverOptions.PgLogicalSlot, AllChanges, NeedsKey: KeyNeed.SourceKey),
                ScriptedQuery,
            ],
            [new(BuiltInDriverKinds.PgCopyStaging, []), StagingTable],
            GenericWriters,
            SupportsConnectionTest: true, Provisions, SupportsLibraryValidation: true, DefaultTestQuery: "SELECT version();", SupportsTieSafeRowLimit: true, KeepsStateTable: true),
        new(DriverIds.MySql,
            [Watermark, TriggerAudit, BatchReload(), KeyReconcile, ScriptedQuery],
            [StagingTable],
            GenericWriters,
            SupportsConnectionTest: true, Provisions, SupportsLibraryValidation: true, DefaultTestQuery: "SELECT VERSION();", SupportsTieSafeRowLimit: false),
        new(DriverIds.Oracle,
            [
                Watermark,
                TriggerAudit,
                BatchReload(),
                KeyReconcile,
                new(BuiltInDriverKinds.OracleFlashback, false, true, [], [ReadIntent.Changes, ReadIntent.ChangesFromLatest]),
                ScriptedQuery,
            ],
            [StagingTable],
            GenericWriters,
            SupportsConnectionTest: true, Provisions, SupportsLibraryValidation: true,
            DefaultTestQuery: "SELECT banner FROM v$version WHERE banner LIKE 'Oracle%' FETCH FIRST 1 ROWS ONLY", SupportsTieSafeRowLimit: true),
        new(DriverIds.DuckDb, [BatchReload()], [], [], SupportsConnectionTest: true, [], DefaultTestQuery: "SELECT version();", SupportsTieSafeRowLimit: true),
        FlightSql(DriverIds.ArrowFlightSql, "SELECT 1", tieSafe: true),
        FlightSql(DriverIds.ArrowFlightSqlMsSql, "SELECT 1", tieSafe: true),
        FlightSql(DriverIds.ArrowFlightSqlPostgres, "SELECT 1", tieSafe: true),
        FlightSql(DriverIds.ArrowFlightSqlMySql, "SELECT 1", tieSafe: false),
        FlightSql(DriverIds.ArrowFlightSqlOracle, "SELECT 1 FROM DUAL", tieSafe: true),
        FlightSql(DriverIds.ArrowFlightSqlDuckDb, "SELECT 1", tieSafe: true),
    ];

    /// <summary>An Arrow Flight SQL driver reads only, a whole table at a time, in the SQL of the engine behind the server.</summary>
    private static DriverCapabilities FlightSql(string driverType, string testQuery, bool tieSafe) =>
        new(driverType, [BatchReload()], [], [], SupportsConnectionTest: true, [], DefaultTestQuery: testQuery, SupportsTieSafeRowLimit: tieSafe);

    /// <summary>The built-in driver of this id, or null for a driver a project carries or one that does not exist.</summary>
    public static DriverCapabilities? For(string driverType) => All.FirstOrDefault(d => string.Equals(d.DriverType, driverType, StringComparison.Ordinal));
}
