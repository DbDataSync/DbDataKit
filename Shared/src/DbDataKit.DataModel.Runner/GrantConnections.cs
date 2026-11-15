using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Runner.Kit;
using DbDataKit.Secrets;

namespace DbDataKit.DataModel.Runner;

/// <summary>
/// A database connection opened inside a task from what the runner handed it for a slot (<see cref="ConnectionGrant"/>): the connection's definition as the runner keeps it (TLS, timeouts, properties; secret references only, never
/// resolved here) with the address and the login of the grant, through the shared providers (<c>IDbProvider.OpenAsync</c>). SQL Server and PostgreSQL, the engines a model's storage supports. The login is wiped once the connection is open.
/// </summary>
public static class GrantConnections
{
    public static ProviderRegistry Providers { get; } = new ProviderRegistry().Register(new SqlServerProvider()).Register(new PostgresProvider());

    /// <summary>The definition the grant stands for: the runner's text when it has one that reads, else one made of the grant's engine and address.</summary>
    public static ConnectionDefinition DefinitionOf(ConnectionGrant grant)
    {
        ConnectionDefinition? definition = null;
        if (grant.Definition is { Length: > 0 } text && ConnectionDefinitionLoader.LoadDatabaseConnection(text, grant.Connection + ".yaml", addressRequired: false) is { Ok: true } loaded)
            definition = loaded.Definition;
        definition ??= new ConnectionDefinition { Name = grant.Connection };
        // the grant is the truth for what the run uses: engine, address and string come from it
        definition.Engine = grant.Engine;
        definition.ConnectionString ??= grant.ConnectionString;
        return definition;
    }

    /// <summary>Opens the connection the grant names with its login, as <paramref name="role"/>. Throws <see cref="ConnectionFailureException"/> naming the class of failure, never a value.</summary>
    public static async Task<DbConnection> OpenAsync(ConnectionGrant grant, LoginRole role, CancellationToken ct)
    {
        if (!Providers.TryGet(grant.Engine, out var provider))
            throw new InvalidOperationException($"The connection '{grant.Connection}' is a {grant.Engine} connection; a model's storage is on sqlserver or postgres.");
        var definition = DefinitionOf(grant);
        var mode = Enum.TryParse<AuthMode>(grant.AuthMode, true, out var parsed) ? parsed : AuthMode.None;
        using var login = new ResolvedLogin(role, mode, "runner:" + grant.Connection)
        {
            UserName = grant.User,
            Password = grant.Password is null ? null : SecretValue.From(grant.Password),
            ConnectionString = grant.ConnectionStringSecret is null ? null : SecretValue.From(grant.ConnectionStringSecret),
            Token = grant.Token is null ? null : SecretValue.From(grant.Token),
        };
        return await provider.OpenAsync(new ConnectionContext { Definition = definition, Login = login, Role = role }, ct).ConfigureAwait(false);
    }
}
