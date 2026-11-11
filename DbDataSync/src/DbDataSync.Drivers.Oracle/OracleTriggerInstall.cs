using System.Data.Common;
using System.Text;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Which text the installed trigger has (<see cref="OracleTriggerAudit.VersionMarker"/> in <c>ALL_SOURCE</c>,
/// one row per line of the text as it was created). Used by the reader before every pass and by the
/// provisioner to plan the replacement.
/// </summary>
public sealed class OracleTriggerInstall : ITriggerAuditInstallCheck
{
    public static OracleTriggerInstall Instance { get; } = new();

    public Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken) =>
        InspectAsync(connection, source.Schema, source.Table, cancellationToken);

    public static async Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText =
            "SELECT text FROM all_source WHERE owner = :schema AND name = :triggerName AND type = 'TRIGGER' ORDER BY line";
        cmd.AddParameter("schema", schema.ToUpperInvariant());
        cmd.AddParameter("triggerName", OracleTriggerAudit.TriggerName(table).ToUpperInvariant());

        var text = new StringBuilder();
        var any = false;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            any = true;
            text.Append(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return !any ? TriggerAuditInstall.Absent
            : text.ToString().Contains(OracleTriggerAudit.VersionMarker, StringComparison.Ordinal)
                ? TriggerAuditInstall.Current
                : TriggerAuditInstall.Outdated;
    }
}
