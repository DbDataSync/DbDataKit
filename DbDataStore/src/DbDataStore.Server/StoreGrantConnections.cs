using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.DataModel.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Secrets;

namespace DbDataStore.Server;

/// <summary>
/// How the <c>dbdatastore</c> runtime opens a model's storage inside a task (plan 33 SC24): the slot <c>storage</c> is a <see cref="StorageGrant"/>, made here from what the runner handed it (<see cref="SlotFactory"/>, set as
/// <c>RemoteRunOptions.SlotFactory</c>), with the connection's definition as the runner keeps it (secret references only, never resolved here), the address and the login of the grant, through the shared providers. SQL Server and
/// PostgreSQL, the engines a model's storage supports. The login is wiped once the connection is open. The drivers are this program's, not the library's, so a server that only builds a model's package reaches no data source.
/// </summary>
public static class StoreGrantConnections
{
    public static ProviderRegistry Providers { get; } = new ProviderRegistry().Register(new SqlServerProvider()).Register(new PostgresProvider());

    /// <summary>The typed handle of a slot: a <see cref="StorageGrant"/> for the storage slot, the grant itself for any other.</summary>
    public static object SlotFactory(ConnectionGrant grant, Type type) =>
        type == typeof(StorageGrant) ? new StorageGrant(grant, (role, ct) => OpenAsync(grant, role, ct)) : grant;

    /// <summary>Opens the connection the grant names with its login, as <paramref name="role"/>. Throws <see cref="ConnectionFailureException"/> naming the class of failure, never a value.</summary>
    public static async Task<DbConnection> OpenAsync(ConnectionGrant grant, LoginRole role, CancellationToken ct)
    {
        if (!Providers.TryGet(grant.Engine, out var provider))
            throw new InvalidOperationException($"The connection '{grant.Connection}' is a {grant.Engine} connection; a model's storage is on sqlserver or postgres.");
        var definition = StorageGrant.DefinitionOf(grant);
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
