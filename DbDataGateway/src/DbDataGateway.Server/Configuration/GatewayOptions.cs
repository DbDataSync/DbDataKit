using DbDataKit.Config;
using DbDataKit.Serving.FlightSql;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// Root of the "Gateway" configuration section. Keys are PascalCase and matched case-insensitively (like the stock configuration binder);
/// unknown keys are startup errors (see <see cref="GatewayConfig"/>).
/// </summary>
[ConfigSchema(GatewayConfig.Kind, Version = GatewayConfig.LatestSchemaVersion, Naming = KeyNaming.Verbatim)]
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Where generated files (self-signed certificate) live. Defaults to a "DbDataGateway" folder under the OS common application data directory.</summary>
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

    /// <summary>
    /// The modules this instance serves (plan 20, O01): <c>flightsql</c> (Arrow Flight SQL, the default) and <c>identity</c>; an unknown name is refused naming this file. <c>Identity.Enabled</c> also turns the identity module on.
    /// An instance with no <c>flightsql</c> opens no Flight port.
    /// </summary>
    public List<string> Modules { get; set; } = [GatewayModules.FlightSql];

    /// <summary>The identity home (plan 20, O06): who people are and what they may do, for every console and app of the install, on a listener of its own. Off unless asked for.</summary>
    public IdentityOptions Identity { get; set; } = new();

    /// <summary>The apps host (plan 26, AP05): published apps served by subpath on a listener of its own. Off unless <c>Modules</c> lists <c>apps</c>.</summary>
    public AppsOptions Apps { get; set; } = new();

    public StoreModuleOptions Store { get; set; } = new();

    public ViewsModuleOptions Views { get; set; } = new();

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

    /// <summary>Semantic models served as sources named <c>semantic:&lt;model&gt;</c> (plan 19, J10). Read at start; a change needs a restart.</summary>
    public SemanticOptions Semantic { get; set; } = new();

    /// <summary>Where the server keeps its state (notifications and what later slices add, such as saved queries). SQLite in the data directory unless an engine is named. Read at start; a change needs a restart.</summary>
    public GatewayStateOptions State { get; set; } = new();

    /// <summary>How often, in seconds, the instance looks for definitions published to its group (plan 20 O03): a publish through another instance is served here this long after at most. (It was <c>state.definitionPollSeconds</c> until the state section became the one every DbData product shares.)</summary>
    [ConfigKey(Min = 1, Max = 300)]
    public int DefinitionPollSeconds { get; set; } = 5;
}

/// <summary>
/// The <c>state:</c> section: the one every DbData product shares (plan 29b C07), read through <c>StateSettingsReader</c> by <see cref="GatewayStateConfig"/>. The Gateway's own layers (file, environment, command line) decide which value wins.
/// <code>
/// state:
///   engine: postgres
///   connection: Host=db;Database=dbdatagateway_state;Username=gateway;Password=secret:state-db
///   namespaces: { default: { schema: dbdatagateway } }
///   retention: { days: 30 }
/// </code>
/// </summary>
public sealed class GatewayStateOptions
{
    /// <summary><c>sqlite</c> (the default: <c>state.db</c> in the data directory), <c>mssql</c> or <c>postgres</c> (a database server you name in <see cref="Connection"/>).</summary>
    public string Engine { get; set; } = "sqlite";

    /// <summary>For <c>mssql</c> and <c>postgres</c>: the connection string, with the password as a secret reference (<c>Password=secret:state-db</c>). The Gateway needs a database of its own; a console on the same server uses another.</summary>
    public string Connection { get; set; } = "";

    /// <summary>Where the registry table is (<c>schema</c>, <c>prefix</c>, <c>file</c>).</summary>
    public GatewayStateNamespace Registry { get; set; } = new();

    /// <summary>Where each module's tables are: <c>default</c> for every module without an entry of its own, then one entry per module.</summary>
    public Dictionary<string, GatewayStateNamespace> Namespaces { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How long history is kept: <c>days</c>, and per module or table.</summary>
    public GatewayStateRetention Retention { get; set; } = new();

    /// <summary>The settings these options say, read through the shared reader (a setting it refuses raises <see cref="InvalidOperationException"/>).</summary>
    public DbDataKit.State.StateSettings ToSettings()
    {
        var section = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Engine)) section["engine"] = Engine;
        if (!string.IsNullOrWhiteSpace(Connection)) section["connection"] = Connection;
        if (Registry.ToMap() is { } registry) section["registry"] = registry;
        if (Namespaces.Count > 0) section["namespaces"] = Namespaces.ToDictionary(p => p.Key, p => (object?)(p.Value?.ToMap() ?? []), StringComparer.Ordinal);
        if (Retention.ToMap() is { } retention) section["retention"] = retention;
        return DbDataKit.State.StateSettingsReader.Read(section.Count == 0 ? null : section, "state");
    }
}

