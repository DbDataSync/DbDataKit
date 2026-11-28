using DbDataKit.Sync.Drivers.Generic;

namespace DbDataSync.Drivers.MsSql;

public static class MsSqlDriverKinds
{
    public const string ChangeTracking = BuiltInDriverKinds.MsSqlChangeTracking;

    /// <summary>Change Data Capture. Not an upgrade to <see cref="ChangeTracking"/> — a different
    /// mechanism with different semantics; see <c>MsSqlCdcReader</c>.</summary>
    public const string Cdc = BuiltInDriverKinds.MsSqlCdc;

    /// <summary>Re-exported from <see cref="GenericDriverKinds"/>: the watermark reader is
    /// engine-neutral, so its Kind carries no prefix and is not this driver's to define.</summary>
    public const string Watermark = GenericDriverKinds.Watermark;
    public const string BatchReload = BuiltInDriverKinds.MsSqlBatchReload;
    public const string StagingTable = BuiltInDriverKinds.MsSqlStagingTable;
    public const string Merge = BuiltInDriverKinds.MsSqlMerge;
    public const string MergeReconcile = BuiltInDriverKinds.MsSqlMergeReconcile;
    public const string DeleteInsert = BuiltInDriverKinds.MsSqlDeleteInsert;

    /// <summary>SqlBulkCopy straight into the target, no staging; paired with the <c>PassThrough</c> cache.</summary>
    public const string BulkInsert = BuiltInDriverKinds.MsSqlBulkInsert;

    /// <summary>Re-exported from <see cref="GenericDriverKinds"/>, same reasoning as
    /// <see cref="Watermark"/> — phase 124's delete-diff sweep is engine-neutral.</summary>
    public const string KeyReconcile = GenericDriverKinds.KeyReconcile;
    public const string KeyReconcileDelete = GenericDriverKinds.KeyReconcileDelete;
}
