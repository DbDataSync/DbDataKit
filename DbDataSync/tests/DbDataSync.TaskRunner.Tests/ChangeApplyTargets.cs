using System.Data.Common;
using System.Globalization;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.MySql;
using DbDataSync.Drivers.Oracle;
using DbDataSync.Drivers.Postgres;
using DbDataSync.TestSupport;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>The engines the <c>ChangeApply</c> replication tests (plan 28c, CW01c) write into: where each is, how to log in without TLS (the test engines have none), the DDL of the target table in the engine's own spelling.</summary>
internal static class ChangeApplyTargets
{
    public static string SqlServer => TestEngines.MsSql.ConnectionString;

    public static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    internal abstract class TargetEngine
    {
        public abstract string DriverType { get; }
        public abstract IDriver Driver { get; }
        public abstract string Database { get; }
        public abstract string Schema { get; }
        public abstract ConnectionInput Input(string name);
        public abstract DbConnection Open();
        public abstract string CreateSql(string table);
        public abstract string Q(string id);
        public virtual string Column(string lower) => lower;
        public virtual string Table(string table) => table;
        public virtual Task PrepareAsync() => Task.CompletedTask;
        public virtual async Task DisposeAsync(string table)
        {
            try { await using var c = Open(); await ChangeApplyTargets.ExecuteAsync(c, $"DROP TABLE {Q(Schema)}.{Q(table)}"); }
            catch (DbException) { /* the scratch table of a test that did not get as far as creating it */ }
        }
    }

    internal sealed class PostgresTarget : TargetEngine
    {
        private static string Server => Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER") ?? Environment.GetEnvironmentVariable("DBKIT_TEST_POSTGRES") ?? TestEngines.Postgres.ConnectionString;
        private readonly NpgsqlConnectionStringBuilder _b = new(Server);
        public override string DriverType => DriverIds.Postgres;
        public override IDriver Driver { get; } = new PostgresDriver();
        public override string Database => _b.Database ?? "postgres";
        public override string Schema => "public";
        public override ConnectionInput Input(string name) => new() { Name = name, DriverType = DriverType, Host = _b.Host, Port = _b.Port, Database = Database, AuthMode = AuthMode.SqlAuth, UserId = _b.Username, Password = _b.Password, Properties = new Dictionary<string, string> { ["SSL Mode"] = "Disable" } };
        public override DbConnection Open() { var c = new NpgsqlConnection(_b.ConnectionString); c.Open(); return c; }
        public override string CreateSql(string t) => $"CREATE TABLE public.\"{t}\" (id integer PRIMARY KEY, name varchar(50) NULL, amount numeric(18,2) NULL)";
        public override string Q(string id) => $"\"{id}\"";
    }

    internal sealed class MySqlFamilyTarget(string serverConnectionString) : TargetEngine
    {
        private readonly string _database = $"ca_{Guid.NewGuid():N}";
        private readonly MySqlConnectionStringBuilder _b = new(serverConnectionString);
        public override string DriverType => DriverIds.MySql;
        public override IDriver Driver { get; } = new MySqlDriver();
        public override string Database => _database;
        public override string Schema => _database;
        public override ConnectionInput Input(string connection) => new() { Name = connection, DriverType = DriverType, Host = _b.Server, Port = (int)_b.Port, Database = _database, AuthMode = AuthMode.SqlAuth, UserId = _b.UserID, Password = _b.Password, Properties = new Dictionary<string, string> { ["SslMode"] = "None" } };
        public override DbConnection Open() { var c = new MySqlConnection(new MySqlConnectionStringBuilder(_b.ConnectionString) { Database = _database }.ConnectionString); c.Open(); return c; }
        public override string CreateSql(string t) => $"CREATE TABLE `{t}` (id int PRIMARY KEY, name varchar(50) NULL, amount decimal(18,2) NULL)";
        public override string Q(string id) => $"`{id}`";
        public override async Task PrepareAsync()
        {
            await using var c = new MySqlConnection(_b.ConnectionString);
            await c.OpenAsync();
            await ChangeApplyTargets.ExecuteAsync(c, $"CREATE DATABASE `{_database}`");
        }
        public override async Task DisposeAsync(string table)
        {
            try { await using var c = new MySqlConnection(_b.ConnectionString); await c.OpenAsync(); await ChangeApplyTargets.ExecuteAsync(c, $"DROP DATABASE IF EXISTS `{_database}`"); }
            catch (DbException) { }
        }
    }