/// <summary>Where one module's tables are: a schema, a table-name prefix, or (SQLite) a file of its own.</summary>
public sealed class GatewayStateNamespace
{
    public string? Schema { get; set; }

    public string? Prefix { get; set; }

    public string? File { get; set; }

    internal Dictionary<string, object?>? ToMap()
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Schema is not null) map["schema"] = Schema;
        if (Prefix is not null) map["prefix"] = Prefix;
        if (File is not null) map["file"] = File;
        return map.Count == 0 ? null : map;
    }
}

/// <summary>How long history is kept (a number of days above zero, or <c>forever</c>).</summary>
public sealed class GatewayStateRetention
{
    public string? Days { get; set; }

    public Dictionary<string, string> Modules { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Tables { get; set; } = new(StringComparer.Ordinal);

    internal Dictionary<string, object?>? ToMap()
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Days is not null) map["days"] = Days;
        if (Modules.Count > 0) map["modules"] = Modules.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
        if (Tables.Count > 0) map["tables"] = Tables.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
        return map.Count == 0 ? null : map;
    }
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
/// command line on this machine. Off unless <see cref="Enabled"/>: a gateway nobody manages from a console opens nothing new.
/// </summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class ManagementOptions
{
    public bool Enabled { get; set; }

    /// <summary>Where it listens. The loopback address by default: a console on another machine needs an address that is reachable, chosen on purpose.</summary>
    public string Address { get; set; } = "127.0.0.1";

    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 8816;

    /// <summary>The local channel for <c>dbdatagateway config show</c> and the like on this machine: a named pipe on Windows and a Unix socket elsewhere, reachable only by the service's account and administrators. Off by default.</summary>
    public bool LocalChannel { get; set; }

    /// <summary>Unix only: the folder that holds the local channel's socket. Empty: the platform's runtime folder for this account.</summary>
    public string? LocalChannelDirectory { get; set; }
}

/// <summary>
/// The identity home as a module of the serving layer (plan 20, section 6 and O06): the OpenID Connect provider every console and app signs people in through, the passkey sign-in and invitations, on an HTTPS
/// listener of its own (browsers reach it, so HTTP/1.1 as well as HTTP/2). Its people, roles, clients and signing keys are in the state database, so every instance of a group that shares it is the same home;
/// the signing keys are protected with the secret <c>dbdata:gateway:identity:signing-keys</c>, made on the first start and copied to every other instance of the group.
/// </summary>
/// <summary>The <c>apps</c> module (plan 26, AP05): where it listens. Its pages are https only.</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class AppsOptions
{
    /// <summary>Where it listens: every address by default, as the identity home, since end users open apps here.</summary>
    public string Address { get; set; } = "0.0.0.0";

    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 8818;

    /// <summary>The https address people reach the apps host at, without a path. Empty: <c>https://&lt;the first TLS host name, or localhost&gt;:&lt;Port&gt;</c>. The home's redirect URI for it is this plus <c>/apps/-/callback</c>.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>How old, in seconds, the identity home's last answer about a person may be before the host asks again: a role taken away at the home stops working here within this time.</summary>
    [ConfigKey(Min = 1, Max = 3600)]
    public int RolesRefreshSeconds { get; set; } = 300;

    /// <summary>The identity home people sign in through. A client registered at the home (<c>identity client-add</c>) with the redirect URI above; a public client needs no secret.</summary>
    public AppsHomeOptions Home { get; set; } = new();

    /// <summary>
    /// The app build's allow-list (plan 22a GP33, QG12; <c>apps.allowedPackages</c> in <c>server.yaml</c>; plan 22a GP33 names it <c>apps.allowed_packages</c>, the file's keys are camel case): packages, besides the default library set, that an app's source may import. A package must also be in the package store, which holds
    /// what an administrator vendored; a source that imports any other package is refused by name. Names are npm names (<c>left-pad</c>, <c>@scope/name</c>).
    /// </summary>
    public List<string> AllowedPackages { get; set; } = [];

    /// <summary>The vendored package store the app build uses (<c>apps.packageStore</c>): a folder made by <c>DbDataKit/web/app-store/build.mjs</c> (<c>store.json</c> and <c>node_modules</c>). When it is set, <c>apps build</c> builds from it with no network and installs nothing; without it a build installs the app's packages from a registry (a developer's machine).</summary>
    public string? PackageStore { get; set; }

    /// <summary>The SHA-256 of the store's <c>store.json</c> (<c>apps.packageStoreSha256</c>), so that a replaced manifest is refused too; empty: the manifest is trusted as the file system keeps it. <c>apps store-verify</c> prints it.</summary>
    [ConfigKey(Pattern = "[0-9a-fA-F]{64}")]
    public string? PackageStoreSha256 { get; set; }
}

