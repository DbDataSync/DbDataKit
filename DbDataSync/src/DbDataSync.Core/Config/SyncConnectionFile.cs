using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using YamlDotNet.Serialization;

namespace DbDataSync.Core.Config;

/// <summary>
/// What Sync keeps in a connection file beside the shared fields, under <c>extensions.sync</c> (plan 13, E01). The keys are Sync's own, spelled as in every other Sync file, so the nested script and hook bindings are the shapes they always were.
/// </summary>
public sealed class SyncConnectionExtension
{
    /// <summary>The database to work in, for a connection that is addressed by a <c>connection_string</c> (with an <c>address</c> it is <c>address.database</c>).</summary>
    public string? Database { get; set; }

    /// <summary>A connect timeout the shared <c>timeouts</c> block cannot say: <c>0</c> (unlimited) or more than a day.</summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>A command timeout the shared <c>timeouts</c> block cannot say.</summary>
    public int? CommandTimeoutSeconds { get; set; }

    public SegmentOrder? SegmentOrder { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
    public Dictionary<string, ScriptBinding?>? Scripts { get; set; }
    public Dictionary<string, List<HookConfig>?>? Hooks { get; set; }
    public string? TestQuery { get; set; }
}

/// <summary>
/// A Sync connection as a file of the shared connection format, <c>connection/1</c> (plan 13, E01): <c>config/connections/&lt;name&gt;.yaml</c>, the same file the Proxy and Build keep, with what only Sync has under <c>extensions.sync</c>.
/// The file is read with the shared strict loader (unknown keys, no plaintext credential, secret references that parse, a complete login) and mapped onto <see cref="ConnectionConfig"/>, so everything that consumes a connection is unchanged.
/// <list type="bullet">
/// <item><c>engine</c> is the driver: <c>sqlserver</c>, <c>postgres</c>, <c>mysql</c>, <c>oracle</c>, <c>duckdb</c> and <c>flightsql</c> (with <c>dialect</c> for the Flight SQL variants) are Sync's built-in drivers under the names every product uses; any other value is a driver id as written.</item>
/// <item><c>address</c> is host, port and database; <c>connection_string</c> is the engine-native address (then the database is <c>extensions.sync.database</c>).</item>
/// <item><c>auth</c> is <c>sql</c> (a user and <c>password_secret: secret:dbdata:sync:connection:&lt;name&gt;</c>), <c>integrated</c> or <c>none</c>.</item>
/// <item><c>timeouts</c> are the connect and command seconds when they are between 1 and 86400; an unset timeout is the default of Sync (30 and 1800 seconds), not that of the shared type.</item>
/// <item>What Sync does not use is refused by key, never ignored: <c>tls</c>, <c>read_login</c>, <c>write_login</c>, <c>connection_string_secret</c>, <c>access</c>, <c>managed_by</c>.</item>
/// </list>
/// A file in the form of earlier releases (no <c>schema</c>, <c>driverType</c>, <c>schemaVersion</c>) is refused with a message naming it and the new form; nothing is converted.
/// </summary>
internal static partial class SyncConnectionFile
{
    private static readonly (string Driver, string Engine, string? Dialect)[] Engines =
    [
        (DriverIds.MsSql, "sqlserver", null),
        (DriverIds.Postgres, "postgres", null),
        (DriverIds.MySql, "mysql", null),
        (DriverIds.Oracle, "oracle", null),
        (DriverIds.DuckDb, "duckdb", null),
        (DriverIds.ArrowFlightSql, "flightsql", null),
        (DriverIds.ArrowFlightSqlMsSql, "flightsql", "sqlserver"),
        (DriverIds.ArrowFlightSqlPostgres, "flightsql", "postgres"),
        (DriverIds.ArrowFlightSqlMySql, "flightsql", "mysql"),
        (DriverIds.ArrowFlightSqlOracle, "flightsql", "oracle"),
        (DriverIds.ArrowFlightSqlDuckDb, "flightsql", "duckdb"),
    ];

    private const string SecretPrefix = "secret:";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();

