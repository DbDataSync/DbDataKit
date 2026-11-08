using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Connections.DuckDb;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Dialect;
using DbDataKit.Secrets;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Quality.Evaluation.Tests;

/// <summary>
/// One data engine of the conformance suite: SQL Server and PostgreSQL when the shared engines are up (<c>DBDATA_TEST_SQLSERVER</c>, <c>DBDATA_TEST_POSTGRES</c>, <c>testenv/testenv.sh up mssql postgres</c>), DuckDB always (a file in a scratch
/// directory). An engine whose variable is not set skips its rows unless <c>DBDATA_REQUIRE_SQLSERVER</c> / <c>DBDATA_REQUIRE_POSTGRES</c> is <c>true</c>. The checks run over the <b>provider's read-only connection</b>; the data is written over a second, writing one.
/// </summary>
public sealed class EvaluationEngine : IAsyncDisposable
{
    public const string SqlServer = "sqlserver", Postgres = "postgres", DuckDb = "duckdb";

    private readonly string _name;
    private readonly string? _server;
    private readonly string? _duckFile;
    private readonly List<string> _tables = [];
    private readonly List<string> _loginSecrets = [];

    private EvaluationEngine(string name, string? server, string? duckFile)
    {
        _name = name;
        _server = server;
        _duckFile = duckFile;
        Dialect = DialectCatalog.Builtin.Get(name);
    }

    public ISqlDialect Dialect { get; }

    public string Name => _name;

    /// <summary>The default schema of the engine, to test a qualified name.</summary>
    public string DefaultSchema => _name switch { SqlServer => "dbo", Postgres => "public", _ => "main" };

    public static TheoryData<string> All => [SqlServer, Postgres, DuckDb];

    public static EvaluationEngine Open(string name)
    {
        var variable = name switch { SqlServer => "DBDATA_TEST_SQLSERVER", Postgres => "DBDATA_TEST_POSTGRES", _ => null };
        if (variable is null)
        {
            var dir = Directory.CreateTempSubdirectory("dbdatakit-quality-");
            return new EvaluationEngine(name, null, Path.Combine(dir.FullName, "data.duckdb"));
        }
        var server = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(server))
        {
            var required = string.Equals(Environment.GetEnvironmentVariable(name == SqlServer ? "DBDATA_REQUIRE_SQLSERVER" : "DBDATA_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);
            Assert.False(required, $"{name} is required but {variable} is not set");
            Assert.Skip($"set {variable} to run on {name}");
        }
        return new EvaluationEngine(name, server, null);
    }

    /// <summary>A table name no other test uses, so tests share one database.</summary>
    public string Unique(string stem) => $"qchk_{Guid.NewGuid():N}_{stem}"[..Math.Min(60, 38 + stem.Length)];

    public async Task<DbConnection> OpenWriter()
    {
        DbConnection connection = _name switch
        {
            SqlServer => new SqlConnection(_server),
            Postgres => new NpgsqlConnection(_server),
            _ => new DuckDB.NET.Data.DuckDBConnection($"Data Source={_duckFile}"),
        };
        await connection.OpenAsync();
        return connection;
    }

    public async Task Execute(string sql)
    {
        await using var connection = await OpenWriter();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Creates a table (one name for the whole test, dropped at the end) and inserts the rows, written as literals of the engine's own dialect.</summary>
    public async Task Table(string name, IReadOnlyList<(string Column, CanonicalType Type, bool Nullable)> columns, IEnumerable<object?[]> rows)
    {
        var q = Dialect.QuoteQualified([name]);
        var definition = string.Join(", ", columns.Select(c => $"{Dialect.Quote(c.Column)} {Dialect.ColumnType(c.Type).Require()}{(c.Nullable ? " NULL" : " NOT NULL")}"));
        await Execute($"CREATE TABLE {q} ({definition})");
        _tables.Add(name);
        var names = string.Join(", ", columns.Select(c => Dialect.Quote(c.Column)));
        var values = rows.Select(r => "(" + string.Join(", ", r.Select((v, i) => Dialect.Literal(v, columns[i].Type))) + ")").ToList();
        for (var i = 0; i < values.Count; i += 100)
            await Execute($"INSERT INTO {q} ({names}) VALUES {string.Join(", ", values.Skip(i).Take(100))}");
    }

    /// <summary>The definition and login of a connection to this engine as the suite's provider takes them; <paramref name="down"/> points them where nothing answers (a closed port, a file that is not there).</summary>
    public (IDbProvider Provider, ConnectionDefinition Definition, ResolvedLogin? Login) Recipe(bool down = false)
    {
        switch (_name)
        {
            case Postgres:
            {
                var b = new NpgsqlConnectionStringBuilder(_server);
                var definition = new ConnectionDefinition { Name = "pg", Engine = "postgres", Address = new AddressSpec { Host = b.Host, Port = down ? 1 : b.Port, Database = b.Database }, Tls = new TlsSpec { Encrypt = false }, Timeouts = new TimeoutSpec { ConnectS = 3 } };
                return (new PostgresProvider(), definition, new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "test") { UserName = b.Username, Password = SecretValue.From(b.Password ?? "") });
            }
            case SqlServer:
            {
                var b = new SqlConnectionStringBuilder(_server);
                var (host, p) = SplitDataSource(b.DataSource);
                var definition = new ConnectionDefinition { Name = "mssql", Engine = "sqlserver", Address = new AddressSpec { Host = host, Port = down ? 1 : p, Database = b.InitialCatalog.Length == 0 ? null : b.InitialCatalog }, Tls = new TlsSpec { TrustServerCertificate = true }, Timeouts = new TimeoutSpec { ConnectS = 3 } };
                return (new SqlServerProvider(), definition, new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "test") { UserName = b.UserID, Password = SecretValue.From(b.Password) });
            }
            default:
            {
                var definition = new ConnectionDefinition { Name = "duck", Engine = "duckdb", Address = new AddressSpec { Database = down ? _duckFile + ".missing" : _duckFile } };
                return (new DuckDbProvider(), definition, null);
            }
        }
    }

    private static (string Host, int? Port) SplitDataSource(string dataSource)
    {
        var text = dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? dataSource[4..] : dataSource;
        var comma = text.IndexOf(',');
        return comma < 0 ? (text, null) : (text[..comma], int.Parse(text[(comma + 1)..]));
    }

    /// <summary>The read-only opener of the provider: what the evaluator is given.</summary>
    public Func<CancellationToken, Task<DbConnection>> ReadOnlyOpener(bool down = false)
    {
        var (provider, definition, login) = Recipe(down);
        return ReadOnlyConnections.Opener(provider, definition, login);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var table in _tables)
        {
            try { await Execute($"DROP TABLE {Dialect.QuoteQualified([table])}"); }
            catch (Exception) { /* a table left behind has a GUID in its name */ }
        }
        SqlConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        if (_duckFile is not null)
        {
            try { Directory.Delete(Path.GetDirectoryName(_duckFile)!, recursive: true); }
            catch (Exception) { /* the scratch directory is in the temp folder */ }
        }
    }
}
