using DbDataKit.Config;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections;

/// <summary>Whether a connection may write. A <see cref="Readonly"/> connection forbids a write login (Build DDB-455).</summary>
public enum AccessMode { Readwrite, Readonly }

/// <summary>How the connection authenticates.</summary>
public enum AuthMode { Sql, Integrated, None, Token }

/// <summary>Which login a caller wants. <see cref="Read"/> never falls back to the write login.</summary>
public enum LoginRole { Default, Read, Write }

/// <summary>Host/port/database form of an address. Mutually exclusive with a connection string.</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class AddressSpec
{
    public string? Host { get; set; }
    [ConfigKey(Min = 1, Max = 65535)]
    public int? Port { get; set; }
    public string? Database { get; set; }
}

/// <summary>One login: a user plus a secret reference, a whole-string secret, or a token secret. Never a literal password.</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class LoginSpec
{
    public AuthMode Mode { get; set; } = AuthMode.Sql;
    public string? User { get; set; }
    public string? PasswordSecret { get; set; }
    public string? ConnectionStringSecret { get; set; }
    public string? TokenSecret { get; set; }
}

/// <summary>TLS: encrypt and verify by default. Opting out is explicit and per connection.</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class TlsSpec
{
    public bool Encrypt { get; set; } = true;
    public bool Verify { get; set; } = true;
    public string? CaFile { get; set; }
    public bool TrustServerCertificate { get; set; }

    /// <summary>True when certificate validation is on: <see cref="Verify"/> and not <see cref="TrustServerCertificate"/>.</summary>
    [ConfigIgnore]
    public bool EffectiveVerify => Verify && !TrustServerCertificate;
}

/// <summary>Timeouts in seconds. Never null: an unset timeout is the default, not "forever" (Gateway gap).</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class TimeoutSpec
{
    [ConfigKey(Min = 1, Max = 86400)]
    public int ConnectS { get; set; } = 15;
    [ConfigKey(Min = 1, Max = 86400)]
    public int CommandS { get; set; } = 300;
}

/// <summary>Per-product settings (<c>extensions.sync</c>, <c>extensions.gateway</c>, ...), kept as nodes so each product binds its own part.</summary>
[ConfigSchema(UnknownKeys = UnknownKeyPolicy.Preserve)]
public sealed class ExtensionBag
{
    [ConfigExtra]
    public Dictionary<string, YamlNode> Items { get; set; } = [];
}

/// <summary>
/// The <c>cache:</c> section of a connection definition used as a serving data source (plan 24a UC03; <c>connection/2</c>): whether results of this data source are cached in the serving layer, at most for how long, who may
/// share an entry and which dataset it is about. Off unless <see cref="Enabled"/>. The values are checked by <see cref="ConnectionDefinitionLoader"/> with <see cref="CacheSection"/>'s rules.
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "The cache setting of a serving data source (connection/2).")]
public sealed class CacheSpec
{
    public bool Enabled { get; set; }

    /// <summary>A whole number and a unit (<c>90s</c>, <c>5m</c>, <c>2h</c>), at most <c>24h</c>.</summary>
    [ConfigKey(Pattern = CacheSection.TtlPattern)]
    public string Ttl { get; set; } = "5m";

    /// <summary>caller | rules | shared.</summary>
    [ConfigKey(Pattern = "caller|rules|shared")]
    public string Scope { get; set; } = "caller";

    public string? Dataset { get; set; }
}

/// <summary>
/// The <c>grants:</c> section of a connection definition used as a serving data source (plan 22a GP03, owner decision of 2026-11-08; <c>connection/2</c>): which people of the identity home may read the data source, by home group, or everyone.
/// The grants are part of the definition a Build project publishes: a publish replaces them completely, and the Gateway that serves the data source enforces exactly what the definition says (it has no grants of its own to add or take away).
/// A data source with no <c>grants</c> (or an empty one) is used by no home user.
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "Who may read a serving data source: home groups, or everyone (connection/2).")]
public sealed class GrantsSpec
{
    /// <summary>The identity home's groups whose members may use the data source (names match ignoring case). Not together with <see cref="Everyone"/>.</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Every user of the identity home may use the data source, whatever their groups. Say it explicitly: leaving the groups out opens nothing.</summary>
    public bool Everyone { get; set; }
}

