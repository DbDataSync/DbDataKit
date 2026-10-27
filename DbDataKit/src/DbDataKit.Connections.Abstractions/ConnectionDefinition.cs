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

/// <summary>Timeouts in seconds. Never null: an unset timeout is the default, not "forever" (Proxy gap).</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class TimeoutSpec
{
    [ConfigKey(Min = 1, Max = 86400)]
    public int ConnectS { get; set; } = 15;
    [ConfigKey(Min = 1, Max = 86400)]
    public int CommandS { get; set; } = 300;
}

/// <summary>Per-product settings (<c>extensions.sync</c>, <c>extensions.proxy</c>, ...), kept as nodes so each product binds its own part.</summary>
[ConfigSchema(UnknownKeys = UnknownKeyPolicy.Preserve)]
public sealed class ExtensionBag
{
    [ConfigExtra]
    public Dictionary<string, YamlNode> Items { get; set; } = [];
}

/// <summary>The shared connection definition, schema <c>connection/1</c>. Load it with <see cref="ConnectionDefinitionLoader"/>, which also enforces the rules a binder cannot.</summary>
[ConfigSchema("connection", Naming = KeyNaming.SnakeCase, VersionKey = "schema", Description = "A database connection (connection/1).")]
public sealed class ConnectionDefinition
{
    public const string SchemaId = "connection/1";

    [ConfigKey(Required = true, Pattern = "[A-Za-z][A-Za-z0-9_.-]{0,63}")]
    public string Name { get; set; } = "";
    /// <summary>sqlserver | postgres | mysql | oracle | duckdb | odbc | flightsql | jdbc | descriptor id.</summary>
    [ConfigKey(Required = true)]
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

    /// <summary>The login spec for <paramref name="role"/>. Read never returns the write login.</summary>
    public LoginSpec? LoginFor(LoginRole role) => role switch
    {
        LoginRole.Read => ReadLogin ?? Auth,
        LoginRole.Write => WriteLogin ?? Auth,
        _ => Auth,
    };
}