    /// <summary>Connection names are the shared <c>name</c>: a letter, then letters, digits, <c>_ . -</c>, at most 64 characters.</summary>
    public static void ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name))
            throw new ConfigValidationException(
                $"Connection name '{name}' must start with a letter and use only letters (A-Z), digits, '-', '_' and '.', at most 64 characters (it is the `name` of a connection/1 file).");
    }

    // ---- write ----

    public static string Write(ConnectionConfig c)
    {
        var ext = new SyncConnectionExtension
        {
            SegmentOrder = c.SegmentOrder,
            Properties = c.Properties.Count == 0 ? null : c.Properties,
            Scripts = c.Scripts.Count == 0 ? null : c.Scripts,
            Hooks = c.Hooks.Count == 0 ? null : c.Hooks,
            TestQuery = c.TestQuery,
        };

        var (engine, dialect) = EngineFor(c.DriverType);
        var document = new Document { Name = c.Name, Engine = engine, Dialect = dialect };

        if (!string.IsNullOrWhiteSpace(c.Host))
            document.Address = new AddressDocument { Host = c.Host, Port = c.Port, Database = c.Database };
        else
        {
            document.ConnectionString = c.ConnectionString;
            ext.Database = c.Database;
        }

        document.Auth = c.AuthMode switch
        {
            AuthMode.SqlAuth => new AuthDocument { Mode = "sql", User = c.UserId, PasswordSecret = c.CredentialSecretRef is null ? null : SecretPrefix + c.CredentialSecretRef },
            AuthMode.IntegratedAuth => new AuthDocument { Mode = "integrated" },
            _ => new AuthDocument { Mode = "none" },
        };

        var timeouts = new TimeoutsDocument();
        if (c.ConnectTimeoutSeconds is >= 1 and <= 86400) timeouts.ConnectS = c.ConnectTimeoutSeconds; else ext.ConnectTimeoutSeconds = c.ConnectTimeoutSeconds;
        if (c.CommandTimeoutSeconds is >= 1 and <= 86400) timeouts.CommandS = c.CommandTimeoutSeconds; else ext.CommandTimeoutSeconds = c.CommandTimeoutSeconds;
        if (timeouts.ConnectS is not null || timeouts.CommandS is not null) document.Timeouts = timeouts;

        document.Extensions = new ExtensionsDocument { Sync = IsEmpty(ext) ? null : ext };
        if (document.Extensions.Sync is null) document.Extensions = null;

        return YamlConfigSerializer.Serialize(document);
    }

    private static bool IsEmpty(SyncConnectionExtension e) =>
        e.Database is null && e.ConnectTimeoutSeconds is null && e.CommandTimeoutSeconds is null && e.SegmentOrder is null
        && e.Properties is null && e.Scripts is null && e.Hooks is null && e.TestQuery is null;

    private static (string Engine, string? Dialect) EngineFor(string driverType)
    {
        foreach (var (driver, engine, dialect) in Engines)
            if (driver == driverType) return (engine, dialect);
        // a driver installed at run time keeps its id; one that would be read back as a built-in is refused
        if (Engines.Any(e => e.Engine == driverType))
            throw new ConfigValidationException($"Driver id '{driverType}' is the name of a built-in engine in connection files; give the driver another id.");
        return (driverType, null);
    }

    // ---- read ----

    /// <summary>Reads <paramref name="text"/>, the content of <paramref name="path"/>, as the connection called <paramref name="name"/>. Throws <see cref="ConfigFileException"/> naming the file.</summary>
    public static ConnectionConfig Read(string text, string path, string name)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, path, diags);
        if (diags.Count == 0 && root is YamlMapping first && first.Get("schema") is null && IsEarlierForm(first))
            throw new ConfigFileException(path, [EarlierFormDiagnostic(path, name)]);

        var loaded = ConnectionDefinitionLoader.Load(text, path);
        var errors = loaded.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
        if (errors.Count > 0 || loaded.Definition is null) throw new ConfigFileException(path, errors);
        var d = loaded.Definition;
        var map = (YamlMapping)root!;

        void Error(string message, string? keyPath = null, YamlEntry? at = null, string? fix = null) =>
            errors.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.InvalidValue, path, at?.Key.Line ?? 1, at?.Key.Column ?? 1, message, fix, keyPath: keyPath));

        if (!string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            Error($"`name` is `{d.Name}`, and the file is called `{name}.yaml`; the file name is the name of the connection.", "name", map.GetEntry("name"), $"Name the file `{d.Name}.yaml`, or set `name: {name}`.");

        foreach (var key in new[] { "tls", "read_login", "write_login", "connection_string_secret", "access", "managed_by" })
            if (map.GetEntry(key) is { } entry)
                Error($"`{key}` is not a setting Sync uses.", key, entry, "Remove it; Sync reads `address` or `connection_string`, `auth`, `timeouts`, `properties` and `extensions.sync`.");

        var (driver, driverError) = DriverFor(d);
        if (driverError is not null) Error(driverError, d.Dialect is null ? "engine" : "dialect", map.GetEntry(d.Dialect is null ? "engine" : "dialect"));

        var auth = d.Auth;
        if (auth is { Mode: DbDataKit.Connections.AuthMode.Token })
            Error("A `token` login is not one Sync connects with.", "auth.mode", (map.Get("auth") as YamlMapping)?.GetEntry("mode") ?? map.GetEntry("auth"), "Use `sql`, `integrated` or `none`.");
        if (auth is { ConnectionStringSecret: not null })
            Error("`auth.connection_string_secret` is not a setting Sync uses.", "auth.connection_string_secret", map.GetEntry("auth"), "Use `user` and `password_secret`.");
        string? credentialRef = null;
        if (auth is { Mode: DbDataKit.Connections.AuthMode.Sql, PasswordSecret: { } secret })
        {
            if (secret.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase)) credentialRef = secret[SecretPrefix.Length..];
            else Error("Sync reads a connection's password from its own secret store; `auth.password_secret` must be `secret:dbdata:sync:connection:<name>`.", "auth.password_secret", (map.Get("auth") as YamlMapping)?.GetEntry("password_secret") ?? map.GetEntry("auth"));
        }

        var hasHost = !string.IsNullOrWhiteSpace(d.Address?.Host);
        if (d.Address is not null && !hasHost)
            Error("Sync addresses a connection by `address.host` (with `port` and `database`) or by `connection_string`.", "address", map.GetEntry("address"));

        SyncConnectionExtension? ext = null;
        if (map.Get("extensions") is YamlMapping extensions && extensions.Get("sync") is { } syncNode)
        {
            var before = errors.Count;
            StrictConfigFile.CheckNode<SyncConnectionExtension>(syncNode, "extensions.sync", path, errors);
            if (errors.Count == before)
                ext = StrictConfigFile.Bind(text, path, t => YamlConfigSerializer.Deserialize<Envelope>(t).Extensions?.Sync);
        }
        ext ??= new SyncConnectionExtension();

        if (hasHost && ext.Database is not null)
            Error("`extensions.sync.database` is for a connection with a `connection_string`; with an `address` the database is `address.database`.", "extensions.sync.database", (map.Get("extensions") as YamlMapping)?.GetEntry("sync"));

        if (errors.Count > 0) throw new ConfigFileException(path, errors);

        var timeouts = map.Get("timeouts") as YamlMapping;
        var config = new ConnectionConfig
        {
            Name = d.Name,
            DriverType = driver!,
            Host = hasHost ? d.Address!.Host : null,
            Port = hasHost ? d.Address!.Port : null,
            ConnectionString = d.ConnectionString,
            Database = hasHost ? d.Address!.Database : ext.Database,
            AuthMode = auth?.Mode switch
            {
                DbDataKit.Connections.AuthMode.Sql => AuthMode.SqlAuth,
                DbDataKit.Connections.AuthMode.Integrated => AuthMode.IntegratedAuth,
                _ => AuthMode.None,
            },
            UserId = auth?.Mode == DbDataKit.Connections.AuthMode.Sql ? auth.User : null,
            CredentialSecretRef = credentialRef,
            ConnectTimeoutSeconds = ext.ConnectTimeoutSeconds ?? (timeouts?.Get("connect_s") is not null ? d.Timeouts.ConnectS : null),
            CommandTimeoutSeconds = ext.CommandTimeoutSeconds ?? (timeouts?.Get("command_s") is not null ? d.Timeouts.CommandS : null),
            SegmentOrder = ext.SegmentOrder,
            Properties = new Dictionary<string, string>(d.Properties).WithAll(ext.Properties ?? []),
            Scripts = ext.Scripts ?? [],
            Hooks = ext.Hooks ?? [],
            TestQuery = ext.TestQuery,
        };
        if (!hasHost && string.IsNullOrWhiteSpace(config.ConnectionString))
            throw new ConfigFileException(path, [DefaultDiagnosticCatalog.Instance.Create(SharedCodes.InvalidValue, path, 1, 1, "Sync addresses a connection by `address.host` (with `port` and `database`) or by `connection_string`.", keyPath: "address")]);
        return config;
    }

    /// <summary>The secret a connection file names, read as leniently as possible so a connection that cannot otherwise be read can still be deleted: the new <c>auth.password_secret</c> or the old <c>credentialSecretRef</c>.</summary>
    public static string? TryReadCredentialRef(string text)
    {
        try
        {
            var lenient = YamlConfigSerializer.Deserialize<LenientEnvelope>(text);
            var secret = lenient?.Auth?.PasswordSecret;
            if (secret is not null) return secret.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase) ? secret[SecretPrefix.Length..] : null;
            return lenient?.CredentialSecretRef;
        }
        catch (YamlDotNet.Core.YamlException) { return null; }
    }

    private static Dictionary<string, string> WithAll(this Dictionary<string, string> target, Dictionary<string, string> more)
    {
        foreach (var (k, v) in more) target[k] = v;
        return target;
    }

    private static bool IsEarlierForm(YamlMapping map) =>
        map.Get("driverType") is not null || map.Get("schemaVersion") is not null || map.Get("authMode") is not null;

    private static ConfigDiagnostic EarlierFormDiagnostic(string path, string name) =>
        DefaultDiagnosticCatalog.Instance.Create(SharedCodes.SchemaVersionInvalid, path, 1, 1,
            $"'{name}.yaml' is a connection in the form of earlier releases (it has `driverType` or `schemaVersion` and no `schema: connection/1`), which Sync no longer reads.",
            "Rewrite it as a connection/1 file: `schema: connection/1`, `name`, `engine` (sqlserver, postgres, mysql, oracle, duckdb, flightsql, or a driver id), `address: {host, port, database}` or `connection_string`, " +
            $"`auth: {{mode: sql, user, password_secret: 'secret:dbdata:sync:connection:{name}'}}` (or `mode: integrated` / `none`), `timeouts: {{connect_s, command_s}}`, and `extensions.sync` for `segmentOrder`, `properties`, `scripts`, `hooks`, `testQuery` and, with a `connection_string`, `database`. " +
            "The password stays in the secret store; see docs/configuration.md.", keyPath: null);

    private static (string? Driver, string? Error) DriverFor(ConnectionDefinition d)
    {
        var engine = d.Engine;
        if (engine == "flightsql")
        {
            if (d.Dialect is null) return (DriverIds.ArrowFlightSql, null);
            var match = Engines.FirstOrDefault(e => e.Engine == "flightsql" && e.Dialect == d.Dialect);
            return match.Driver is null ? (null, $"`dialect` is `{d.Dialect}`; a flightsql connection takes {string.Join(", ", Engines.Where(e => e.Dialect is not null).Select(e => e.Dialect))}.") : (match.Driver, null);
        }
        if (d.Dialect is not null) return (null, "`dialect` is only used with `engine: flightsql`.");
        var builtin = Engines.FirstOrDefault(e => e.Engine == engine && e.Dialect is null);
        return (builtin.Driver ?? engine, null);
    }

    // ---- the shapes read and written with YamlDotNet ----

    private sealed class Envelope
    {
        public ExtensionsDocument? Extensions { get; set; }
    }

    private sealed class LenientEnvelope
    {
        public AuthDocument? Auth { get; set; }
        public string? CredentialSecretRef { get; set; }
    }

    private sealed class Document
    {
        [YamlMember(Alias = "schema", ApplyNamingConventions = false)] public string Schema { get; set; } = ConnectionDefinition.SchemaId;
        [YamlMember(Alias = "name", ApplyNamingConventions = false)] public string Name { get; set; } = "";
        [YamlMember(Alias = "engine", ApplyNamingConventions = false)] public string Engine { get; set; } = "";
        [YamlMember(Alias = "dialect", ApplyNamingConventions = false)] public string? Dialect { get; set; }
        [YamlMember(Alias = "address", ApplyNamingConventions = false)] public AddressDocument? Address { get; set; }
        [YamlMember(Alias = "connection_string", ApplyNamingConventions = false)] public string? ConnectionString { get; set; }
        [YamlMember(Alias = "auth", ApplyNamingConventions = false)] public AuthDocument? Auth { get; set; }
        [YamlMember(Alias = "timeouts", ApplyNamingConventions = false)] public TimeoutsDocument? Timeouts { get; set; }
        [YamlMember(Alias = "extensions", ApplyNamingConventions = false)] public ExtensionsDocument? Extensions { get; set; }
    }

    private sealed class AddressDocument
    {
        [YamlMember(Alias = "host", ApplyNamingConventions = false)] public string? Host { get; set; }
        [YamlMember(Alias = "port", ApplyNamingConventions = false)] public int? Port { get; set; }
        [YamlMember(Alias = "database", ApplyNamingConventions = false)] public string? Database { get; set; }
    }

    private sealed class AuthDocument
    {
        [YamlMember(Alias = "mode", ApplyNamingConventions = false)] public string? Mode { get; set; }
        [YamlMember(Alias = "user", ApplyNamingConventions = false)] public string? User { get; set; }
        [YamlMember(Alias = "password_secret", ApplyNamingConventions = false)] public string? PasswordSecret { get; set; }
    }

    private sealed class TimeoutsDocument
    {
        [YamlMember(Alias = "connect_s", ApplyNamingConventions = false)] public int? ConnectS { get; set; }
        [YamlMember(Alias = "command_s", ApplyNamingConventions = false)] public int? CommandS { get; set; }
    }

    private sealed class ExtensionsDocument
    {
        [YamlMember(Alias = "sync", ApplyNamingConventions = false)] public SyncConnectionExtension? Sync { get; set; }
    }
}
