using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// Apache Arrow Flight SQL, as a source — phase 197F.
/// <para>
/// A source only, like <c>DuckDbDriver</c>: one reader (a full reload of a table or query), no staging
/// provider and no writer. The usual deployment is a SQL proxy that exposes some other engine over the
/// Flight SQL protocol, and what this driver is for is reading from it.
/// </para>
/// <para>
/// **One instance per SQL dialect** (<see cref="Variants"/>). The protocol carries SQL text to whatever
/// engine the server fronts, so how to quote an identifier, limit rows or spell a literal is a property of
/// *that* engine — and readers are built with their dialect once, when the driver is, so the dialect
/// cannot vary per connection. Each variant is its own driver id (<c>ArrowFlightSqlPostgres</c>, ...) that
/// the connection editor lists as "Arrow Flight SQL (PostgreSQL syntax)"; the plain
/// <see cref="DriverIds.ArrowFlightSql"/> speaks ANSI SQL.
/// </para>
/// <para>
/// Authentication is basic (a user id and password, exchanged for a bearer token by the Flight handshake)
/// or bearer (a pre-issued token). Both store the secret in the secret store like any password — see
/// <see cref="FlightSqlConnection"/> — and are chosen with the <c>authMethod</c> setting.
/// </para>
/// </summary>
public sealed class ArrowFlightSqlDriver : IDriver, IConnectionTester, IDialectProvider
{
    /// <summary>The dialect each driver id speaks: the id, the name an operator sees, the dialect it
    /// borrows, and that dialect's identifier quote characters.</summary>
    public sealed record Variant(string DriverType, string DisplayName, SqlDialect Inner, char QuoteOpen = '"', char QuoteClose = '"');

    public static IReadOnlyList<Variant> Variants { get; } =
    [
        new(DriverIds.ArrowFlightSql, "Arrow Flight SQL", AnsiDialect.Instance),
        new(DriverIds.ArrowFlightSqlMsSql, "Arrow Flight SQL (SQL Server syntax)", MsSqlDialect.Instance, '[', ']'),
        new(DriverIds.ArrowFlightSqlPostgres, "Arrow Flight SQL (PostgreSQL syntax)", PostgresDialect.Instance),
        new(DriverIds.ArrowFlightSqlMySql, "Arrow Flight SQL (MySQL syntax)", MySqlDialect.Instance, '`', '`'),
        new(DriverIds.ArrowFlightSqlOracle, "Arrow Flight SQL (Oracle syntax)", OracleDialect.Instance),
        new(DriverIds.ArrowFlightSqlDuckDb, "Arrow Flight SQL (DuckDB syntax)", DuckDbDialect.Instance),
    ];

    /// <summary>One driver per variant, in the order of <see cref="Variants"/>. What a composition root
    /// registers.</summary>
    public static IReadOnlyList<ArrowFlightSqlDriver> CreateAll() => [.. Variants.Select(v => new ArrowFlightSqlDriver(v))];

    private readonly Variant _variant;

    public ArrowFlightSqlDriver() : this(Variants[0]) { }

    public ArrowFlightSqlDriver(Variant variant)
    {
        _variant = variant;
        Dialect = new FlightSqlDialect(variant.Inner, variant.QuoteOpen, variant.QuoteClose);
        Readers = [new BatchReloadReader(Dialect, FlightSqlValueBinding.Instance)];
    }

    public string DriverType => _variant.DriverType;
    public string DisplayName => _variant.DisplayName;
    public SqlDialect Dialect { get; }

    /// <summary>Null: Flight SQL has no conventional port (Dremio uses 32010, others differ), and a
    /// pre-filled wrong one is worse than a blank.</summary>
    public int? DefaultPort => null;

    public IReadOnlyList<IChangeReader> Readers { get; }
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
    public IReadOnlyList<IChangeWriter> Writers { get; } = [];

    // ---- Connection form ----

    private const string Auth = "Authentication";
    private const string Tls = "Security";

