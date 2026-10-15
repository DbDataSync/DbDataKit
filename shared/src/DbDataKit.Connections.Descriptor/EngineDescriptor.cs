using DbDataKit.Config;

namespace DbDataKit.Connections.Descriptor;

/// <summary>Key spellings of the provider. A key left out keeps its default; <c>port: ""</c> or <c>none</c> means the provider has no port key.</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class ConnectionStringKeysSpec
{
    public string Host { get; set; } = "Host";
    public string Port { get; set; } = "Port";
    public string Database { get; set; } = "Database";
    public string Username { get; set; } = "User Id";
    public string Password { get; set; } = "Password";
    public string ConnectTimeout { get; set; } = "Connect Timeout";
    public string CommandTimeout { get; set; } = "";
    /// <summary>The integrated authentication flag, when the provider has one.</summary>
    public string IntegratedSecurity { get; set; } = "";
    /// <summary>Key that turns encryption on or off. Only the spelling is here; whether to encrypt is the connection's (secure) default.</summary>
    public string EncryptKey { get; set; } = "";
    public string EncryptOn { get; set; } = "true";
    public string EncryptOff { get; set; } = "false";
    /// <summary>Key that controls certificate validation. The descriptor gives the spelling and the two values; it never decides to trust.</summary>
    public string TrustKey { get; set; } = "";
    /// <summary>Value written when the connection explicitly trusts the server certificate.</summary>
    public string TrustValue { get; set; } = "true";
    /// <summary>Value written when the certificate is verified (the default).</summary>
    public string VerifyValue { get; set; } = "false";
}

/// <summary>Read-only recipe of the engine: connection-string keys and session statements.</summary>
[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public sealed class ReadOnlySpec
{
    public Dictionary<string, string> Keys { get; set; } = [];
    public List<string> Statements { get; set; } = [];
    public string? Notes { get; set; }
}

/// <summary>
/// A YAML described ADO.NET engine, schema <c>engine/1</c> (the port of Sync's <c>driver.yaml</c>, minus type maps, catalogs and
/// capability lists, which now come from the dialect). SQL facts are a dialect id from <c>dialect.yaml</c> (built in or supplied alongside).
/// </summary>
[ConfigSchema("engine", Naming = KeyNaming.SnakeCase, VersionKey = "schema", Description = "A YAML described ADO.NET engine (engine/1).")]
public sealed class EngineDescriptor
{
    public const string SchemaId = "engine/1";

    [ConfigKey(Required = true, Pattern = "[a-z][a-z0-9._-]{0,63}")]
    public string Id { get; set; } = "";
    [ConfigKey(Required = true)]
    public string DisplayName { get; set; } = "";
    /// <summary>The name an <see cref="IProviderFactoryResolver"/> turns into a <c>DbProviderFactory</c> (a library id or an ADO.NET invariant name).</summary>
    [ConfigKey(Required = true)]
    public string Library { get; set; } = "";
    /// <summary>A dialect id (Plan 05 <c>dialect.yaml</c>).</summary>
    [ConfigKey(Required = true)]
    public string Dialect { get; set; } = "";
    /// <summary>Write the string with ODBC brace quoting (set for engines reached through System.Data.Odbc).</summary>
    public bool OdbcQuoting { get; set; }
    [ConfigKey(Min = 1, Max = 65535)]
    public int? DefaultPort { get; set; }
    public string? DefaultDatabase { get; set; }
    public ConnectionStringKeysSpec ConnectionStringKeys { get; set; } = new();
    /// <summary>Written first on every connection. Credential, host, identity and TLS keys are refused here; <c>Driver</c> and <c>DSN</c> are the intended use.</summary>
    public Dictionary<string, string> FixedProperties { get; set; } = [];
    /// <summary>The fixed properties a connection may override through its own <c>properties</c>. Every other fixed property is locked.</summary>
    public List<string> OverridableProperties { get; set; } = [];
    public ReadOnlySpec ReadOnly { get; set; } = new();
    public string? TestQuery { get; set; }
}