/// <summary>The <c>store</c> module (plan 20 O08): where it listens. Master data is read and written here, as the identity home's people and service accounts, through the models a DbDataStore server published to the group.</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class StoreModuleOptions
{
    /// <summary>Where it listens: every address by default, since apps and agents call it.</summary>
    public string Address { get; set; } = "0.0.0.0";

    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 8819;
}

/// <summary>The <c>views</c> module (plan 32, DBR11b): where it listens. Published dashboards and reports are read and rendered here, as the identity home's people, through the models published to the group.</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class ViewsModuleOptions
{
    /// <summary>Where it listens: every address by default, since people, apps and agents call it.</summary>
    public string Address { get; set; } = "0.0.0.0";

    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 8820;

    /// <summary>How long, in seconds, the rows of a request are kept for the viewer who asked (their access and the request are the key). 0 turns the cache off.</summary>
    [ConfigKey(Min = 0, Max = 3600)]
    public int CacheSeconds { get; set; } = 30;

    /// <summary>The most rows a CSV or xlsx export holds in all (plan 32 DBR17, <c>ViewsOptions.MaxExportRows</c>): a larger one is refused with 413 in words. Not a browser's limit, so it is not part of <c>Export</c>.</summary>
    [ConfigKey(Min = 1, Max = 10_000_000)]
    public int MaxExportRows { get; set; } = 100_000;

    /// <summary>PDF export (plan 32 DBR15, <c>views.export</c>): off unless <c>Mode</c> is set; the module's health says "export unavailable" otherwise and screens keep working.</summary>
    public ViewsExportOptions Export { get; set; } = new();
}

