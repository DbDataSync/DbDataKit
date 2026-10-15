using DbDataKit.Config;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// Root of the "Proxy" configuration section. Keys are PascalCase and matched case-insensitively (like the stock configuration binder);
/// unknown keys are startup errors (see <see cref="ProxyConfig"/>).
/// </summary>
[ConfigSchema(ProxyConfig.Kind, Version = ProxyConfig.LatestSchemaVersion, Naming = KeyNaming.Verbatim)]
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>Where generated files (self-signed certificate) live. Defaults to a "DbDataProxy" folder under the OS common application data directory.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Include (truncated) SQL text in the log. Turn off if queries may contain sensitive literals.</summary>
    public bool LogSql { get; set; } = true;

    /// <summary>
    /// Refuse to start (and refuse the data source or token key at run time) when a connection-string password,
    /// <c>Tls:CertificatePassword</c> or <c>Auth:Jwt:SigningKey</c> is a literal instead of a secret reference
    /// (<c>secret:</c>, <c>env:</c>, <c>file:</c>, <c>dpapi:</c>). Off by default: literals still work and log a one-time warning naming the key.
    /// </summary>
    public bool RequireReferencedSecrets { get; set; }

    /// <summary>
    /// Forward-compatibility switch: unknown keys become warnings instead of startup errors (for example while rolling a config file back
    /// to an older release). Off by default: a misspelt key is an error with a "did you mean" suggestion.
    /// </summary>
    public bool AllowUnknownKeys { get; set; }

    /// <summary>
    /// Version of the configuration schema this file was written for ("SchemaVersion", optional; absent means 1). Version 2 changes defaults only
    /// (the statement <c>Classifier</c> defaults to <c>strict</c> instead of <c>warn</c>); see the README. Set by the loader, not bound as a property.
    /// </summary>
    [ConfigIgnore]
    public int SchemaVersion { get; set; } = 1;

    public ListenOptions Listen { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    public CompressionOptions Compression { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();

    /// <summary>Named back-end connections clients can target. Clients never send connection strings.</summary>
    public Dictionary<string, DataSourceOptions> DataSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local user accounts (hashed passwords) and the data sources they may use.</summary>
    public List<UserOptions> Users { get; set; } = [];
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class ListenOptions
{
    public string Address { get; set; } = "0.0.0.0";
    [ConfigKey(Min = 0, Max = 65535)]
    /// <summary>TCP port, 1-65535; 0 lets the operating system pick one (tests).</summary>
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

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class TlsOptions
{
    public TlsMode Mode { get; set; } = TlsMode.SelfSigned;

    /// <summary>PFX path for <see cref="TlsMode.File"/>; for self-signed mode, where the generated PFX is stored.</summary>
    public string? CertificatePath { get; set; }
    [Secret]
    public string? CertificatePassword { get; set; }

    /// <summary>Extra DNS names / IPs for the self-signed certificate. localhost and the machine name are always included.</summary>
    public List<string> Hostnames { get; set; } = [];

    [ConfigKey(Min = 1)]
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

[ConfigSchema(Naming = KeyNaming.Verbatim)]
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

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class AuthOptions
{
    /// <summary>How long an issued bearer token stays valid after its last use.</summary>
    [ConfigKey(Min = 1, Max = 525600)]
    public int SessionIdleMinutes { get; set; } = 30;

    /// <summary>Absolute lifetime of a bearer token.</summary>
    [ConfigKey(Min = 1, Max = 8760)]
    public int SessionMaxHours { get; set; } = 12;

    /// <summary>Failed logins (per user and client) within <see cref="LockoutMinutes"/> before further attempts are refused. 0 disables.</summary>
    [ConfigKey(Min = 0, Max = 1000)]
    public int MaxFailedLogins { get; set; } = 5;

    [ConfigKey(Min = 0, Max = 10080)]
    public int LockoutMinutes { get; set; } = 1;

    /// <summary>
    /// Accept credentials that are not in <see cref="ProxyOptions.Users"/> and hand them to pass-through data sources,
    /// which validate them by connecting to the database as that user.
    /// </summary>
    public bool AllowPassThrough { get; set; } = true;

    /// <summary>Accept JWT bearer tokens from an OpenID Connect provider such as Microsoft Entra ID.</summary>
    public JwtOptions Jwt { get; set; } = new();
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class WorkerOptions
{
    /// <summary>Path to the 32-bit worker executable. Defaults to "worker/DbDataProxy.Worker.exe" beside the service.</summary>
    public string? Path { get; set; }
    [ConfigKey(Min = 1, Max = 3600)]
    public int StartupTimeoutSeconds { get; set; } = 30;
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class DataSourceOptions
{
    /// <summary>"odbc", "sqlserver" or "duckdb".</summary>
    public string Provider { get; set; } = "odbc";
    // Not [Secret]: `config show` masks only the credential parts of a connection string (references stay visible).
    public string ConnectionString { get; set; } = "";

    /// <summary>"x64" (in-process, the default) or "x86" (routes through the 32-bit worker; ODBC only).</summary>
    public string Bitness { get; set; } = "x64";

    /// <summary>Connect with the caller's own credentials (UID/PWD injected into the connection string).</summary>
    public bool PassThrough { get; set; }

    /// <summary>Allow statements sent as updates (INSERT/UPDATE/DDL via ExecuteUpdate). Off by default.</summary>
    public bool AllowWrites { get; set; }

    /// <summary>
    /// What to do with statement text on a read-only source (AllowWrites=false) that is not a single plain SELECT or that uses a
    /// deny-listed function or statement (OPENROWSET, xp_*, read_csv, COPY, ATTACH, ...): "warn" (default) logs the would-be refusal,
    /// "strict" refuses it with PermissionDenied, "off" skips the check. Defence in depth only; use a read-only database login.
    /// </summary>
    public ClassifierMode Classifier { get; set; } = ClassifierMode.Warn;

    /// <summary>
    /// How catalog metadata is read: "auto" (the ODBC catalog API for ODBC sources, INFORMATION_SCHEMA otherwise),
    /// "odbc" (force the ODBC catalog API) or "informationschema" (force SQL against INFORMATION_SCHEMA).
    /// </summary>
    public string Metadata { get; set; } = "auto";

    [ConfigKey(Min = 1, Max = 1000000)]
    public int BatchSize { get; set; } = 32768;
    [ConfigKey(Min = 0, Max = 86400)]
    public int? CommandTimeoutSeconds { get; set; }

    /// <summary>Overrides the global compression settings for this data source.</summary>
    public CompressionOptions? Compression { get; set; }
}

/// <summary>How <see cref="DataSourceOptions.Classifier"/> treats statement text on read-only sources.</summary>
public enum ClassifierMode
{
    /// <summary>Log a warning for statements that would be refused, and run them.</summary>
    Warn,

    /// <summary>Refuse statements that are not a single SELECT or that use deny-listed words.</summary>
    Strict,

    /// <summary>No statement text checks.</summary>
    Off,
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class UserOptions
{
    public string Name { get; set; } = "";

    /// <summary>How the user proves who they are.</summary>
    public UserAuthType AuthType { get; set; } = UserAuthType.Password;

    /// <summary>PBKDF2 hash as produced by "DbDataProxy hash-password" (AuthType Password).</summary>
    [Secret]
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