    /// <summary>
    /// The shared host/port/auth set, reshaped for Flight SQL: authentication is basic or bearer rather
    /// than SQL Auth/Integrated, user id only shows for basic, the password field is labelled for what
    /// it holds, and TLS, certificate validation and parameter handling are added. The driver-specific
    /// settings are named <c>properties.&lt;key&gt;</c> because the persisted connection has no other
    /// place for them — the same bag the free-form custom properties live in.
    /// </summary>
    public IReadOnlyList<ParameterDescriptor> ConnectionParameters(IReadOnlyDictionary<string, string> values)
    {
        var sqlAuth = Value(values, DriverParameters.AuthMode, nameof(AuthMode.SqlAuth)) == nameof(AuthMode.SqlAuth);
        var bearer = Value(values, Key(FlightSqlSettings.AuthMethodKey), nameof(FlightSqlAuthMethod.Basic))
            .Equals(nameof(FlightSqlAuthMethod.Bearer), StringComparison.OrdinalIgnoreCase);
        var tls = !Value(values, Key(FlightSqlSettings.UseTlsKey), "true").Equals("false", StringComparison.OrdinalIgnoreCase);

        var shared = DriverParameters.ForConnection(values, DefaultPort);
        var result = new List<ParameterDescriptor>();

        foreach (var parameter in shared)
        {
            switch (parameter.Name)
            {
                case DriverParameters.Properties:
                    // Flight SQL's own settings go before the free-form bag, which stays last.
                    result.Add(new ParameterDescriptor
                    {
                        Name = Key(FlightSqlSettings.ParameterModeKey),
                        Label = "Parameters",
                        Description =
                            "Prepared: values are bound, never part of the SQL text. Inline: values are written " +
                            "into the statement — only for a server or proxy that does not implement prepared statements.",
                        Type = ParameterType.Dropdown,
                        DropdownOptions = [nameof(FlightSqlParameterMode.Prepared), nameof(FlightSqlParameterMode.Inline)],
                        DropdownLabels = new Dictionary<string, string>
                        {
                            [nameof(FlightSqlParameterMode.Prepared)] = "Prepared statement (recommended)",
                            [nameof(FlightSqlParameterMode.Inline)] = "Inline literals",
                        },
                        Default = nameof(FlightSqlParameterMode.Prepared),
                        Layout = new ParameterLayout("Driver settings"),
                    });
                    result.Add(new ParameterDescriptor
                    {
                        Name = Key(FlightSqlSettings.DataSourceKey),
                        Label = "Data source",
                        Description =
                            "Optional. Sent as the x-datasource header, for a proxy that fronts several databases. " +
                            "Leave empty to use the user's default.",
                        Type = ParameterType.Text,
                        Layout = new ParameterLayout("Driver settings"),
                    });
                    result.Add(new ParameterDescriptor
                    {
                        Name = Key(FlightSqlSettings.CompressionKey),
                        Label = "Compression",
                        Description =
                            "Optional. Sent as the x-compression header (none, gzip, zstd or lz4) to override a proxy's " +
                            "default. Leave empty to take the server's choice.",
                        Type = ParameterType.Text,
                        Layout = new ParameterLayout("Driver settings"),
                    });
                    result.Add(parameter);
                    break;

                case DriverParameters.Database:
                    parameter.Label = "Catalog";
                    parameter.Description =
                        "Optional. The catalog metadata lookups browse. Not sent as a statement — Flight SQL has no way to switch databases.";
                    result.Add(parameter);
                    break;

                case DriverParameters.AuthMode:
                    parameter.DropdownOptions = [nameof(AuthMode.SqlAuth), nameof(AuthMode.None)];
                    parameter.DropdownLabels = new Dictionary<string, string>
                    {
                        [nameof(AuthMode.SqlAuth)] = "Basic or bearer token",
                        [nameof(AuthMode.None)] = "None — an open server",
                    };
                    result.Add(parameter);
                    result.Add(new ParameterDescriptor
                    {
                        Name = Key(FlightSqlSettings.AuthMethodKey),
                        Label = "Method",
                        Type = ParameterType.Dropdown,
                        DropdownOptions = [nameof(FlightSqlAuthMethod.Basic), nameof(FlightSqlAuthMethod.Bearer)],
                        DropdownLabels = new Dictionary<string, string>
                        {
                            [nameof(FlightSqlAuthMethod.Basic)] = "Basic — user id and password",
                            [nameof(FlightSqlAuthMethod.Bearer)] = "Bearer — a token",
                        },
                        Default = nameof(FlightSqlAuthMethod.Basic),
                        Recalc = true,
                        Visible = sqlAuth,
                        Layout = new ParameterLayout(Auth),
                    });
                    break;

                case DriverParameters.UserId:
                    parameter.Visible = sqlAuth && !bearer;
                    result.Add(parameter);
                    break;

                case DriverParameters.Password:
                    parameter.Label = bearer ? "Bearer token" : "Password";
                    parameter.Description = bearer
                        ? "Sent as 'Authorization: Bearer'. Kept in the secret store, never in the committed config."
                        : null;
                    result.Add(parameter);
                    break;

                case DriverParameters.ConnectTimeoutSeconds:
                    result.Add(new ParameterDescriptor
                    {
                        Name = Key(FlightSqlSettings.UseTlsKey),
                        Label = "Use TLS",
                        Type = ParameterType.Bool,
                        Default = "true",
                        Recalc = true,
                        Layout = new ParameterLayout(Tls),
                    });
                    result.Add(new ParameterDescriptor
                    {
                        Name = Key(FlightSqlSettings.SkipCertificateValidationKey),
                        Label = "Skip certificate validation",
                        Description =
                            "Accepts any server certificate — expired, self-signed, wrong host, unknown authority. " +
                            "The connection is encrypted but the server's identity is not checked, so anyone able " +
                            "to intercept the traffic can read the credential. Use only on a network you trust.",
                        Type = ParameterType.Bool,
                        Default = "false",
                        Visible = tls,
                        Layout = new ParameterLayout(Tls),
                    });
                    result.Add(parameter);
                    break;

                default:
                    result.Add(parameter);
                    break;
            }
        }

        return result;
    }

