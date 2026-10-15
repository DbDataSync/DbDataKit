using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Data;

public sealed record ResolvedDataSource(string Name, DataSourceOptions Options, IQueryExecutor Executor);

/// <summary>Builds executors for the 32-bit ODBC worker; absent when the platform cannot host one.</summary>
public interface IWorkerExecutorFactory
{
    IQueryExecutor Create(string connectionString);
}

/// <summary>
/// Maps (caller, requested data source name) to an executor: enforces per-user access, injects pass-through
/// credentials, and routes 32-bit ODBC sources through the worker process.
/// </summary>
public sealed class DataSourceCatalog(
    IOptionsMonitor<ProxyOptions> options,
    IEnumerable<IDbProvider> providers,
    IWorkerExecutorFactory? worker = null)
{
    private readonly Dictionary<string, IDbProvider> _providers =
        providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public ResolvedDataSource Resolve(ProxyPrincipal principal, string? requested)
    {
        var configured = options.CurrentValue.DataSources;
        string name = ChooseName(principal, requested, configured);

        if (!configured.TryGetValue(name, out var source))
            throw new RpcException(new Status(StatusCode.NotFound, $"Unknown data source '{name}'."));
        if (!principal.CanUse(name))
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"Not authorized for data source '{name}'."));

        string connectionString = source.ConnectionString;
        if (!_providers.TryGetValue(source.Provider, out var provider))
            throw new RpcException(new Status(StatusCode.Internal, $"Data source '{name}' uses unknown provider '{source.Provider}'."));

        if (source.PassThrough)
        {
            if (principal.Credentials is null)
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    $"Data source '{name}' requires database credentials; authenticate with your database username and password."));
            connectionString = provider.ApplyCredentials(connectionString, principal.Credentials.User, principal.Credentials.Password);
        }

        return new ResolvedDataSource(name, source, CreateExecutor(name, source, provider, connectionString));
    }

    private IQueryExecutor CreateExecutor(string name, DataSourceOptions source, IDbProvider provider, string connectionString)
    {
        // A 32-bit proxy process can load 32-bit drivers itself; a 64-bit one hands them to the worker.
        if (source.Bitness.Equals("x86", StringComparison.OrdinalIgnoreCase) && Environment.Is64BitProcess)
        {
            if (!provider.Name.Equals("odbc", StringComparison.OrdinalIgnoreCase))
                throw new RpcException(new Status(StatusCode.Internal, $"Data source '{name}': only ODBC sources can use bitness x86."));
            if (worker is null)
                throw new RpcException(new Status(StatusCode.Unavailable, "The 32-bit worker is not available on this platform."));
            return worker.Create(connectionString);
        }
        return new DbQueryExecutor(new DelegateConnectionFactory(provider.CreateConnection), connectionString, provider.ParameterStyle, provider.Dialect);
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
