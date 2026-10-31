using DbDataKit.Config;
using DbDataKit.Serving.FlightSql;

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
    /// Write the (redacted) driver text of failed statements and connections to the server log. Clients never see it: they get the failure kind,
    /// the engine's error number or SQLSTATE and a fixed sentence. Turn off to log only kind and code.
    /// </summary>
    public bool LogDriverErrors { get; set; } = true;

    /// <summary>
    /// Version of the configuration schema this file was written for ("SchemaVersion", optional; absent means 1). Version 2 changes defaults only
    /// (the statement <c>Classifier</c> defaults to <c>strict</c> instead of <c>warn</c>); see the README. Set by the loader, not bound as a property.
    /// </summary>
    [ConfigIgnore]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// This server's identity for the consoles that manage it (plan 13, P03): made the first time <c>server pair</c> runs and written to <c>server.yaml</c>; every token a console sends names it as its audience.
    /// Not something to edit.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>The consoles and servers paired with this one, written by <c>server pair</c> and removed by <c>server unpair</c>; one line each in <c>server.yaml</c>. Not something to edit by hand.</summary>
    public List<PairedClientOptions> PairedClients { get; set; } = [];

    /// <summary>The management API a console uses (plan 13, P03): off unless asked for.</summary>
    public ManagementOptions Management { get; set; } = new();

    public ListenOptions Listen { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();

    /// <summary>Where the encrypted secret store gets its key (<c>secret:</c> references).</summary>
    public SecretsOptions Secrets { get; set; } = new();
    public CompressionOptions Compression { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public HostingOptions Hosting { get; set; } = new();

    /// <summary>Named back-end connections clients can target. Clients never send connection strings.</summary>
    public Dictionary<string, DataSourceOptions> DataSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local user accounts (hashed passwords) and the data sources they may use.</summary>
    public List<UserOptions> Users { get; set; } = [];

    /// <summary>Semantic models served as sources named <c>semantic:&lt;model&gt;</c> (plan 19, J10). Read at start; a change needs a restart.</summary>
    public SemanticOptions Semantic { get; set; } = new();

    /// <summary>Where the server keeps its state (notifications and what later slices add, such as saved queries). SQLite in the data directory unless an engine is named. Read at start; a change needs a restart.</summary>
    public ProxyStateOptions State { get; set; } = new();
}

/// <summary>The state database's engine (plan 13a, P11).</summary>
public sealed class ProxyStateOptions
{
    /// <summary><c>sqlite</c> (the default: <c>state.db</c> in the data directory), <c>mssql</c> or <c>postgres</c> (a database server you name in <see cref="ConnectionString"/>).</summary>
    public string Engine { get; set; } = "sqlite";

    /// <summary>For <c>mssql</c> and <c>postgres</c>: the connection string, with the password as a secret reference (<c>Password=secret:state-db</c>). The Proxy needs a database of its own; a console on the same server uses another.</summary>
    public string ConnectionString { get; set; } = "";
}

/// <summary>The semantic models this server answers semantic requests and semantic SQL for. Their data is read through the data sources, as the signed-in client, so a client needs access to those.</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class SemanticOptions
{
    public List<SemanticModelOptions> Models { get; set; } = [];

    /// <summary>The engine's adoption level (0-5): what a level cannot execute comes back as remaining steps. The highest is the default.</summary>
    [ConfigKey(Min = 0, Max = 5)]
    public int Level { get; set; } = 5;

    /// <summary>What one combine (the step that joins the results of several scans) may use before it is stopped and the request fails with a limit.</summary>
    public SemanticCombineOptions Combine { get; set; } = new();
}

public sealed class SemanticCombineOptions
{
    /// <summary>Memory one combine may use, in megabytes.</summary>
    [ConfigKey(Min = 16, Max = 1_048_576)]
    public int MemoryMb { get; set; } = 1024;

    /// <summary>Seconds one combine may run.</summary>
    [ConfigKey(Min = 1, Max = 86_400)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Rows, over all the scans a combine loads, above which it is refused.</summary>
    [ConfigKey(Min = 1, Max = 2_000_000_000)]
    public int MaxInputRows { get; set; } = 5_000_000;

    internal DbDataKit.Semantic.SandboxLimits ToLimits() => new(MemoryMb * 1024L * 1024L, TimeSpan.FromSeconds(TimeoutSeconds), MaxInputRows);
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class SemanticModelOptions
{
    /// <summary>The catalog file (the semantic model, <c>docs/semantic/catalog.md</c>); a relative path is relative to <c>DataDirectory</c>. The model is named by the catalog's own <c>name</c>.</summary>
    public string Catalog { get; set; } = "";

    /// <summary>For each source of the catalog, the data source of this server that holds its tables. A source not listed is looked for under its own name.</summary>
    public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// For each catalog source of kind <c>ssas</c>, the connection string of its Analysis Services server (a secret reference works for the whole string or for <c>Password=</c>). Windows only. The connection is this server's, not the caller's:
    /// whoever may query the model reads what that account can read.
    /// </summary>
    public Dictionary<string, string> Ssas { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A client paired with this server, as <c>server.yaml</c> keeps it (see <c>PairedClientsFile</c>).</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class PairedClientOptions
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? PublicKey { get; set; }
    public List<string> Ceiling { get; set; } = [];
    public string? Paired { get; set; }
}

/// <summary>
/// The management API (plan 13, P03): HTTPS on a port of its own, never the Flight port, with the pairing every product has and the console's signed token on every call; and, if asked for, a local channel for the
/// command line on this machine. Off unless <see cref="Enabled"/>: a proxy nobody manages from a console opens nothing new.
/// </summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class ManagementOptions
{
    public bool Enabled { get; set; }

    /// <summary>Where it listens. The loopback address by default: a console on another machine needs an address that is reachable, chosen on purpose.</summary>
    public string Address { get; set; } = "127.0.0.1";

    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 8816;

    /// <summary>The local channel for <c>dbdataproxy config show</c> and the like on this machine: a named pipe on Windows and a Unix socket elsewhere, reachable only by the service's account and administrators. Off by default.</summary>
    public bool LocalChannel { get; set; }

    /// <summary>Unix only: the folder that holds the local channel's socket. Empty: the platform's runtime folder for this account.</summary>
    public string? LocalChannelDirectory { get; set; }
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class HostingOptions
{
    /// <summary>In-flight requests (a Flight stream counts for its whole duration) before new ones are refused with 503.</summary>
    public int MaxConcurrentRequests { get; set; } = 1000;

    /// <summary>On stop, how long to let in-flight queries finish before the host shuts down.</summary>
    public int DrainSeconds { get; set; } = 30;
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
    /// <summary>Use a certificate from the certificate store (Windows: <c>LocalMachine\My</c> by default), found by thumbprint or subject.</summary>
    Store,
    /// <summary>Plain HTTP/2 without TLS. Credentials travel in clear text; only for local testing.</summary>
    None,
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class SecretsOptions
{
    /// <summary>
    /// A key file the platform provides (a Docker or Kubernetes secret, a systemd credential): 32 raw bytes, or the key as base64 or hex text. Empty (the default) keeps the key in
    /// <c>&lt;DataDirectory&gt;/secrets/keys</c>, protected by DPAPI on Windows and by file permissions elsewhere. The environment variable <c>DBDATA_SECRETS_KEY_FILE</c> does the same.
    /// </summary>
    public string? KeyFile { get; set; }
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

    /// <summary>For <see cref="TlsMode.Store"/>: thumbprint of the certificate to use (takes precedence over <see cref="CertificateSubject"/>).</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>For <see cref="TlsMode.Store"/>: subject name to look for; when several match, the one valid for longest wins, so a renewed certificate is picked up.</summary>
    public string? CertificateSubject { get; set; }

    /// <summary>For <see cref="TlsMode.Store"/>: which store location to read.</summary>
    public StoreLocationOption StoreLocation { get; set; } = StoreLocationOption.LocalMachine;
}

public enum StoreLocationOption
{
    LocalMachine,
    CurrentUser,
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
    /// <summary>Path to the 32-bit worker executable. Defaults to "worker/dbdataproxy-worker.exe" beside the service.</summary>
    public string? Path { get; set; }
    [ConfigKey(Min = 1, Max = 3600)]
    public int StartupTimeoutSeconds { get; set; } = 30;
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class DataSourceOptions
{
    /// <summary>"odbc", "sqlserver", "duckdb", "postgres" or "flightsql".</summary>
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

    /// <summary>
    /// DuckDB sources without <see cref="AllowWrites"/> run in a sandbox (read-only file, no external file access, no extensions, locked settings).
    /// Directories listed here are the one exception: queries may read files below them (for example a folder of Parquet files the source
    /// deliberately serves). Absolute paths only; everything else stays blocked. No effect on a source with <see cref="AllowWrites"/>, which is not sandboxed.
    /// </summary>
    public List<string> AllowedDirectories { get; set; } = [];

    /// <summary>
    /// Most queries running at once on this data source (a streaming result counts until it has been read or discarded); further requests wait up to
    /// <see cref="AcquireTimeoutSeconds"/> and are then refused with ResourceExhausted. 0 (default) means no limit.
    /// </summary>
    [ConfigKey(Min = 0, Max = 100000)]
    public int MaxConcurrentQueries { get; set; }

    /// <summary>Most queries one user may have running at once on this data source. 0 (default) means no limit.</summary>
    [ConfigKey(Min = 0, Max = 100000)]
    public int MaxConcurrentQueriesPerUser { get; set; }

    /// <summary>How long a request waits for a free slot when a limit is set.</summary>
    [ConfigKey(Min = 1, Max = 3600)]
    public int AcquireTimeoutSeconds { get; set; } = 30;

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

    /// <summary>PBKDF2 hash as produced by "dbdataproxy secret hash" (AuthType Password).</summary>
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