/// <summary>
/// How a connection of an outside kind (<c>http</c>, <c>model-provider</c>) signs in (plan 27a IX01 and IX02; <c>connection/2</c>, additive). Every secret is a <b>reference</b> (<c>secret:</c>, <c>env:</c>, <c>file:</c>, <c>keyring:</c>, <c>dpapi:</c>), never the value.
/// <c>method</c> is <c>none</c>, <c>api_key</c> (<see cref="KeySecret"/> sent in the header or query parameter <see cref="Name"/>, after <see cref="Prefix"/>), <c>basic</c> (<see cref="User"/> and <see cref="PasswordSecret"/>),
/// <c>oauth2_client_credentials</c> (<see cref="TokenUrl"/>, <see cref="ClientId"/>, <see cref="ClientSecretSecret"/>, <see cref="Scope"/>; the token is cached and refreshed), <c>oauth2_authorization_code</c> (plan 27a IX03: a person gives consent once on the console, the refresh token is kept as the
/// secret <see cref="RefreshTokenSecret"/> and every access token is got from it), <c>client_certificate</c> (a PKCS#12 file or a secret holding it as Base64)
/// or <c>signed_webhook</c> (plan 22a GP18 fold: the request body is signed with HMAC-SHA256 and a timestamp using the shared secret <see cref="KeySecret"/>, sent in the header <see cref="Name"/>, default <c>X-DbData-Signature</c>).
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "How a connection of an outside kind signs in (connection/2).")]
public sealed class SignInSpec
{
    [ConfigKey(Pattern = "none|api_key|basic|oauth2_client_credentials|oauth2_authorization_code|client_certificate|signed_webhook|ssh_key")]
    public string Method { get; set; } = "none";

    /// <summary>api_key: where the key goes, <c>header</c> or <c>query</c>.</summary>
    [ConfigKey(Pattern = "header|query")]
    public string In { get; set; } = "header";

    /// <summary>api_key: the header or query parameter name (default <c>X-Api-Key</c>, or <c>api_key</c> in a query).</summary>
    public string? Name { get; set; }

    /// <summary>api_key: text written before the key in a header, such as <c>Bearer </c>.</summary>
    public string? Prefix { get; set; }

    public string? KeySecret { get; set; }
    public string? User { get; set; }
    public string? PasswordSecret { get; set; }
    public string? TokenUrl { get; set; }

    /// <summary>oauth2_authorization_code: the address where a person gives consent (the provider's authorization endpoint).</summary>
    public string? AuthorizeUrl { get; set; }

    /// <summary>oauth2_authorization_code: the address that revokes a token (RFC 7009); none: revoking only forgets the token here.</summary>
    public string? RevokeUrl { get; set; }

