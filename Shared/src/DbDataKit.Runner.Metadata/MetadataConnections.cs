using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Connections.MySql;
using DbDataKit.Connections.Oracle;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Dialect;
using DbDataKit.Metadata;
using DbDataKit.Runner.Kit;
using DbDataKit.Secrets;

namespace DbDataKit.Runner.Metadata;

/// <summary>
/// How the package's tasks open the connection the runner resolved for their slot: the engine's provider with the grant's address, its login (of the role the grant carries) and the connection's own document as the runner keeps it (TLS,
/// timeouts; secret references only), and the catalog reader of that engine. SQL Server, PostgreSQL, MySQL and MariaDB, and Oracle: the engines a Sync set, a Build project or a Store model runs on.
/// </summary>
internal static class MetadataConnections
{
    private static readonly ProviderRegistry Providers = new ProviderRegistry().Register(new SqlServerProvider()).Register(new PostgresProvider()).Register(new MySqlProvider(), "mariadb").Register(new OracleProvider());

    /// <summary>The provider of the grant's engine; an engine the package cannot open is refused naming it.</summary>
    public static IDbProvider ProviderOf(ConnectionGrant grant) =>
        Providers.TryGet(grant.Engine, out var provider)
            ? provider
            : throw new NotSupportedException($"The connection '{grant.Connection}' is a {grant.Engine} connection, which the built-in package cannot read (it reads sqlserver, postgres, mysql, mariadb and oracle).");

    /// <summary>The definition the grant stands for: the runner's text when it has one that reads, else one made of the grant's engine and address. The grant is the truth for the engine and the address string.</summary>
    public static ConnectionDefinition DefinitionOf(ConnectionGrant grant)
    {
        ConnectionDefinition? definition = null;
        if (grant.Definition is { Length: > 0 } text && ConnectionDefinitionLoader.LoadDatabaseConnection(text, grant.Connection + ".yaml", addressRequired: false) is { Ok: true } loaded)
            definition = loaded.Definition;
        definition ??= new ConnectionDefinition { Name = grant.Connection };
        definition.Engine = grant.Engine;
        definition.ConnectionString ??= grant.ConnectionString;
        return definition;
    }

    /// <summary>The dialect the read-only policy checks a query with.</summary>
    public static ISqlDialect DialectOf(ConnectionGrant grant) => ProviderOf(grant).Dialect(DefinitionOf(grant));

    /// <summary>Opens the connection with the grant's login. The login is wiped once the connection is open.</summary>
    public static async Task<DbConnection> OpenAsync(ConnectionGrant grant, CancellationToken ct)
    {
        var provider = ProviderOf(grant);
        var role = grant.Role switch { "read" => LoginRole.Read, "write" => LoginRole.Write, _ => LoginRole.Default };
        var mode = Enum.TryParse<AuthMode>(grant.AuthMode, true, out var parsed) ? parsed : AuthMode.None;
        using var login = new ResolvedLogin(role, mode, "runner:" + grant.Connection)
        {
            UserName = grant.User,
            Password = grant.Password is null ? null : SecretValue.From(grant.Password),
            ConnectionString = grant.ConnectionStringSecret is null ? null : SecretValue.From(grant.ConnectionStringSecret),
            Token = grant.Token is null ? null : SecretValue.From(grant.Token),
        };
        return await provider.OpenAsync(new ConnectionContext { Definition = DefinitionOf(grant), Login = login, Role = role }, ct).ConfigureAwait(false);
    }

    /// <summary>The catalog reader of the grant's engine over an open connection.</summary>
    public static ICatalogReader ReaderOf(ConnectionGrant grant, DbConnection connection) => ProviderOf(grant).Engine.Value switch
    {
        "sqlserver" => new SqlServerCatalogReader(connection),
        "postgres" => new PostgresCatalogReader(connection),
        "mysql" => new MySqlCatalogReader(connection),
        "oracle" => new OracleCatalogReader(connection),
        var other => throw new NotSupportedException($"The connection '{grant.Connection}' is a {other} connection, which the built-in package has no catalog reader for."),
    };
}
