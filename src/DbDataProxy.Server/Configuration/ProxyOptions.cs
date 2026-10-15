namespace DbDataProxy.Server.Configuration;

/// <summary>Root of the "Proxy" configuration section.</summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>Where generated files (self-signed certificate) live. Defaults to a "DbDataProxy" folder under the OS common application data directory.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Include (truncated) SQL text in the log. Turn off if queries may contain sensitive literals.</summary>
    public bool LogSql { get; set; } = true;

    public ListenOptions Listen { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    public CompressionOptions Compression { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public HostingOptions Hosting { get; set; } = new();

    /// <summary>Named back-end connections clients can target. Clients never send connection strings.</summary>
    public Dictionary<string, DataSourceOptions> DataSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local user accounts (hashed passwords) and the data sources they may use.</summary>
    public List<UserOptions> Users { get; set; } = [];
}

public sealed class HostingOptions
{
    /// <summary>In-flight requests (a Flight stream counts for its whole duration) before new ones are refused with 503.</summary>
    public int MaxConcurrentRequests { get; set; } = 1000;

    /// <summary>On stop, how long to let in-flight queries finish before the host shuts down.</summary>
    public int DrainSeconds { get; set; } = 30;
}

public sealed class ListenOptions
{
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8815;
}

public enum TlsMode
{
    /// <summary>Generate (once) and reuse a self-signed certificate stored on disk.</summary>
    SelfSigned,
    /// <summary>Use a PFX file.</summary>
    File,
    /// <summary>Plain HTTP/2 without TLS. Credentials travel in clear text; only for local testing.</summary>
    None,
}

public sealed class TlsOptions
{
    public TlsMode Mode { get; set; } = TlsMode.SelfSigned;

    /// <summary>PFX path for <see cref="TlsMode.File"/>; for self-signed mode, where the generated PFX is stored.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }

    /// <summary>Extra DNS names / IPs for the self-signed certificate. localhost and the machine name are always included.</summary>
    public List<string> Hostnames { get; set; } = [];

    public int SelfSignedValidityDays { get; set; } = 825;
}

public enum CompressionMode
{
    None,
    /// <summary>gRPC message compression (gzip/deflate). Works with any gRPC client that advertises support.</summary>
    Grpc,
    /// <summary>Arrow data compression (zstd/lz4): each column buffer inside the Arrow stream is compressed. Best ratio for columnar data; clients must read compressed Arrow data.</summary>
    Arrow,
}

public sealed class CompressionOptions
{
    public CompressionMode Mode { get; set; } = CompressionMode.Arrow;

    /// <summary>For <see cref="CompressionMode.Arrow"/>: "zstd" or "lz4". For <see cref="CompressionMode.Grpc"/>: "gzip" or "deflate".</summary>
    public string Codec { get; set; } = "zstd";

    /// <summary>Codec-specific level; null uses the codec default.</summary>
    public int? Level { get; set; }

    /// <summary>
    /// Lets a client override the mode per request with the "x-compression" header
    /// (values: none, gzip, deflate, zstd, lz4).
    /// </summary>
    public bool AllowClientOverride { get; set; } = true;
}

public sealed class AuthOptions
{
    /// <summary>How long an issued bearer token stays valid after its last use.</summary>
    public int SessionIdleMinutes { get; set; } = 30;

    /// <summary>Absolute lifetime of a bearer token.</summary>
    public int SessionMaxHours { get; set; } = 12;

    /// <summary>Failed logins (per user and client) within <see cref="LockoutMinutes"/> before further attempts are refused. 0 disables.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 1;

    /// <summary>
    /// Accept credentials that are not in <see cref="ProxyOptions.Users"/> and hand them to pass-through data sources,
    /// which validate them by connecting to the database as that user.
    /// </summary>
    public bool AllowPassThrough { get; set; } = true;

    /// <summary>Accept JWT bearer tokens from an OpenID Connect provider such as Microsoft Entra ID.</summary>
    public JwtOptions Jwt { get; set; } = new();
}

public sealed class WorkerOptions
{
    /// <summary>Path to the 32-bit worker executable. Defaults to "worker/DbDataProxy.Worker.exe" beside the service.</summary>
    public string? Path { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 30;
}

public sealed class DataSourceOptions
{
    /// <summary>"odbc", "sqlserver" or "duckdb".</summary>
    public string Provider { get; set; } = "odbc";
    public string ConnectionString { get; set; } = "";

    /// <summary>"x64" (in-process, the default) or "x86" (routes through the 32-bit worker; ODBC only).</summary>
    public string Bitness { get; set; } = "x64";

    /// <summary>Connect with the caller's own credentials (UID/PWD injected into the connection string).</summary>
    public bool PassThrough { get; set; }

    /// <summary>Allow statements sent as updates (INSERT/UPDATE/DDL via ExecuteUpdate). Off by default.</summary>
    public bool AllowWrites { get; set; }

    /// <summary>
    /// How catalog metadata is read: "auto" (the ODBC catalog API for ODBC sources, INFORMATION_SCHEMA otherwise),
    /// "odbc" (force the ODBC catalog API) or "informationschema" (force SQL against INFORMATION_SCHEMA).
    /// </summary>
    public string Metadata { get; set; } = "auto";

    public int BatchSize { get; set; } = 32768;
    public int? CommandTimeoutSeconds { get; set; }

    /// <summary>Overrides the global compression settings for this data source.</summary>
    public CompressionOptions? Compression { get; set; }
}

public sealed class UserOptions
{
    public string Name { get; set; } = "";

    /// <summary>How the user proves who they are.</summary>
    public UserAuthType AuthType { get; set; } = UserAuthType.Password;

    /// <summary>PBKDF2 hash as produced by "DbDataProxy hash-password" (AuthType Password).</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Data sources this user may query ("*" for all).</summary>
    public List<string> DataSources { get; set; } = [];

    /// <summary>Used when the client does not name a data source.</summary>
    public string? DefaultDataSource { get; set; }
}

public enum UserAuthType
{
    /// <summary>Password checked against <see cref="UserOptions.PasswordHash"/>.</summary>
    Password,
    /// <summary>Windows account (local or domain) checked by the operating system; Name is the account name.</summary>
    Windows,
}