    /// <summary>oauth2_authorization_code: the reference of the secret that keeps the refresh token the consent gave (the console writes it; it is never typed). Convention: <c>secret:dbdatakit:server:connection:&lt;name&gt;:refresh_token</c>.</summary>
    public string? RefreshTokenSecret { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecretSecret { get; set; }
    public string? Scope { get; set; }

    /// <summary>oauth2_client_credentials: how the client signs in to the token address, <c>basic</c> (default) or <c>body</c>.</summary>
    [ConfigKey(Pattern = "basic|body")]
    public string ClientAuth { get; set; } = "basic";

    /// <summary>client_certificate: a PKCS#12 (.pfx) file on this machine ...</summary>
    public string? CertificateFile { get; set; }

    /// <summary>... or a secret reference that holds it as Base64.</summary>
    public string? CertificateSecret { get; set; }
    public string? CertificatePasswordSecret { get; set; }

    /// <summary>ssh_key (plan 27a IX04): the reference of the secret that holds the private key (OpenSSH or PEM text, RSA, ECDSA or Ed25519) ...</summary>
    public string? PrivateKeySecret { get; set; }

    /// <summary>... and, when the key is encrypted, the reference of its passphrase.</summary>
    public string? PassphraseSecret { get; set; }
}

/// <summary>
/// The <c>file_server:</c> section of a connection of kind <c>sftp</c>, <c>scp</c> or <c>ftps</c> (plan 27a IX04; <c>connection/2</c>, additive): the server, the folder every path is relative to, and what the server is <b>pinned</b> to. An SSH server is known by the fingerprint of its host key
/// (<see cref="HostKeys"/>, <c>SHA256:...</c> as <c>ssh-keygen -lf</c> prints it); a changed key is refused naming it. An FTPS server is trusted by the machine's roots, by <c>tls.ca_file</c> or by <see cref="CertificateSha256"/>, the SHA-256 of its certificate.
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "A file server (connection/2, kinds sftp, scp, ftps).")]
public sealed class FileServerSpec
{
    [ConfigKey(Required = true)]
    public string Host { get; set; } = "";

    /// <summary>Default: 22 for sftp and scp, 21 for explicit FTPS and 990 for implicit.</summary>
    [ConfigKey(Min = 1, Max = 65535)]
    public int? Port { get; set; }

    /// <summary>The folder every path of a call is under (<c>/</c> by default, or <c>/upload</c>). A path that leaves it is refused.</summary>
    public string Root { get; set; } = "/";

    /// <summary>sftp, scp: the pinned host keys as <c>SHA256:&lt;base64&gt;</c>. Required unless <c>tls.verify: false</c> says on purpose that any key is taken.</summary>
    public List<string> HostKeys { get; set; } = [];

    /// <summary>ftps: the SHA-256 of the server's certificate (64 hex digits) the connection is pinned to, besides or instead of a chain the machine trusts.</summary>
    public List<string> CertificateSha256 { get; set; } = [];

    /// <summary>ftps: <c>explicit</c> (AUTH TLS on the usual port, the default) or <c>implicit</c> (TLS from the first byte, usually port 990).</summary>
    [ConfigKey(Pattern = "explicit|implicit")]
    public string FtpsMode { get; set; } = "explicit";
}

/// <summary>
/// The <c>webhook:</c> section of a connection of kind <c>http</c> that a delivery channel sends through (plan 22a GP09 and the GP18 fold; <c>connection/2</c>, additive): which message the receiver takes (<c>slack</c> Block Kit, <c>teams</c> Adaptive Card, <c>signed</c> a JSON document
/// signed with <c>sign_in.method: signed_webhook</c>), and the private addresses it may reach. The address of a webhook carries a token, so the secret parts are <c>{placeholders}</c> of the <c>endpoint</c> template bound in <see cref="ConnectionDefinition.EndpointSecrets"/>.
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "A webhook a delivery channel sends through (connection/2, kind http).")]
public sealed class WebhookSpec
{
    [ConfigKey(Required = true, Pattern = "slack|teams|signed")]
    public string Format { get; set; } = "";

    /// <summary>Hosts, addresses or CIDR ranges (<c>10.1.0.0/16</c>) that may be private (loopback, 10/8, 192.168/16, ...); an address in a private range that is not listed is refused. A plain <c>http</c> address is used only for a listed host.</summary>
    public List<string> AllowPrivate { get; set; } = [];

    [ConfigKey(Min = 1, Max = 100000)]
    public int? LimitPerMinute { get; set; }

