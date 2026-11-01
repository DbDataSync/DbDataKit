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
/// (<c>write_login</c>, then <c>DBDATA_DBDATASTORE_&lt;NAME&gt;_WRITE</c>, then <c>auth</c>), secrets through <c>env:</c> and <c>file:</c> references. SQL Server and PostgreSQL only, the engines the storage is written for.
/// </summary>
public sealed class StoreConnections(string directory, ISecretResolver? secrets = null)
{
    public const string Folder = "connections";
    public const string Application = "dbdatastore";

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
        var loaded = ConnectionDefinitionLoader.LoadFile(file);
        if (loaded.Definition is not { } definition) throw new StoreConnectionException($"{name}.yaml is not a valid connection: {string.Join(" ", loaded.Diagnostics.Select(d => d.ToString()))}");
        return definition;
    }

    public async Task<DbConnection> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var definition = Load(name);
        if (!providers.TryGet(definition.Engine, out var provider)) throw new StoreConnectionException($"{name}.yaml names the engine '{definition.Engine}', which a model's storage does not support; use sqlserver or postgres.");
        try
        {
            using var login = await new LoginResolver(secretResolver, Application).ResolveAsync(definition, LoginRole.Write, cancellationToken).ConfigureAwait(false)
                ?? throw new StoreConnectionException($"{name}.yaml has no login for the server to write with: give it a write_login or an auth, or set DBDATA_DBDATASTORE_{name.ToUpperInvariant().Replace('-', '_')}_WRITE.");
            return await provider.OpenAsync(new ConnectionContext { Definition = definition, Login = login, Role = LoginRole.Write }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectionFailureException ex) { throw new StoreConnectionException($"The server could not connect with {name}.yaml ({ex.Failure.Kind})."); }
        catch (SecretNotFoundException ex) { throw new StoreConnectionException($"{name}.yaml needs a secret the server cannot find: {ex.Message}"); }
    }
}
