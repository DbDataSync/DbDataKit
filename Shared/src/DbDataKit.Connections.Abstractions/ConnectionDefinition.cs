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
/// <c>oauth2_client_credentials</c> (<see cref="TokenUrl"/>, <see cref="ClientId"/>, <see cref="ClientSecretSecret"/>, <see cref="Scope"/>; the token is cached and refreshed) or <c>client_certificate</c> (a PKCS#12 file or a secret holding it as Base64).
/// </summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase, Description = "How a connection of an outside kind signs in (connection/2).")]
public sealed class SignInSpec
{
    [ConfigKey(Pattern = "none|api_key|basic|oauth2_client_credentials|client_certificate")]
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

/// <summary>The kinds of a connection (plan 27a IX01): a database (the default), an HTTP API, a model provider.</summary>
public static class ConnectionKinds
{
    public const string Database = "database";
    public const string Http = "http";
    public const string ModelProvider = "model-provider";

    public static readonly string[] All = [Database, Http, ModelProvider];

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
    /// What the connection is (format 2, plan 27a IX01): <c>database</c> (also when the key is left out), <c>http</c> or <c>model-provider</c>. Only the loader's <c>outsideKinds</c> option lets a program read the last two; a program that keeps database connections refuses
    /// such a file naming it. A format-1 file cannot say it.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>The kind, with <c>database</c> for a file that does not say.</summary>
    [ConfigIgnore]
    public string KindName => Kind ?? ConnectionKinds.Database;

    /// <summary>True for a kind that is not a database (<c>http</c>, <c>model-provider</c>).</summary>
    [ConfigIgnore]
    public bool IsOutside => KindName != ConnectionKinds.Database;

    /// <summary>The address of an outside system: an http or https address with no user name, password or query in it (a key is never part of the address).</summary>
    public string? Endpoint { get; set; }

    /// <summary>How an outside kind signs in; null: it does not.</summary>
    public SignInSpec? SignIn { get; set; }

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
