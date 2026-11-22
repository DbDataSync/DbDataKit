namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Kinds for engine-neutral implementations. Unlike a driver-specific Kind these carry no engine
/// prefix, because the name identifies a *strategy* every driver can offer rather than one engine's
/// mechanism — an operator choosing "Watermark" means the same thing whichever driver serves it.
/// </summary>
public static class GenericDriverKinds
{
    public const string Watermark = "Watermark";

    /// <summary>A trigger-maintained shadow table. The read side is engine-neutral; the trigger DDL
    /// that fills it is not, and is a per-engine provisioning step.</summary>
    public const string TriggerAudit = "TriggerAudit";
    public const string BatchReload = "BatchReload";
    public const string StagingTable = "StagingTable";

    /// <summary>Stages nothing: hands the reader's rows to a writer that loads them straight into the
    /// target. Only legal beside such a writer (<c>IPassThroughWriter</c>).</summary>
    public const string PassThrough = "PassThrough";
    public const string DeleteInsert = "DeleteInsert";

    /// <summary>
    /// The change writer of every engine: the staged rows are collapsed to the last change per key, then a DELETE, an UPDATE and an INSERT run against the target in one
    /// transaction (no MERGE, ON CONFLICT or ON DUPLICATE KEY). Needs a target primary key; not reconciling, so it is the writer behind an incremental reader, and the
    /// default there.
    /// </summary>
    public const string ChangeApply = "ChangeApply";

    /// <summary>Reads only a source table's primary-key values — phase 124's cheap delete-diff sweep.
    /// Always paired with <see cref="KeyReconcileDelete"/>; reconcile-only, never offered in the
    /// ordinary Change Processing reader picker.</summary>
    public const string KeyReconcile = "KeyReconcile";

    /// <summary>Deletes target rows within a segment's scope whose key is absent from the staged key
    /// set — never inserts or updates. Always paired with <see cref="KeyReconcile"/>.</summary>
    public const string KeyReconcileDelete = "KeyReconcileDelete";

    /// <summary>Closes the open SCD2 version of every key absent from the staged key set — never deletes
    /// a row. Always paired with <see cref="KeyReconcile"/>, and only legal when the mapping's own writer
    /// is <see cref="Scd2"/>.</summary>
    public const string KeyReconcileScd2Close = "KeyReconcileScd2Close";

    /// <summary>Appends a complete copy per pass, marked with when it ran. Keeps history by keeping
    /// every copy.</summary>
    public const string Snapshot = "Snapshot";

    /// <summary>Slowly Changing Dimension Type 2: keeps history by versioning each key, closing the
    /// old version when its values change.</summary>
    public const string Scd2 = "Scd2";
}
