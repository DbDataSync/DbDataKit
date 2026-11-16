using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Runner.Kit;

namespace DbDataKit.DataModel.Runner;

/// <summary>
/// The model's storage as a task gets it from its slot <c>storage</c>: what the runner resolved for the connection bound there (<see cref="Grant"/>), the definition it stands for (<see cref="Definition"/>: TLS, timeouts, the address; secret
/// references only), and a way to open it. The opening is the runtime's (<c>dbdatastore --runner</c> makes this from the grant with the database providers, as <c>RemoteRunOptions.SlotFactory</c>), so this library references no driver and a
/// server that only builds the package (the Workspace) reaches no data source.
/// </summary>
public sealed class StorageGrant(ConnectionGrant grant, Func<LoginRole, CancellationToken, Task<DbConnection>> open)
{
    public ConnectionGrant Grant { get; } = grant;

    public string Engine => Grant.Engine;

    /// <summary>The definition the grant stands for: the runner's text when it has one that reads, else one made of the grant's engine and address. The grant is the truth for the engine and the address string.</summary>
    public ConnectionDefinition Definition { get; } = DefinitionOf(grant);

    /// <summary>Opens the connection with the grant's login, as <paramref name="role"/>.</summary>
    public Task<DbConnection> OpenAsync(LoginRole role, CancellationToken ct) => open(role, ct);

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
}
