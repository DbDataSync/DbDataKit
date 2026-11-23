using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// What a source table's change trigger is, as installed in the customer's database. The trigger is a
/// stored format: the text this tool generated when the table was set up stays there until somebody
/// replaces it, so a change to the text (CW02a: an update that changes the primary key records the old key
/// as a delete) has to be recognised in the database, never assumed.
/// </summary>
public enum TriggerAuditInstall
{
    /// <summary>There is no trigger of this tool's name on the table (not set up, or set up by hand).</summary>
    Absent,

    /// <summary>The trigger is the text that recorded only the NEW key of an update, so after a primary key
    /// update at the source the old key stays in the target.</summary>
    Outdated,

    /// <summary>The trigger is the current text.</summary>
    Current,
}

/// <summary>
/// An engine's way of saying which text its change trigger has. Given to the <see cref="TriggerAuditReader"/>
/// by the drivers that generate the trigger DDL (PostgreSQL, MySQL/MariaDB, Oracle); SQL Server's trigger
/// never needed the change (its <c>inserted</c> and <c>deleted</c> tables already say a key update as a
/// delete of the old key and an insert of the new one) and a descriptor driver's trigger is the operator's.
/// </summary>
public interface ITriggerAuditInstallCheck
{
    Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken);
}

/// <summary>The one wording of the refusal, used by the reader (a pass).</summary>
public static class TriggerAuditInstallMessages
{
    public static string Outdated(string qualifiedTable, string mappingName) =>
        $"The change trigger on '{qualifiedTable}' (table mapping '{mappingName}') is the version that records " +
        "only the new key of an UPDATE, so a primary key update at the source would leave the old key in the " +
        "target and nothing would report it. The pass stops until the trigger is replaced. Replace it: on the " +
        "replication's Provisioning card apply 'Enable source change capture' (the Apply button, or POST " +
        "api/replications/{replication}/provisioning/apply), which replaces the trigger and keeps the shadow " +
        "table and the stored position.";
}
