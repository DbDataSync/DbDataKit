using System.Data.Common;

namespace DbDataSync.State;

/// <summary>
/// Sync's state database (architecture/detailed-design.md §3.7), shared by DbDataSync.Api and every DbDataSync.TaskRunner process: the shared <see cref="DbDataKit.State.StateDatabase"/> built from
/// <see cref="SyncState.Modules"/> (plan 28c slice E14): Sync's own <c>sync_state</c> and the shared <c>runs</c>, <c>scheduling</c>, <c>notifications</c>, <c>audit</c> and <c>runner_progress</c> modules, each versioned
/// on its own. SQLite by default; phase 63 added SQL Server and PostgreSQL as alternatives.
/// <para>
/// A state database made by Sync before the modules (one schema version for the whole database: <c>TaskRuns</c>, <c>ChangeWatermarks</c>, ...) is not converted and not touched. Opening it fails with
/// <see cref="OldStateException"/>, which names the database and the ways to carry on (plan 28c decision ED15).
/// </para>
/// </summary>
public sealed partial class StateDatabase : DbDataKit.State.StateDatabase
{
    /// <summary>The SQLite path constructor, unchanged from before there was a choice.</summary>
    public StateDatabase(string sqliteFilePath, TimeProvider? time = null)
        : base(sqliteFilePath, CheckedLayout(DbDataKit.State.StateEngineIds.Sqlite, SqliteConnectionString(sqliteFilePath), sqliteFilePath), SyncState.AppliedBy, time)
    {
    }

    public StateDatabase(string engine, string connectionString, TimeProvider? time = null)
        : base(engine, connectionString, CheckedLayout(engine, connectionString, null), SyncState.AppliedBy, time)
    {
    }

    /// <summary>
    /// The layout for <paramref name="engine"/>, after checking that the database is not one the old structure made. The check reads and writes nothing, so a refused database is left exactly as it was found
    /// (the previous release still reads it).
    /// </summary>
    private static DbDataKit.State.StateLayout CheckedLayout(string engine, string connectionString, string? sqliteFile)
    {
        var layout = DbDataKit.State.StateLayout.Create(engine, SyncState.Modules, SyncState.Scope);
        var dialect = DbDataKit.State.StateDialect.For(engine);
        using var connection = dialect.CreateConnection(connectionString);
        connection.Open();
        if (dialect.TableExists(connection, layout.Registry))
            return layout;

        if (dialect.FindLegacySchemaVersion(connection) is { } marker)
            throw new OldStateException(Describe(connection, sqliteFile), marker);
        return layout;
    }

    private static string Describe(DbConnection connection, string? sqliteFile) =>
        sqliteFile is not null ? Path.GetFullPath(sqliteFile) : $"the database '{connection.Database}' on '{connection.DataSource}'";
}

/// <summary>
/// The state database is one that Sync made before its state was built from modules (plan 28c, E14, decision ED15: a clean break, nothing is converted). The message names the database and the ways forward; the old
/// database is not opened for writing and not changed.
/// </summary>
public sealed class OldStateException(string database, string marker) : InvalidOperationException(MessageFor(database, marker))
{
    /// <summary>The state database that was refused, as it was named (a file's full path, or a database on a server).</summary>
    public string Database { get; } = database;

    private static string MessageFor(string database, string marker) =>
        $"DbDataSync's state database {database} was made by an earlier release ({marker}), before its state was kept in modules, and is not converted. It has not been changed. " +
        "To carry on, choose one: " +
        "(1) replay changes from the earliest position the source still has (the log or change-tracking retention): start DbDataSync on a new state database (a new file, or a new database on the server) and set " +
        "'Default read intent' to ChangesFromEarliest on the replications, so a table whose position is no longer available is then reloaded alone; " +
        "(2) set each mapping's position by hand on a new state database (the read-state recovery on the mapping, with the position the old release showed); " +
        "(3) stay on the previous release, which still reads this database as it is; " +
        "(4) start with a new state database and let every mapping begin from its configured initial load. " +
        "In every case point DbDataSync at the new state database (DbDataSync:State:DbPath, or DbDataSync:State:Engine and ConnectionString) and leave this one where it is.";
}