    [ConfigKey(Min = 1, Max = 600)]
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// The <c>smtp:</c> section of a connection of kind <c>smtp</c> (plan 22a GP08 and the GP18 fold; <c>connection/2</c>, additive): the mail server, how the connection is protected, the sender address. The login is <c>sign_in</c> (<c>basic</c>, the password a reference).
/// <c>tls: none</c> is refused unless the host is this machine (<c>localhost</c>, <c>127.x</c>, <c>::1</c>) or <see cref="AllowPlainText"/> says so on purpose.
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "A mail server a delivery channel sends through (connection/2, kind smtp).")]
public sealed class SmtpSpec
{
    [ConfigKey(Required = true)]
    public string Host { get; set; } = "";

    /// <summary>Default: 587 for starttls, 465 for implicit, 25 for none.</summary>
    [ConfigKey(Min = 1, Max = 65535)]
    public int? Port { get; set; }

    [ConfigKey(Pattern = "starttls|implicit|none")]
    public string Tls { get; set; } = "starttls";

    /// <summary>The sender address: <c>ana@example.com</c> or <c>Name &lt;ana@example.com&gt;</c>.</summary>
    [ConfigKey(Required = true)]
    public string Sender { get; set; } = "";

    /// <summary>A certificate file (PEM or DER) the server's chain is checked against instead of the machine's roots. The name is checked either way.</summary>
    public string? TrustedCertificate { get; set; }

    public string? Helo { get; set; }

    [ConfigKey(Min = 1, Max = 100000)]
    public int? LimitPerMinute { get; set; }

