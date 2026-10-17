namespace DbDataProxy.Server.Auth;

/// <summary>Database credentials supplied by the caller, used for pass-through data sources.</summary>
public sealed record DbCredentials(string User, string Password);

/// <summary>An authenticated caller and what it may do.</summary>
public sealed class ProxyPrincipal
{
    private readonly HashSet<string> _dataSources;

    public ProxyPrincipal(string name, string method, IEnumerable<string> dataSources, string? defaultDataSource = null, DbCredentials? credentials = null)
    {
        Name = name;
        Method = method;
        _dataSources = new HashSet<string>(dataSources, StringComparer.OrdinalIgnoreCase);
        DefaultDataSource = defaultDataSource;
        Credentials = credentials;
    }

    public string Name { get; }

    /// <summary>How the caller proved their identity ("password", "passthrough", "bearer", ...).</summary>
    public string Method { get; }

    public string? DefaultDataSource { get; }
    public DbCredentials? Credentials { get; }
    public IReadOnlyCollection<string> DataSources => _dataSources;

    public bool CanUse(string dataSource) => _dataSources.Contains("*") || _dataSources.Contains(dataSource);
}
