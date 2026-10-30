using DbDataKit.Odbc.Metadata;
using System.Data.Common;
using DbDataBuild.Core;
using DbDataKit.Connections;
using DbDataBuild.Models;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Npgsql;
using DbDataKit.Secrets;

namespace DbDataBuild.Execution;

public enum Login { Read, Write }

/// <summary>A connection string found in the environment. Never printed whole: <see cref="Describe"/> shows the variable and the user only.</summary>
public sealed class LoginSettings
{
    static LoginSettings() => DriverSettings.Apply();

    /// <summary>The connection this login is for (its name: `warehouse`, or `sqlserver` for a connection named after its engine).</summary>
    public string Connection { get; }

    /// <summary>The driver of that connection (sqlserver, fabric, postgres, duckdb): what decides how it is opened and, for the three engines, the SQL.</summary>
    public string Engine { get; }

    /// <summary>The connection is `readonly`: there is no write login for it (the overload that takes the connection refuses to make one).</summary>
    public bool ReadOnly { get; private init; }

    /// <summary>The SQL dialect of the connection: how a parameter is written and a name quoted when this login is read from as an origin.</summary>
    public DialectProfile Dialect { get; private init; } = DialectProfiles.Ansi;
    public Login Login { get; }
    public string Variable { get; }

    /// <summary>
    /// How the connection is secured, from the connection file's `tls:` block; null is the shared default (encrypt, verify the certificate). It is applied over what the login's string says, so a string that turns encryption off or
    /// trusts any certificate no longer does so by itself (plan 13, B02): the connection file says it, where a reader of the project can see it.
    /// </summary>
    public TlsSpec? Tls { get; private init; }

    /// <summary>For tests of a harness whose engines have self-signed certificates: the TLS settings of a connection that has no `tls:` block. Null (always, in the product) is the shared default.</summary>
    internal static TlsSpec? DefaultTls { get; set; }
    internal string ConnectionString { get; }

    private LoginSettings(string connection, string engine, Login login, string variable, string connectionString)
    {
        Connection = connection; Engine = engine; Login = login; Variable = variable; ConnectionString = connectionString;
        Dialect = DialectProfiles.OfEngine(engine) ?? DialectProfiles.Ansi;
    }

    /// <summary>`DBDATA_BUILD_SQLSERVER_READ`, `DBDATA_BUILD_WAREHOUSE_WRITE` and so on: the connection's name in capitals, anything but a letter or digit as an underscore (the rule every product shares).</summary>
    public static string VariableName(string connection, Login login) =>
        LoginEnvironment.Name("build", connection, login == Login.Read ? LoginRole.Read : LoginRole.Write);

