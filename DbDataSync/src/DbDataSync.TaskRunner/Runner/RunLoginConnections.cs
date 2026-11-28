using System.Data.Common;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Generic;
using DbDataSync.Libraries;
using DbDataKit.Sync.Provisioning;
using DbDataSync.Scripting;
using DbDataKit.Sync.Scripting.Abstractions;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// Every connection with its run login (the slot the runner bound), whatever role the caller names: a runner holds no other login. The set's manual tasks
/// (<c>provision</c>, <c>preflight</c>) open their connections through it, the way a drain does.
/// </summary>
internal sealed class RunLoginConnections(ConfigRepository config, DriverRegistry drivers, LibraryRegistry libraries, string repoRoot, SecretStore secrets) : IConnectionFactory
{
    public async Task<(DbConnection Connection, IDriver Driver)> OpenAsync(string connectionName, CredentialRole role, CancellationToken cancellationToken)
    {
        var connection = config.LoadConnection(connectionName);
        var driver = drivers.Get(connection.DriverType);
        return (await RunExecutor.OpenWithRunLoginAsync(connection, driver, libraries, repoRoot, secrets, cancellationToken).ConfigureAwait(false), driver);
    }
}

/// <summary>The catalog a provisioner reads a target back from: the connection's metadata script if one is bound, else the driver, as the Sync server's metadata service reads it.</summary>
internal sealed class RunLoginColumnCatalog(IConnectionFactory connections, ScriptedMetadata metadata) : IColumnCatalog
{
    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Run, cancellationToken).ConfigureAwait(false);
        await using (connection)
            return await metadata.ListColumnsAsync(connectionName, connection, driver, (IScriptDialect?)ScriptDialectAdapter.For(driver) ?? DialectlessScriptDialect.Instance, database, schema, table, cancellationToken).ConfigureAwait(false);
    }
}
