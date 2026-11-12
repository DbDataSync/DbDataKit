using DbDataKit.Connections;
using DbDataKit.Connections.Descriptor;
using DbDataKit.Secrets;

namespace DbDataGateway.Server.Data;

/// <summary>
/// What the suite's engine registry (plan 14a SL02) says about the engines a data source can name: the providers the Gateway registers
/// (<see cref="Providers"/>, the one list <c>GatewayHost</c> uses) and the sentence a data source gets when its engine is not one of them. The registry's
/// <c>gateway_source</c> role is the claim, a test holds it to this list in both directions, and a refusal says what the descriptor says.
/// </summary>
public static class GatewaySources
{
    /// <summary>The providers of the engines the Gateway opens (and Flight SQL, a gateway reaching another gateway, which the role does not mean).</summary>
    public static ProviderRegistry Providers(IRedactor? redactor = null, bool includeRawFailures = false) => new(
    [
        new DbDataKit.Connections.Odbc.OdbcProvider(redactor: redactor, includeRawFailures: includeRawFailures),
        new DbDataKit.Connections.SqlServer.SqlServerProvider(redactor: redactor, includeRawFailures: includeRawFailures),
        new DbDataKit.Connections.DuckDb.DuckDbProvider(redactor: redactor, includeRawFailures: includeRawFailures),
        new DbDataKit.Connections.Postgres.PostgresProvider(redactor: redactor, includeRawFailures: includeRawFailures),
        new DbDataKit.Connections.FlightSql.FlightSqlProvider(redactor: redactor, includeRawFailures: includeRawFailures),
    ]);

    /// <summary>
    /// Why a data source whose engine has no provider here cannot be opened: the registry knows the engine and says it is not a Gateway source (with the
    /// descriptor's note), or the engine is not one of the suite's at all.
    /// </summary>
    public static string Refusal(string dataSource, string engine)
    {
        var descriptor = EngineRegistry.Builtin.Find(engine);
        if (descriptor is null) return $"Data source '{dataSource}' uses unknown provider '{engine}'. The engines the Gateway reads are listed in docs/engine-support.md.";
        var role = descriptor.Role(EngineRoles.GatewaySource);
        return role.Status >= EngineSupport.Unverified
            ? $"Data source '{dataSource}' uses {descriptor.DisplayName} ('{engine}'), which the engine registry lists as a Gateway source, but this server has no provider for it."
            : $"Data source '{dataSource}' uses {descriptor.DisplayName} ('{engine}'), which the Gateway cannot read yet"
              + (string.IsNullOrWhiteSpace(role.Note) ? "" : ": " + role.Note.TrimEnd('.')) + ". See docs/engine-support.md for the engines it does.";
    }
}