    /// <summary>Reads the login from the environment. There is deliberately no fallback from one login to the other.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, string engine, Login login, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var variable = VariableName(connection, login);
        var value = env(variable);
        if (string.IsNullOrWhiteSpace(value))
            return (null, new Diagnostic(DiagnosticCatalog.LoginNotConfigured, new($"env:{variable}", 0, 0),
                $"The {login.ToString().ToLowerInvariant()} login for connection `{connection}` is not configured: environment variable `{variable}` is not set."));
        return (new LoginSettings(connection, engine, login, variable, value), null);
    }

    /// <summary>
    /// Reads the login of a connection of the project. A `readonly` connection has no write login: asking for one is DDB-455 and the environment is not looked at. <paramref name="dialect"/> is the profile of the
    /// connection when it is a project's own (the three engines, DuckDB and `ansi` are known without it).
    /// </summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(ConnectionConfig connection, Login login, Func<string, string?>? env = null, DialectProfile? dialect = null)
    {
        if (connection.ReadOnly && login == Login.Write)
            return (null, new Diagnostic(DiagnosticCatalog.ReadOnlyConnectionNotWritable, new($"connection:{connection.Name}", 0, 0),
                $"The connection `{connection.Name}` is `readonly` ({connection.Kind.Describe()}): it has no write login and the tool never writes there."));
        var (settings, error) = FromEnvironment(connection.Name, connection.Engine, login, env);
        if (settings == null) return (null, error);
        var kind = connection.Kind;
        return (settings.With(kind.ReadOnly, dialect ?? connection.Profile ?? DialectProfiles.BuiltIn.GetValueOrDefault(kind.Dialect) ?? DialectProfiles.Ansi, connection.Tls), null);
    }

    private static readonly HashSet<string> AddressKeys = new(StringComparer.OrdinalIgnoreCase)
        { "server", "data source", "host", "address", "addr", "network address", "port", "database", "initial catalog", "dbname" };

    /// <summary>
    /// The connection as lineage names a table's home (`LineageIdentity`): the engine and only the address keys of the connection string (server, port, database). The login's user and password are left out, so nothing secret is held
    /// by what the identity is worked out from.
    /// </summary>
    public ConnectionDefinition AddressOnlyDefinition() => new()
    {
        Name = Connection, Engine = Engine,
        ConnectionString = string.Join(';', ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Split('=', 2) is [var key, _] && AddressKeys.Contains(key.Trim()))),
    };

    private LoginSettings With(bool readOnly, DialectProfile dialect, TlsSpec? tls) => new(Connection, Engine, Login, Variable, ConnectionString) { ReadOnly = readOnly, Dialect = dialect, Tls = tls };

    /// <summary>Reads the read or write login of a connection by name: the project's own connection (with its dialect) when it has one, else the connection named after an engine.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(ProjectConfig project, string connection, Login login, Func<string, string?>? env = null) =>
        project.Connections.TryGetValue(connection, out var declared) ? FromEnvironment(declared, login, env, project.Dialects.GetValueOrDefault(declared.Kind.Dialect))
            : FromEnvironment(connection, project.EngineOf(connection) ?? connection, login, env);

    /// <summary>For a connection named after its engine (`sqlserver`, `postgres`, `fabric`), which needs no declaration.</summary>
    public static (LoginSettings? Settings, Diagnostic? Error) FromEnvironment(string connection, Login login, Func<string, string?>? env = null) =>
        FromEnvironment(connection, connection, login, env);

    /// <summary>For the command header (DESIGN.md 9.1): which variable and which user, never the password or the rest of the string.</summary>
    public string Describe() => $"{(Engine == "duckdb" ? "DuckDB file, read-only" : Engine == "odbc" ? $"ODBC, {User ?? "the driver's own credentials"}" : Engine == "flightsql" ? FlightSqlDescription() : User ?? "integrated/default")} ({Variable})";

    /// <summary>Where the login goes, when it is a network address that is safe to show (`host:port` of a Flight SQL login); null otherwise, and when the value cannot be read.</summary>
    public string? Target
    {
        get
        {
            if (Engine != "flightsql") return null;
            try { return FlightSqlLogin.Parse(ConnectionString).HostAndPort; }
            catch (FlightSqlLoginException) { return null; }
        }
    }

    // the host and port and how the login signs in (a user's name, or the kind of token); the value itself is never shown
    private string FlightSqlDescription()
    {
        try { var login = FlightSqlLogin.Parse(ConnectionString); return $"Flight SQL {login.HostAndPort}, {login.AuthenticationDescription}"; }
        catch (FlightSqlLoginException) { return "Flight SQL (the login value is not a valid Flight SQL URI)"; }
    }

    public string? User
    {
        get
        {
            try
            {
                if (Engine == "duckdb") return null;                              // a file has no user
                if (Engine == "odbc") return OdbcLogin.UserOf(ConnectionString);
                if (Engine == "flightsql") return FlightSqlLogin.UserOf(ConnectionString);
                if (Engine == "postgres") return new NpgsqlConnectionStringBuilder(ConnectionString).Username;
                var b = new SqlConnectionStringBuilder(ConnectionString);
                return string.IsNullOrEmpty(b.UserID) ? null : b.UserID;
            }
            catch (ArgumentException) { return null; }
        }
    }

    /// <summary>Opens the read side of this login as a row source. Internal on purpose: the way to it is <see cref="ReadSession"/>.</summary>
    internal async Task<IRowSource> OpenRowSourceAsync(CancellationToken ct)
    {
        if (Engine == "flightsql")
        {
            if (Login == Login.Write) throw new InvalidOperationException("A Flight SQL connection is only read.");
            var flight = await FlightSqlRowSource.ConnectAsync(ConnectionString, ct);
            flight.Dialect = Dialect;
            return flight;
        }
        var connection = await OpenAsync(ct);
        return Engine switch
        {
            "duckdb" => new DuckDbRowSource(connection, Dialect.Parameters),
            "odbc" => new OdbcRowSource((System.Data.Odbc.OdbcConnection)connection, Dialect.Parameters, ConnectionString, Dialect),
            _ => new AdoRowSource(connection, Dialect.Parameters),
        };
    }

    /// <summary>Opens a connection of the right provider. Internal on purpose: the write side is reached only through <see cref="MutationGate"/>, the read side through <see cref="ReadSession"/>.</summary>
    internal async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        if (Engine == "duckdb") return await OpenDuckDbAsync(ct);
        if (Engine == "odbc") return await OpenOdbcAsync(ct);
        // The connection string is composed by the engine's shared provider: the login's string with the connection file's TLS settings over it, and for a read login the session-level read-only recipe (a second layer; the login's
        // permissions are the real one). The provider makes the connection; it is opened here so a failure reaches the tool's own failure sentences as the driver raised it.
        var (provider, role, context, login) = Prepare();
        using var _ = login;
        var c = provider.Create(context);
        try
        {
            await c.OpenAsync(ct);
            if (Login == Login.Read) await provider.ReadOnly.ApplyAsync(c, ct);
        }
        catch { await c.DisposeAsync(); throw; }
        return c;
    }

    private (IDbProvider Provider, LoginRole Role, ConnectionContext Context, ResolvedLogin Login) Prepare()
    {
        IDbProvider provider = Engine == "postgres" ? Postgres : SqlServer;
        var role = Login == Login.Read ? LoginRole.Read : LoginRole.Write;
        var definition = new ConnectionDefinition
        {
            Name = Connection, Engine = Engine == "postgres" ? "postgres" : "sqlserver", Access = ReadOnly ? AccessMode.Readonly : AccessMode.Readwrite,
            Tls = Tls ?? DefaultTls ?? new TlsSpec(),
            Properties = Engine == "postgres" ? [] : new() { ["Application Name"] = Login == Login.Read ? $"{ProductInfo.Cli}-read" : $"{ProductInfo.Cli}-write" },
        };
        var login = new ResolvedLogin(role, AuthMode.Sql, "env:" + Variable) { ConnectionString = SecretValue.From(ConnectionString) };
        return (provider, role, new ConnectionContext { Definition = definition, Login = login, Role = role }, login);
    }

    /// <summary>The connection string the provider composes for this login (for a test of what TLS settings reach the driver). A secret: never logged.</summary>
    internal string ComposedConnectionString()
    {
        var (provider, _, context, login) = Prepare();
        using var _l = login;
        return provider.Composer.Compose(context);
    }

    private static readonly IDbProvider SqlServer = new DbDataKit.Connections.SqlServer.SqlServerProvider();
    private static readonly IDbProvider Postgres = new DbDataKit.Connections.Postgres.PostgresProvider();

    /// <summary>
    /// An ODBC origin: the login's value goes to the driver manager as written (unixODBC on Linux and macOS, the system's on Windows) and the vendor's driver it names; the tool ships neither. There is no write side to an ODBC
    /// connection. A missing driver manager or driver surfaces here as an exception that <see cref="Failures"/> turns into a sentence that says which is missing.
    /// </summary>
    private async Task<DbConnection> OpenOdbcAsync(CancellationToken ct)
    {
        if (Login == Login.Write) throw new InvalidOperationException("An ODBC connection is only read.");
        var c = new System.Data.Odbc.OdbcConnection(OdbcConnectionString.Normalize(ConnectionString)) { ConnectionTimeout = 30 };
        try { await c.OpenAsync(ct); }
        catch { await c.DisposeAsync(); throw; }
        return c;
    }

    /// <summary>
    /// A DuckDB origin: the login's value is the **path of a .duckdb file**, opened read-only (`access_mode=READ_ONLY`: a file that is not there is an error, never created, and nothing can be written to it), or `:memory:`, a
    /// database with no file, for a native query that reads a Parquet or CSV file. There is no write side to a DuckDB connection.
    /// </summary>
    private async Task<DbConnection> OpenDuckDbAsync(CancellationToken ct)
    {
        if (Login == Login.Write) throw new InvalidOperationException("A DuckDB connection is only read.");
        var path = ConnectionString.Trim();
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        if (!string.Equals(path, ":memory:", StringComparison.Ordinal)) builder["access_mode"] = "READ_ONLY";
        var c = new DuckDBConnection(builder.ConnectionString);
        try { await c.OpenAsync(ct); }
        catch { await c.DisposeAsync(); throw; }
        return c;
    }
}
