namespace DbDataGateway.Server.Auth;

/// <summary>Database credentials supplied by the caller, used for pass-through data sources.</summary>
public sealed record DbCredentials(string User, string Password);

/// <summary>An authenticated caller and what it may do.</summary>
public sealed class GatewayPrincipal
{
    private readonly HashSet<string> _dataSources;

    public GatewayPrincipal(string name, string method, IEnumerable<string> dataSources, string? defaultDataSource = null, DbCredentials? credentials = null,
        string? homeUserId = null, IEnumerable<string>? homeGroups = null)
    {
        Name = name;
        Method = method;
        _dataSources = new HashSet<string>(dataSources, StringComparer.OrdinalIgnoreCase);
        DefaultDataSource = defaultDataSource;
        Credentials = credentials;
        HomeUserId = homeUserId;
        HomeGroups = homeGroups is null ? [] : [.. homeGroups];
    }

    public string Name { get; }

    /// <summary>How the caller proved their identity ("home-token", "home-user", "windows", "passthrough", "jwt").</summary>
    public string Method { get; }

    public string? DefaultDataSource { get; }
    public DbCredentials? Credentials { get; }
    public IReadOnlyCollection<string> DataSources => _dataSources;

    /// <summary>The identity home's user id when the caller is a person or service account of the home (a home token, a personal access token or a Windows sign-in resolved to one); null for pass-through and external JWT callers.</summary>
    public string? HomeUserId { get; }

    /// <summary>The home groups the person was in when they signed in: which data sources a home user may use is decided by the groups the data source files name.</summary>
    public IReadOnlyList<string> HomeGroups { get; }

    public bool CanUse(string dataSource) => _dataSources.Contains("*") || _dataSources.Contains(dataSource);
}