    [ConfigKey(Min = 1, Max = 600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Allows <c>tls: none</c> for a host that is not this machine (a relay on a private network): said on purpose, never the default.</summary>
    public bool AllowPlainText { get; set; }
}

/// <summary>One model of a provider: its id as the provider names it, its window and largest answer, its price per million tokens (null: not priced, as a local model).</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class ModelSpec
{
    [ConfigKey(Required = true)]
    public string Id { get; set; } = "";
    [ConfigKey(Min = 1000, Max = 10000000)]
    public int? Window { get; set; }
    [ConfigKey(Min = 16, Max = 1000000)]
    public int? MaxOutput { get; set; }
    public PriceSpec? Price { get; set; }
}

[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class PriceSpec
{
    [ConfigKey(Required = true, Min = 0)]
    public decimal Input { get; set; }
    [ConfigKey(Required = true, Min = 0)]
    public decimal Output { get; set; }
}

/// <summary>The <c>model_provider:</c> section of a connection of kind <c>model-provider</c> (plan 27a AG05): which wire it speaks, cloud or local, what it is for and the models it allows. Its key is <c>sign_in.key_secret</c>.</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "A model provider (connection kind model-provider).")]
public sealed class ModelProviderSpec
{
    /// <summary>anthropic | openai | foundry | ollama | openai-compatible.</summary>
    [ConfigKey(Required = true, Pattern = "anthropic|openai|foundry|ollama|openai-compatible")]
    public string Provider { get; set; } = "";

    /// <summary>cloud | local; required for openai-compatible, otherwise cloud (local for ollama).</summary>
    [ConfigKey(Pattern = "cloud|local")]
    public string? Locality { get; set; }

    /// <summary>chat | embeddings.</summary>
    [ConfigKey(Pattern = "chat|embeddings")]
    public string Purpose { get; set; } = "chat";

    /// <summary>bearer | api-key | none; the provider's usual one when left out.</summary>
    [ConfigKey(Pattern = "bearer|api-key|none")]
    public string? AuthStyle { get; set; }

    public List<ModelSpec> Models { get; set; } = [];

    /// <summary>Where a provider is when no endpoint is written (a Foundry deployment and a compatible server have no default).</summary>
    public static string? DefaultEndpoint(string provider) => provider switch
    {
        "anthropic" => "https://api.anthropic.com",
        "openai" => "https://api.openai.com/v1",
        "ollama" => "http://localhost:11434/v1",
        _ => null,
    };
}

/// <summary>The kinds of a connection (plan 27a IX01): a database (the default), an HTTP API, a model provider, a mail server.</summary>
public static class ConnectionKinds
{
    public const string Database = "database";
    public const string Http = "http";
    public const string ModelProvider = "model-provider";

    /// <summary>A mail server a delivery channel sends through (plan 22a GP08, folded into connections by GP18).</summary>
    public const string Smtp = "smtp";

    /// <summary>A server that keeps files and is reached over SSH (plan 27a IX04): SFTP lists, reads and writes; SCP reads and writes (it cannot list).</summary>
    public const string Sftp = "sftp";
    public const string Scp = "scp";

    /// <summary>A file server reached over FTP with TLS (plan 27a IX04).</summary>
    public const string Ftps = "ftps";

    public static readonly string[] All = [Database, Http, ModelProvider, Smtp, Sftp, Scp, Ftps];

    /// <summary>The kinds that are a file store (<see cref="ConnectionKindGroup.FileStore"/>): they have a <c>file_server</c> section and no <c>endpoint</c>.</summary>
    public static bool IsFileServer(string? kind) => kind is Sftp or Scp or Ftps;

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// The shared connection definition, schema <c>connection/2</c> (and <c>connection/1</c>, which is read as before). Load it with <see cref="ConnectionDefinitionLoader"/>, which also enforces the rules a binder cannot.
/// Format 2 adds the optional sections <c>cache</c>, <c>checks</c> (plan 24a D5 and UC03) and <c>grants</c> (plan 22a GP03); a file that says <c>connection/1</c> and has either is refused naming the file and line.
/// </summary>
[ConfigSchema("connection", Naming = KeyNaming.SnakeCase, VersionKey = "schema", Version = 2, Description = "A database connection (connection/2; connection/1 is read as well).")]
public sealed class ConnectionDefinition
{
    /// <summary>The format every program reads and writes by default: files are written as <c>connection/1</c> until they use a section of the next format.</summary>
    public const string SchemaId = "connection/1";

    /// <summary>The next format: <see cref="SchemaId"/> plus the optional sections <c>cache</c> and <c>checks</c>.</summary>
    public const string SchemaIdV2 = "connection/2";

    /// <summary>The newest format this reads.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The id a save or a publish writes (plan 33 6d, rules 3 and 5): <c>connection/</c> and <see cref="CurrentVersion"/>.</summary>
    public static string CurrentSchemaId => "connection/" + CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The format the file was read in (1 or 2); set by the loader. Its sections <c>cache</c> and <c>checks</c> need 2.</summary>
    [ConfigIgnore]
    public int SchemaVersion { get; set; } = CurrentVersion;

    [ConfigKey(Required = true, Pattern = "[A-Za-z][A-Za-z0-9_.-]{0,63}")]
    public string Name { get; set; } = "";
    /// <summary>
    /// What the connection is (format 2, plan 27a IX01): <c>database</c> (also when the key is left out), <c>http</c>, <c>model-provider</c> or <c>smtp</c>. Only the loader's <c>outsideKinds</c> option lets a program read the last two; a program that keeps database connections refuses
    /// such a file naming it. A format-1 file cannot say it.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>The kind, with <c>database</c> for a file that does not say.</summary>
    [ConfigIgnore]
    public string KindName => Kind ?? ConnectionKinds.Database;

    /// <summary>True for a kind that is not a database (<c>http</c>, <c>model-provider</c>, <c>smtp</c>).</summary>
    [ConfigIgnore]
    public bool IsOutside => KindName != ConnectionKinds.Database;

    /// <summary>The address of an outside system, as a template (<see cref="EndpointTemplate"/>): an http or https address with no user name or password and no secret in plain sight; the secret parts are <c>{placeholders}</c> bound in <see cref="EndpointSecrets"/>. Or wholly a secret reference (<c>secret:...</c>), for a service nothing classifies, shown as "address hidden".</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The secret parts of <see cref="Endpoint"/> as <c>{placeholder}</c> names bound to secret references (<c>token: secret:...</c>): a webhook's address carries a token, so the file keeps the visible template and the secrets stay in the store (see <see cref="EndpointTemplate"/>).
    /// Every placeholder is bound and every binding used.
    /// </summary>
    public Dictionary<string, string> EndpointSecrets { get; set; } = [];

    /// <summary>The webhook section (kind <c>http</c> that a delivery channel sends through); null: an ordinary API connection.</summary>
    public WebhookSpec? Webhook { get; set; }

    /// <summary>The mail server (kind <c>smtp</c>).</summary>
    public SmtpSpec? Smtp { get; set; }

    /// <summary>The file server (kinds <c>sftp</c>, <c>scp</c>, <c>ftps</c>; plan 27a IX04).</summary>
    public FileServerSpec? FileServer { get; set; }

    /// <summary>How an outside kind signs in; null: it does not.</summary>
    public SignInSpec? SignIn { get; set; }

    /// <summary>An address beneath the endpoint that <b>Test</b> calls (a health or a cheap read), such as <c>/v1/ping</c>; none: the endpoint itself. A relative path, with no query: a key is never part of it.</summary>
    public string? TestPath { get; set; }

    /// <summary>The model provider section (kind <c>model-provider</c>).</summary>
    public ModelProviderSpec? ModelProvider { get; set; }

    /// <summary>sqlserver | postgres | mysql | oracle | duckdb | odbc | flightsql | jdbc | descriptor id. Required for a database; an outside kind has none.</summary>
    [ConfigKey]
    public string Engine { get; set; } = "";
    /// <summary>Optional dialect id override (Plan 05).</summary>
    public string? Dialect { get; set; }
    /// <summary>The id of the paired server client that created this connection (a Build server publishing it); empty for one made by hand. The server that keeps the file checks it on every change by a client (<c>OwnerScope</c>).</summary>
    public string? ManagedBy { get; set; }
    public AccessMode Access { get; set; } = AccessMode.Readwrite;
    public AddressSpec? Address { get; set; }
    /// <summary>A connection string without credentials. Plaintext credentials are refused.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Reference to a secret holding the whole connection string (Build style).</summary>
    public string? ConnectionStringSecret { get; set; }
    public LoginSpec? Auth { get; set; }
    public LoginSpec? ReadLogin { get; set; }
    public LoginSpec? WriteLogin { get; set; }
    public TlsSpec Tls { get; set; } = new();
    public TimeoutSpec Timeouts { get; set; } = new();
    /// <summary>Extra connection-string properties. Credential, host and identity keys are refused (see <see cref="CredentialPolicy"/>).</summary>
    public Dictionary<string, string> Properties { get; set; } = [];
    public ExtensionBag Extensions { get; set; } = new();

    /// <summary>The cache setting of a serving data source (format 2); null: the cache is off.</summary>
    public CacheSpec? Cache { get; set; }

    /// <summary>Who may read a serving data source (format 2, plan 22a GP03): home groups, or everyone. Null: no home user may use it. Travels with the published definition; a republish replaces it.</summary>
    public GrantsSpec? Grants { get; set; }

    /// <summary>
    /// The checks of this data source (format 2) as the binder sees them: a list of mappings. <c>ConnectionDefinitionLoader</c> reads them strictly as <c>check/1</c> entries (<see cref="CheckDefinitions"/>); null when the file has none.
    /// </summary>
    public List<ExtensionBag>? Checks { get; set; }

    /// <summary>The <c>check/1</c> entries of <see cref="Checks"/>, read and validated by the loader (23a FQ01): each names the table it is about in <c>dataset</c>. Null when the file has none or when it was not made by the loader.</summary>
    [ConfigIgnore]
    public IReadOnlyList<DbDataKit.Quality.CheckDefinition>? CheckDefinitions { get; set; }

    /// <summary>The login spec for <paramref name="role"/>. Read never returns the write login.</summary>
    public LoginSpec? LoginFor(LoginRole role) => role switch
    {
        LoginRole.Read => ReadLogin ?? Auth,
        LoginRole.Write => WriteLogin ?? Auth,
        _ => Auth,
    };
}