    internal sealed class OracleTarget : TargetEngine
    {
        private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
        private readonly string _host = Env("DBKIT_TEST_ORACLE_HOST", "127.0.0.1"), _user = Env("DBKIT_TEST_ORACLE_USER", "dbkit"), _password = Env("DBKIT_TEST_ORACLE_PASSWORD", "DbKit_Test_Pw1"), _service = Env("DBKIT_TEST_ORACLE_SERVICE", "FREEPDB1");
        private readonly int _port = int.Parse(Env("DBKIT_TEST_ORACLE_PORT", "15210"), CultureInfo.InvariantCulture);
        public override string DriverType => DriverIds.Oracle;
        public override IDriver Driver { get; } = new OracleDriver();
        public override string Database => _service;
        public override string Schema => _user.ToUpperInvariant();
        public override string Column(string lower) => lower.ToUpperInvariant();
        public override string Table(string table) => table.ToUpperInvariant();
        public override ConnectionInput Input(string name) => new() { Name = name, DriverType = DriverType, Host = _host, Port = _port, Database = _service, AuthMode = AuthMode.SqlAuth, UserId = _user, Password = _password, Properties = new Dictionary<string, string> { ["Encrypt"] = "false" } };
        public override DbConnection Open() { var c = new OracleConnection($"Data Source={_host}:{_port}/{_service};User Id={_user};Password={_password}"); c.Open(); return c; }
        public override string CreateSql(string t) => $"CREATE TABLE \"{t}\" (\"ID\" NUMBER(10,0) PRIMARY KEY, \"NAME\" VARCHAR2(50) NULL, \"AMOUNT\" NUMBER(18,2) NULL)";
        public override string Q(string id) => $"\"{id}\"";
        public override async Task DisposeAsync(string table)
        {
            try { await using var c = Open(); await ChangeApplyTargets.ExecuteAsync(c, $"DROP TABLE \"{table}\" PURGE"); }
            catch (DbException) { }
        }
    }

    internal sealed class SqlServerTarget : TargetEngine
    {
        public override string DriverType => DriverIds.MsSql;
        public override IDriver Driver { get; } = new MsSqlDriver();
        public override string Database => "ca_target_" + Guid.NewGuid().ToString("N");
        public override string Schema => "dbo";
        public override ConnectionInput Input(string name) => throw new NotSupportedException();
        public override DbConnection Open() => throw new NotSupportedException();
        public override string CreateSql(string t) => throw new NotSupportedException();
        public override string Q(string id) => $"[{id}]";
    }

    public static TargetEngine Make(string engine) => engine switch
    {
        "postgres" => new PostgresTarget(),
        "mysql" => new MySqlFamilyTarget(TestEngines.MySql.ConnectionString),
        "mariadb" => new MySqlFamilyTarget(Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MARIADB_SERVER") ?? "Server=localhost;Port=13307;UserID=root;Password=DbDataSync_Test_Pw1;AllowUserVariables=true;"),
        "oracle" => new OracleTarget(),
        "sqlserver" => new SqlServerTargetInSource(),
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    /// <summary>The SQL Server target is a second database on the same server: the same engine on both sides, the way the default changes for it too.</summary>
    internal sealed class SqlServerTargetInSource : TargetEngine
    {
        private readonly string _database = $"CaTarget_{Guid.NewGuid():N}";
        private bool _created;
        public override string DriverType => DriverIds.MsSql;
        public override IDriver Driver { get; } = new MsSqlDriver();
        public override string Database => _database;
        public override string Schema => "dbo";
        public override ConnectionInput Input(string name) => new()
        {
            Name = name, DriverType = DriverType, Host = "localhost", Port = TestEngines.MsSql.Port, Database = _database, AuthMode = AuthMode.SqlAuth, UserId = TestEngines.MsSql.User, Password = TestEngines.MsSql.Password,
            Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true" },
        };
        public override DbConnection Open() { var c = new SqlConnection(new SqlConnectionStringBuilder(ChangeApplyTargets.SqlServer) { InitialCatalog = _database }.ConnectionString); c.Open(); return c; }
        public override string CreateSql(string t) => $"CREATE TABLE dbo.[{t}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NULL, amount DECIMAL(18,2) NULL)";
        public override string Q(string id) => $"[{id}]";
        public override async Task PrepareAsync()
        {
            await using var c = new SqlConnection(ChangeApplyTargets.SqlServer);
            await c.OpenAsync();
            await ChangeApplyTargets.ExecuteAsync(c, $"CREATE DATABASE [{_database}]");
            _created = true;
        }
        public override async Task DisposeAsync(string table)
        {
            if (!_created) return;
            await using var c = new SqlConnection(ChangeApplyTargets.SqlServer);
            await c.OpenAsync();
            await MsSqlScratch.DropDatabaseAsync(c, _database);
        }
    }
}
