using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Data;

public sealed record ResolvedDataSource(string Name, DataSourceOptions Options, IQueryExecutor Executor);

/// <summary>Builds executors for the 32-bit ODBC worker; absent when the platform cannot host one.</summary>
public interface IWorkerExecutorFactory
{
    IQueryExecutor Create(string connectionString, bool readOnly = false);
}

/// <summary>
/// Maps (caller, requested data source name) to an executor: enforces per-user access, injects pass-through
/// credentials, and routes 32-bit ODBC sources through the worker process.
/// </summary>
public sealed class DataSourceCatalog(
    IOptionsMonitor<ProxyOptions> options,
    ProviderRegistry providers,
    IWorkerExecutorFactory? worker = null,
    SecretsRuntime? secrets = null)
{
    public ResolvedDataSource Resolve(ProxyPrincipal principal, string? requested)
    {
        var configured = options.CurrentValue.DataSources;
        string name = ChooseName(principal, requested, configured);

        if (!configured.TryGetValue(name, out var source))
            throw new RpcException(new Status(StatusCode.NotFound, $"Unknown data source '{name}'."));
        if (!principal.CanUse(name))
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"Not authorized for data source '{name}'."));

        if (!providers.TryGet(source.Provider, out var provider))
            throw new RpcException(new Status(StatusCode.Internal, $"Data source '{name}' uses unknown provider '{source.Provider}'."));

        // Resolved per call from the current options snapshot: nothing secret outlives the snapshot, and a rotated secret is picked up.
        string connectionString;
        try
        {
            connectionString = secrets?.ResolveConnectionString($"Proxy:DataSources:{name}:ConnectionString", source.ConnectionString, source.Provider) ?? source.ConnectionString;
        }
        catch (SecretConfigurationException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Data source '{name}' has an unusable secret reference ({ex.ConfigKey}); see the server log."));
        }

        if (source.PassThrough)
        {
            if (principal.Credentials is null)
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    $"Data source '{name}' requires database credentials; authenticate with your database username and password."));
            try
            {
                // The shared composer strips every integrated-security style key, writes the caller's login through the engine's own quoting,
                // refuses control characters and over-long values, and asserts nothing integrated remains.
                connectionString = provider.Composer.ApplyPassThroughCredentials(
                    connectionString, principal.Credentials.User, SecretValue.From(principal.Credentials.Password));
            }
            catch (ConnectionPolicyException)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "The database credentials were not accepted."));
            }
        }

        var connection = SourceConnection.Create(provider, DataSourceDefinitions.Build(name, source, connectionString), connectionString);
        try
        {
            // Validates the configuration (malformed string, ODBC policy) once, before a worker or a driver sees it.
            _ = connection.Compose();
        }
        catch (ConnectionPolicyException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Data source '{name}' has an invalid connection configuration ({ex.Rule}); see the server log."));
        }

        return new ResolvedDataSource(name, source, CreateExecutor(name, source, connection, connectionString));
    }

    private IQueryExecutor CreateExecutor(string name, DataSourceOptions source, SourceConnection connection, string connectionString)
    {
        // A 32-bit proxy process can load 32-bit drivers itself; a 64-bit one hands them to the worker.
        if (source.Bitness.Equals("x86", StringComparison.OrdinalIgnoreCase) && Environment.Is64BitProcess)
        {
            if (connection.Engine != "odbc")
                throw new RpcException(new Status(StatusCode.Internal, $"Data source '{name}': only ODBC sources can use bitness x86."));
            if (worker is null)
                throw new RpcException(new Status(StatusCode.Unavailable, "The 32-bit worker is not available on this platform."));
            return worker.Create(connectionString, connection.ReadOnly);
        }
        return new DbQueryExecutor(connection);
    }

    private static string ChooseName(ProxyPrincipal principal, string? requested, Dictionary<string, DataSourceOptions> configured)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        if (principal.DefaultDataSource is { Length: > 0 } d) return d;

        var allowed = configured.Keys.Where(principal.CanUse).ToList();
        if (allowed.Count == 1) return allowed[0];
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            "No data source selected. Send the 'x-datasource' header" +
            (allowed.Count > 0 ? $" (available: {string.Join(", ", allowed)})." : ".")));
    }
}
