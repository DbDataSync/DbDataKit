using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// Which text the installed capture function has (<see cref="PostgresTriggerAudit.VersionMarker"/> in
/// <c>pg_proc.prosrc</c>, which keeps the body exactly as it was created). Used by the reader before every
/// pass and by the provisioner to plan the replacement.
/// </summary>
public sealed class PostgresTriggerInstall : ITriggerAuditInstallCheck
{
    public static PostgresTriggerInstall Instance { get; } = new();

    public Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, SourceTableRef source, CancellationToken cancellationToken) =>
        InspectAsync(connection, source.Schema, source.Table, cancellationToken);

    public static async Task<TriggerAuditInstall> InspectAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT p.prosrc FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.proname = @function;
            """;
        cmd.AddParameter("@schema", schema);
        cmd.AddParameter("@function", PostgresTriggerAudit.FunctionName(table));

        return await cmd.ExecuteScalarAsync(cancellationToken) switch
        {
            string body when body.Contains(PostgresTriggerAudit.VersionMarker, StringComparison.Ordinal) => TriggerAuditInstall.Current,
            string => TriggerAuditInstall.Outdated,
            _ => TriggerAuditInstall.Absent,
        };
    }
}
