using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Secrets;

namespace DbDataStore.Server;

/// <summary>The connection a model's tables live on could not be used; the message says which file and why, and never carries a secret.</summary>
public sealed class StoreConnectionException(string message) : Exception(message);

/// <summary>
/// Opens the connection a model names in <c>storage.connection</c> (plan 19, Z09): <c>connections/&lt;name&gt;.yaml</c> in the server's directory, a <c>connection/1</c> file, logged in with its write login
/// (<c>write_login</c>, then <c>DBDATAKIT_STORE_&lt;NAME&gt;_WRITE</c>, then <c>auth</c>), secrets through <c>env:</c> and <c>file:</c> references. SQL Server and PostgreSQL only, the engines the storage is written for.
/// </summary>
public sealed class StoreConnections(string directory, ISecretResolver? secrets = null)
{
    public const string Folder = "connections";
    public const string Application = "store";

    private readonly ProviderRegistry providers = new ProviderRegistry().Register(new SqlServerProvider()).Register(new PostgresProvider());
    private readonly ISecretResolver secretResolver = secrets ?? new SecretResolverChain([new EnvSecretProvider(), new FileSecretProvider()]);

    public static string FileOf(string directory, string name) => Path.Combine(directory, Folder, name + ".yaml");

    /// <summary>The engine the connection's file names, as the storage and the writer spell it (<c>sqlserver</c> or <c>postgres</c>).</summary>
    public string EngineOf(string name)
    {
        var definition = Load(name);
        if (!providers.TryGet(definition.Engine, out var provider)) throw new StoreConnectionException($"{name}.yaml names the engine '{definition.Engine}', which a model's storage does not support; use sqlserver or postgres.");
        return provider.Engine.Value;
    }

    private DbDataKit.Connections.ConnectionDefinition Load(string name)
    {
        var file = FileOf(directory, name);
        if (!StoreModels.IsValidName(name) || !File.Exists(file)) throw new StoreConnectionException($"There is no connection '{name}': a model's storage.connection names a file connections/{name}.yaml in the server's directory.");
        var loaded = ConnectionDefinitionLoader.LoadDatabaseFile(file);
        if (loaded.Definition is not { } definition) throw new StoreConnectionException($"{name}.yaml is not a valid connection: {string.Join(" ", loaded.Diagnostics.Select(d => d.ToString()))}");
        return definition;
    }

    /// <summary>
    /// The connection's engine and the whole connection string with its write login, for publishing the model's storage to a serving group (plan 20 O10), where it is kept as a secret. It holds a password: it goes over the
    /// pinned channel to the group's own encrypted store and nowhere else.
    /// </summary>
    public async Task<(string Engine, string ConnectionString)> WriteConnectionStringAsync(string name, CancellationToken cancellationToken)
    {
        var definition = Load(name);
        if (!providers.TryGet(definition.Engine, out var provider)) throw new StoreConnectionException($"{name}.yaml names the engine '{definition.Engine}', which a model's storage does not support; use sqlserver or postgres.");
        try
        {
            using var login = await new LoginResolver(secretResolver, Application).ResolveAsync(definition, LoginRole.Write, cancellationToken).ConfigureAwait(false)
                ?? throw new StoreConnectionException($"{name}.yaml has no login for the server to write with: give it a write_login or an auth, or set DBDATAKIT_STORE_{name.ToUpperInvariant().Replace('-', '_')}_WRITE.");
            return (provider.Engine.Value, provider.Composer.Compose(new ConnectionContext { Definition = definition, Login = login, Role = LoginRole.Write }));
        }
        catch (SecretNotFoundException ex) { throw new StoreConnectionException($"{name}.yaml needs a secret the server cannot find: {ex.Message}"); }
    }

    private static readonly HashSet<string> AddressKeys = new(StringComparer.OrdinalIgnoreCase)
        { "server", "data source", "host", "address", "addr", "network address", "port", "database", "initial catalog", "dbname" };

    /// <summary>
    /// The connection as lineage names a table's home (plan 18, G12): its engine and only the address keys (server, port, database), from the file's own address or, as a model's connection usually has none, from the string its secret reference
    /// resolves to. The login's user and password are dropped before anything is returned. Null with a reason when the file or the secret cannot be read.
    /// </summary>
    public async Task<(DbDataKit.Connections.ConnectionDefinition? Definition, string? Problem)> AddressOnlyAsync(string name, CancellationToken cancellationToken)
    {
        DbDataKit.Connections.ConnectionDefinition definition;
        try { definition = Load(name); } catch (StoreConnectionException ex) { return (null, ex.Message); }
        if (definition.Address is not null) return (definition, null);
        try
        {
            using var login = await new LoginResolver(secretResolver, Application).ResolveAsync(definition, LoginRole.Write, cancellationToken).ConfigureAwait(false);
            if (login?.ConnectionString is not { } secret) return (null, $"{name}.yaml has no connection string the server can read, so its address is not known.");
            var text = string.Join(';', secret.RevealString().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Split('=', 2) is [var key, _] && AddressKeys.Contains(key.Trim())));
            return (new DbDataKit.Connections.ConnectionDefinition { Name = name, Engine = definition.Engine, ConnectionString = text }, null);
        }
        catch (SecretNotFoundException ex) { return (null, $"{name}.yaml needs a secret the server cannot find: {ex.Message}"); }
    }

    public async Task<DbConnection> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var definition = Load(name);
        if (!providers.TryGet(definition.Engine, out var provider)) throw new StoreConnectionException($"{name}.yaml names the engine '{definition.Engine}', which a model's storage does not support; use sqlserver or postgres.");
        try
        {
            using var login = await new LoginResolver(secretResolver, Application).ResolveAsync(definition, LoginRole.Write, cancellationToken).ConfigureAwait(false)
                ?? throw new StoreConnectionException($"{name}.yaml has no login for the server to write with: give it a write_login or an auth, or set DBDATAKIT_STORE_{name.ToUpperInvariant().Replace('-', '_')}_WRITE.");
            return await provider.OpenAsync(new ConnectionContext { Definition = definition, Login = login, Role = LoginRole.Write }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectionFailureException ex) { throw new StoreConnectionException($"The server could not connect with {name}.yaml ({ex.Failure.Kind})."); }
        catch (SecretNotFoundException ex) { throw new StoreConnectionException($"{name}.yaml needs a secret the server cannot find: {ex.Message}"); }
    }
}
