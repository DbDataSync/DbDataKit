using System.Data.Common;
using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State;

/// <summary>The one place a product turns its state settings into an open <see cref="StateDatabase"/>, so the console, the Gateway and any command that opens the same store reach it the same way.</summary>
public static class StateOpener
{
    private static readonly object Registration = new();

    /// <summary>
    /// Opens the state database. SQLite is the file at <paramref name="sqlitePath"/>; the other engines are the server in <see cref="StateSettings.ConnectionString"/>, with any secret reference in it resolved through
    /// <paramref name="secrets"/> when the connection is opened. A server engine with no connection string, or a secret that cannot be resolved, stops the start with a message naming what is missing, never the value.
    /// </summary>
    public static StateDatabase Open(StateSettings settings, string sqlitePath, StateSchema schema, ISecretResolver? secrets = null)
    {
        var engine = settings.EngineId;
        if (engine == StateEngineIds.Sqlite) return new StateDatabase(sqlitePath, schema);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException($"The state engine is {settings.Engine}, which needs a connection string (state connection). Only SQLite is configured by a file.");

        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        var connectionString = settings.ConnectionString;
        if (secrets is not null)
        {
            try { connectionString = ConnectionStringReferences.ResolveAsync(connectionString, secrets, ConnectionStringReferences.QuoteDouble).AsTask().GetAwaiter().GetResult(); }
            catch (SecretNotFoundException ex) { throw new InvalidOperationException($"The state connection string refers to the secret '{ex.Reference}', which no secret provider has.", ex); }
        }
        return new StateDatabase(engine, connectionString, schema);
    }

    /// <summary>
    /// Opens a state database built from modules (plan 29): the engine and connection as for <see cref="Open(StateSettings, string, StateSchema, ISecretResolver?)"/>, the
    /// namespaces from <see cref="StateSettings.Namespaces"/>, and <paramref name="scope"/> naming the group and service this process is. Applies what the modules need and
    /// refuses an old database, a module newer than this service, or settings that no longer match what was created, with a message naming the database and the setting.
    /// <paramref name="groupInstances"/> is the product's source of the live instances of its group (U02): a migration is refused while one of them cannot work with the new schema.
    /// </summary>
    public static StateDatabase Open(StateSettings settings, string sqlitePath, IEnumerable<StateModule> modules, StateScope? scope = null, ISecretResolver? secrets = null, string? appliedBy = null, TimeProvider? time = null, IGroupInstanceSource? groupInstances = null)
    {
        var engine = settings.EngineId;
        var layout = StateLayout.Create(engine, modules, scope, settings.Namespaces, groupInstances: groupInstances);
        if (engine == StateEngineIds.Sqlite) return new StateDatabase(sqlitePath, layout, appliedBy, time);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException($"The state engine is {settings.Engine}, which needs a connection string (state connection). Only SQLite is configured by a file.");

        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        return new StateDatabase(engine, ResolveSecrets(settings.ConnectionString, secrets), layout, appliedBy, time);
    }

    /// <summary>Makes the SQL Server and PostgreSQL providers known to the state layer (what <see cref="Open(StateSettings, string, IEnumerable{StateModule}, StateScope?, ISecretResolver?, string?, TimeProvider?, IGroupInstanceSource?)"/> does first), for a command that connects without opening a <see cref="StateDatabase"/>.</summary>
    public static void RegisterProviders()
    {
        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
    }

    /// <summary>The connection string with the secret references in it resolved through <paramref name="secrets"/>; a secret nobody has stops with a message naming the reference, never a value. Without a resolver the string is returned as it is.</summary>
    public static string ResolveConnectionString(string connectionString, ISecretResolver? secrets) => ResolveSecrets(connectionString, secrets);

    /// <summary>
    /// Opens the database <b>without creating or migrating anything</b>: what a snapshot export reads, a verification compares and a snapshot import fills (<see cref="StateDatabase.OpenForImport"/>). The engine, connection,
    /// namespaces and secret references are as for <see cref="Open(StateSettings, string, IEnumerable{StateModule}, StateScope?, ISecretResolver?, string?, TimeProvider?, IGroupInstanceSource?)"/>; nothing is written, and no connection is made until it is used.
    /// </summary>
    public static StateDatabase OpenWithoutMigrating(StateSettings settings, string? sqlitePath, IEnumerable<StateModule> modules, StateScope? scope = null, ISecretResolver? secrets = null, string? appliedBy = null, TimeProvider? time = null)
    {
        var engine = settings.EngineId;
        var layout = StateLayout.Create(engine, modules, scope, settings.Namespaces);
        if (engine == StateEngineIds.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(sqlitePath)) throw new InvalidOperationException("The state engine is SQLite, which is a file: the path of the file is needed.");
            return StateDatabase.OpenSqliteForImport(sqlitePath, layout, appliedBy, time);
        }

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException($"The state engine is {settings.Engine}, which needs a connection string (state connection). Only SQLite is configured by a file.");

        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        return StateDatabase.OpenForImport(engine, ResolveSecrets(settings.ConnectionString, secrets), layout, appliedBy, time);
    }

    /// <summary>The connection string with its secret references resolved (for code that has to hide the values again); null for SQLite.</summary>
    internal static string? ResolveConnectionString(StateSettings settings, ISecretResolver? secrets) =>
        settings.IsSqlite || string.IsNullOrWhiteSpace(settings.ConnectionString) ? null : ResolveSecrets(settings.ConnectionString, secrets);

    private static string ResolveSecrets(string connectionString, ISecretResolver? secrets)
    {
        if (secrets is null) return connectionString;
        try { return ConnectionStringReferences.ResolveAsync(connectionString, secrets, ConnectionStringReferences.QuoteDouble).AsTask().GetAwaiter().GetResult(); }
        catch (SecretNotFoundException ex) { throw new InvalidOperationException($"The state connection string refers to the secret '{ex.Reference}', which no secret provider has.", ex); }
    }

    private static DbProviderFactory Factory(string libraryId) => libraryId switch
    {
        MsSqlStateDialect.LibraryId => SqlClientFactory.Instance,
        PostgresStateDialect.LibraryId => NpgsqlFactory.Instance,
        _ => throw new InvalidOperationException($"No provider is built in for '{libraryId}'."),
    };
}
