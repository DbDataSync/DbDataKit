using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// Which text the installed update trigger has (<see cref="MySqlTriggerAudit.IsCurrentUpdateTrigger"/> on the
/// <c>action_statement</c> the server reports). Used by the reader before every pass and by the provisioner
/// to plan the replacement. Only the <c>au</c> trigger changed; a missing one of the three is the
/// provisioner's own question, not a version.
/// </summary>
public sealed class MySqlTriggerInstall : ITriggerAuditInstallCheck
{
    public static MySqlTriggerInstall Instance { get; } = new();

    public Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken) =>
        InspectAsync(connection, source.Schema, source.Table, cancellationToken);

    public static async Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT action_statement FROM information_schema.triggers
            WHERE trigger_schema = @schema AND event_object_table = @table AND trigger_name = @trigger;
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@table", table);
        cmd.AddParameter("@trigger", MySqlTriggerAudit.TriggerName(table, "au"));

        var statement = await cmd.ExecuteScalarAsync(cancellationToken);
        return statement switch
        {
            null or DBNull => TriggerAuditInstall.Absent,
            _ => MySqlTriggerAudit.IsCurrentUpdateTrigger(Convert.ToString(statement))
                ? TriggerAuditInstall.Current
                : TriggerAuditInstall.Outdated,
        };
    }
}
