using System.Data.Common;
using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State;

/// <summary>The one place a product turns its state settings into an open <see cref="StateDatabase"/>, so the console, the Proxy and any command that opens the same store reach it the same way.</summary>
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
    /// </summary>
    public static StateDatabase Open(StateSettings settings, string sqlitePath, IEnumerable<StateModule> modules, StateScope? scope = null, ISecretResolver? secrets = null, string? appliedBy = null, TimeProvider? time = null)
    {
        var engine = settings.EngineId;
        var layout = StateLayout.Create(engine, modules, scope, settings.Namespaces);
        if (engine == StateEngineIds.Sqlite) return new StateDatabase(sqlitePath, layout, appliedBy, time);

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException($"The state engine is {settings.Engine}, which needs a connection string (state connection). Only SQLite is configured by a file.");

        lock (Registration) StateDialectRegistry.Default.RegisterLibraryBackedEngines(Factory);
        return new StateDatabase(engine, ResolveSecrets(settings.ConnectionString, secrets), layout, appliedBy, time);
    }

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