    private static string Key(string name) => $"{DriverParameters.Properties}.{name}";

    private static string Value(IReadOnlyDictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    // ---- Connections ----

    /// <summary>The returned connection is not open; the caller opens it, which is where authentication
    /// happens.</summary>
    public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
        new FlightSqlConnection(FlightSqlSettings.From(connection, credential), Dialect).WithCommandTimeout(connection);

    // ---- Introspection ----

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await FlightSqlMetadata.CatalogsAsync(Flight(connection), cancellationToken);

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) =>
        await FlightSqlMetadata.TablesAsync(Flight(connection), database, cancellationToken);

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        await FlightSqlMetadata.ColumnsAsync(Flight(connection), database, schema, table, cancellationToken);

    private static FlightSqlConnection Flight(DbConnection connection) =>
        connection as FlightSqlConnection
        ?? throw new InvalidOperationException($"Expected a {nameof(FlightSqlConnection)}, got {connection.GetType().Name}.");

    // ---- Connection test ----

    /// <summary>
    /// Round-trips <c>SELECT 1</c>, which proves the address, TLS and credentials together — the failures
    /// an operator actually hits. A server that wants a <c>FROM</c> clause on every query is the one case
    /// it fails on, which is what the connection's own test-query override is for.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = DefaultTestQuery;
            await cmd.ExecuteScalarAsync(cancellationToken);

            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), connection.ServerVersion, null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, DriverFailures.Describe(ex, DriverIds.ArrowFlightSql));
        }
    }

    /// <summary>Oracle has no FROM-less SELECT; everything else does.</summary>
    public string DefaultTestQuery => _variant.Inner is OracleDialect ? "SELECT 1 FROM DUAL" : "SELECT 1";
}
