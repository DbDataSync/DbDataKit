namespace DbDataSync.Core.Config;

/// <summary>
/// Whether the segments of one reload of a mapping take turns or may run together (plan 13, M03). Chosen when the work is queued; a connection, a replication or a
/// table mapping sets the default through the usual inheritance (<see cref="SegmentOrderResolution"/>).
/// </summary>
public enum SegmentOrder
{
    /// <summary>One segment at a time, as Sync has always run them. Easy on the target: one writer at a time per mapping.</summary>
    OneAtATime,

    /// <summary>Segments run together, as far as the lane's capacity allows. Faster for a large table, and the target must be able to take several writers.</summary>
    Together,
}

/// <summary>
/// What a reload's segment order is: the request's own choice, else the table mapping's, the replication's, the target connection's, and last the application default.
/// Each level falls back independently, most specific first, like <see cref="ReadIntentResolution"/>.
/// </summary>
public static class SegmentOrderResolution
{
    /// <summary>What nothing anywhere has said: today's behaviour.</summary>
    public const SegmentOrder Default = SegmentOrder.OneAtATime;

    public static SegmentOrder Resolve(SegmentOrder? requested, ConnectionConfig? targetConnection, ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
        requested ?? mapping?.SegmentOrder ?? task?.SegmentOrder ?? targetConnection?.SegmentOrder ?? Default;

    /// <summary>Where the resolved value came from, for a UI that wants to show INHERITED.</summary>
    public static BindingLevel LevelOf(ConnectionConfig? targetConnection, ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
        mapping?.SegmentOrder is not null ? BindingLevel.Mapping
        : task?.SegmentOrder is not null ? BindingLevel.Replication
        : targetConnection?.SegmentOrder is not null ? BindingLevel.Connection
        : BindingLevel.Unbound;
}
