using DbDataKit.Connections;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Connections;
using DbDataKit.Runner.Kit;
using DbDataKit.Secrets;

namespace DbDataRunner.Server;

/// <summary>
/// The connect step of a slot's binding test (UI08): opens the resolved grant with the engine's provider (<c>IDbProvider.TestAsync</c>) and answers null when it opened, else a fixed sentence naming the class of the failure
/// (<see cref="ConnectionFailure.PublicMessage"/>: text per kind plus the engine's error code), never a driver message and never a value. The grant's login is built into a <see cref="ResolvedLogin"/> that is disposed here. The
/// connection's own document supplies what the grant does not carry (TLS, timeouts, properties) when the runner's catalog has it. SQL Server and PostgreSQL only, the engines a runner's tasks have a provider for here; another engine
/// is answered as not tried, so the test says the connection was not opened rather than that it failed.
/// </summary>
public static class RunnerConnectTest
{
    private static readonly ProviderRegistry Providers = new ProviderRegistry().Register(new SqlServerProvider()).Register(new PostgresProvider());

    public static RunnerBindingTestApi.ConnectTest Create(IConnectionCatalog catalog) => async (grant, ct) =>
    {
        if (!Providers.TryGet(grant.Engine, out var provider)) return $"The engine '{grant.Engine}' cannot be tried from this runner yet (sqlserver and postgres can); the login resolved and no connection was opened.";
        var role = grant.Role switch { "read" => LoginRole.Read, "write" => LoginRole.Write, _ => LoginRole.Default };
        ConnectionDefinition definition;
        try { definition = catalog.Find(grant.Connection) ?? new ConnectionDefinition { Name = grant.Connection, Engine = grant.Engine }; }
        catch (InvalidDataException) { return "The connection's document cannot be read."; }
        // the grant is the truth for what the run would use: engine, address and string come from it
        definition.Engine = grant.Engine;
        definition.ConnectionString ??= grant.ConnectionString;
        var mode = Enum.TryParse<AuthMode>(grant.AuthMode, true, out var m) ? m : AuthMode.None;
        using var login = new ResolvedLogin(role, mode, "runner:" + grant.Connection)
        {
            UserName = grant.User,
            Password = grant.Password is null ? null : SecretValue.From(grant.Password),
            ConnectionString = grant.ConnectionStringSecret is null ? null : SecretValue.From(grant.ConnectionStringSecret),
            Token = grant.Token is null ? null : SecretValue.From(grant.Token),
        };
        try
        {
            var result = await provider.TestAsync(new ConnectionContext { Definition = definition, Login = login, Role = role }, ct).ConfigureAwait(false);
            return result.Success ? null : result.Failure?.PublicMessage ?? "The connection could not be opened.";
        }
        catch (ConnectionFailureException ex) { return ex.Failure.PublicMessage; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return $"The connection could not be opened ({ex.GetType().Name})."; }
    };
}
