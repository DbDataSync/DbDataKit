using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;
using DbDataSync.Provisioning;

namespace DbDataSync.Scripting;


/// <summary>
/// On-demand schema introspection for a configured connection (architecture/detailed-design.md §3.1)
/// — opens a connection, runs one metadata query, closes it. No data movement.
/// <para>
/// Every answer goes through <see cref="ScriptedMetadata"/>, so a connection with a bound
/// <c>metadataProvider</c> script answers with what that script says and one without falls straight
/// through to the driver at no cost. This is the surface an operator *browses and maps against*; the
/// pipeline reads the driver's own catalog — see phase 29.
/// </para>
/// <para>
/// The connections come from the factory it is given: <see cref="DriverConnectionFactory"/> opens them with the connection's read login, and a task on a
/// Runner hands one that opens them with the login its channel binds.
/// </para>
/// </summary>
public sealed class MetadataService(IConnectionFactory connections, ScriptedMetadata scriptedMetadata)
    : IColumnCatalog
{
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionName, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Read, cancellationToken);
        await using (connection)
            return await scriptedMetadata.ListDatabasesAsync(
                connectionName, connection, driver, DialectFor(driver), cancellationToken);
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(string connectionName, string database, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Read, cancellationToken);
        await using (connection)
            return await scriptedMetadata.ListTablesAsync(
                connectionName, connection, driver, DialectFor(driver), database, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Read, cancellationToken);
        await using (connection)
            return await scriptedMetadata.ListColumnsAsync(
                connectionName, connection, driver, DialectFor(driver), database, schema, table, cancellationToken);
    }

    /// <summary>
    /// The tables of the database the connection opens and the columns of each table <paramref name="wanted"/> keeps, over one connection: what a read of a
    /// whole catalog needs, without opening a connection per table or switching database. The answer names that database as the connection reports it, has every
    /// table the catalog lists, and each table kept with its columns in the order the catalog gives them.
    /// </summary>
    public async Task<(string Database, IReadOnlyList<TableMetadata> Listed, IReadOnlyList<(TableMetadata Table, IReadOnlyList<ColumnMetadata> Columns)> Read)> ReadCatalogAsync(
        string connectionName, Func<TableMetadata, bool> wanted, CancellationToken cancellationToken)
    {
        var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Read, cancellationToken);
        await using (connection)
        {
            var dialect = DialectFor(driver);
            var tables = await scriptedMetadata.ListTablesAsync(connectionName, connection, driver, dialect, "", cancellationToken);
            var read = new List<(TableMetadata, IReadOnlyList<ColumnMetadata>)>();
            foreach (var table in tables.Where(wanted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                read.Add((table, await scriptedMetadata.ListColumnsAsync(connectionName, connection, driver, dialect, "", table.Schema, table.Table, cancellationToken)));
            }

            return (connection.Database ?? "", tables, read);
        }
    }

    /// <summary>
    /// A driver that names no dialect still browses fine — the dialect is only there for a script that
    /// wants to quote or translate a type, and a script bound to such a connection will find out when it
    /// asks. Failing here would stop the pickers working for a driver that has no scripts at all.
    /// </summary>
    private static IScriptDialect DialectFor(IDriver driver) =>
        ScriptDialectAdapter.For(driver) ?? DialectlessScriptDialect.Instance;
}