/// <summary>The <c>views.export</c> section (plan 32 DBR15, DBR16): which browser draws the PDF and the limits. Set <c>Mode</c> to turn export on.</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class ViewsExportOptions
{
    /// <summary><c>pinned</c> (the Chrome for Testing build <c>chromium.pin</c> names, fetched by <c>scripts/fetch-chromium</c>; a browser of another version is refused) or <c>system</c> (an installed Chrome or Edge at <c>Path</c>, its version recorded). Empty: export is off.</summary>
    public string? Mode { get; set; }

    /// <summary>The browser's executable. Pinned mode finds <c>.chromium/&lt;rid&gt;</c> next to <c>chromium.pin</c> when it is left out; system mode needs it.</summary>
    public string? Path { get; set; }

    /// <summary>The <c>chromium.pin</c> a pinned host reads its version from; default: the nearest one above the program.</summary>
    public string? Pin { get; set; }

    /// <summary>The folder with <c>print.js</c> and <c>print.css</c>, the visuals' print page (<c>npm run build:print -w @dbdatakit/visuals</c>).</summary>
    public string? PageAssets { get; set; }

    /// <summary>One export, browser start to PDF.</summary>
    [ConfigKey(Min = 1, Max = 3600)]
    public int DeadlineSeconds { get; set; } = 60;

    [ConfigKey(Min = 1, Max = 5000)]
    public int MaxPages { get; set; } = 500;

    /// <summary>Exports at once on this host.</summary>
    [ConfigKey(Min = 1, Max = 64)]
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Starts the browser without its sandbox: only where a container forces it (it is logged).</summary>
    public bool NoSandbox { get; set; }

    /// <summary>The bundled fonts (<c>.fonts</c> of <c>scripts/fetch-fonts</c>) and the host fonts an administrator lists.</summary>
    public ViewsExportFontOptions Fonts { get; set; } = new();
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class ViewsExportFontOptions
{
    /// <summary>The bundled set's folder; default: <c>.fonts</c> next to <c>fonts.pin</c>.</summary>
    public string? Directory { get; set; }

    public string? Pin { get; set; }

    /// <summary>Host fonts a document may name.</summary>
    public List<string> Allowed { get; set; } = [];

    /// <summary>Host fonts the host promises: one that is missing stops export, naming it.</summary>
    public List<string> Required { get; set; } = [];
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class AppsHomeOptions
{
    /// <summary>The home's address. Empty: this instance's own identity module, when it serves one.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>A secret reference, for a confidential client; empty for a public one (PKCE).</summary>
    public string? ClientSecret { get; set; }
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class IdentityOptions
{
    public bool Enabled { get; set; }

    /// <summary>Where it listens: every address by default, as the Flight listener, since end users sign in here.</summary>
    public string Address { get; set; } = "0.0.0.0";

    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 8817;

    /// <summary>
    /// The https address every console, app and browser reaches the home at (a balancer's name for a group), without a path. It is the token issuer and the origin passkeys are bound to, so it must not change once
    /// people have signed in. Empty: <c>https://&lt;the first TLS host name, or localhost&gt;:&lt;Port&gt;</c>.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>The longest a person's personal access token may last here, in days (plan 13a U20): at most 365, and a token lasts 90 days, or this when it is lower, unless the person asks for less.</summary>
    [ConfigKey(Min = 1, Max = 365)]
    public int AccessTokenMaxDays { get; set; } = 365;

    /// <summary>Sign people in at the home through a Microsoft Entra ID tenant too (plan 20 O06b). Off while <see cref="IdentityEntraOptions.Authority"/> is empty.</summary>
    public IdentityEntraOptions Entra { get; set; } = new();

    /// <summary>Sign people in at the home with their Windows account too (Negotiate; on Linux, Kerberos with a keytab). Off unless asked for.</summary>
    public IdentityWindowsOptions Windows { get; set; } = new();
}

/// <summary>
/// The home as a client of an Entra tenant: an app registered there for it, with the redirect URI <c>&lt;PublicUrl&gt;/identity/upstream/entra/callback</c> and the <c>groups</c> claim in its id token. The groups map to roles with
/// <c>dbdatagateway identity map-group --source entra</c>.
/// </summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class IdentityEntraOptions
{
    /// <summary>The tenant's issuer: <c>https://login.microsoftonline.com/&lt;tenant id&gt;/v2.0</c>. Empty: Entra is not offered.</summary>
    public string? Authority { get; set; }

    /// <summary>The id of the app registered in the tenant for the home.</summary>
    public string? ClientId { get; set; }

    /// <summary>That app's client secret, as a secret reference (<c>secret:dbdata:gateway:identity:entra</c>).</summary>
    [Secret]
    public string? ClientSecret { get; set; }

    /// <summary>Whether the tenant's <c>email</c> claim says whose an address is, so a person's first Entra sign-in joins the account that holds their address. Only for a tenant whose addresses its administrators set.</summary>
    public bool TrustEmail { get; set; }
}

/// <summary>The home's Windows sign-in: the account's SID is the credential, its user principal name the account key, its groups what <c>dbdatagateway identity map-group --source windows</c> mappings match.</summary>
[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class IdentityWindowsOptions
{
    public bool Enabled { get; set; }
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
    /// <c>&lt;DataDirectory&gt;/secrets/keys</c>, protected by DPAPI on Windows and by file permissions elsewhere. The environment variable <c>DBDATAKIT_SECRETS_KEY_FILE</c> does the same.
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
    /// Accept credentials that are not the identity home's (a personal access token) and hand them to pass-through data sources,
    /// which validate them by connecting to the database as that user.
    /// </summary>
    public bool AllowPassThrough { get; set; } = true;

    /// <summary>
    /// Let a person sign in with their Windows account (plan 13a P13, U16): the user name is their UPN (<c>ada@corp.example</c>), the password is checked by the operating system (Windows only), and the account is the identity home's user who holds that
    /// verified address. Off by default; a UPN no home user holds is left to pass-through.
    /// </summary>
    public bool Windows { get; set; }

    /// <summary>Accept JWT bearer tokens from an OpenID Connect provider such as Microsoft Entra ID.</summary>
    public JwtOptions Jwt { get; set; } = new();
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class WorkerOptions
{
    /// <summary>Path to the 32-bit worker executable. Defaults to "worker/dbdatagateway-worker.exe" beside the service.</summary>
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

    /// <summary>
    /// The identity home's groups whose members may use this data source (<c>extensions.gateway.groups</c> in its file; plan 13a P13). The home's groups of the person decide, case does not matter. A data source that names no group and is not
    /// <see cref="Everyone"/> is used by no home user (the safe default); a pass-through source needs none, the database decides there.
    /// </summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>The data source a home user who may use it gets when the client names none (<c>extensions.gateway.default: true</c>; at most one). It replaces the <c>DefaultDataSource</c> of the retired Gateway users; it opens nothing by itself.</summary>
    public bool Default { get; set; }

    /// <summary>Every user of the identity home may use this data source, whatever their groups (<c>extensions.gateway.everyone: true</c>). Say it explicitly: leaving the groups out opens nothing.</summary>
    public bool Everyone { get; set; }

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
