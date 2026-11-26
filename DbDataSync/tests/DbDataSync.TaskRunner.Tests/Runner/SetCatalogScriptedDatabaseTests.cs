using System.Data.Common;
using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Provisioning;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.TaskRunner.Runner;
using DbDataSync.TestSupport;
using LibGit2Sharp;
using Microsoft.Data.SqlClient;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// The set's <c>discover</c> over a SQL Server connection whose <c>metadataProvider</c> script reads the database it is asked for (through the driver's catalog, which switches to that database): the
/// script is asked for the database the connection file names (<c>address.database</c>), so it reads that database's tables even when the login opens another one (here <c>master</c>). A script
/// compiles against a small set of framework assemblies, without <c>System.Data.Common</c>, so it reads through the driver's delegates rather than with a command of its own. Needs the SQL Server of <c>testenv.sh env dbdatakit</c>
/// (<c>DBDATAKIT_TEST_SQLSERVER</c>), or <c>DBDATAKIT_TEST_MSSQL</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SetCatalogScriptedDatabaseTests : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-scripted-db-").FullName;

    private static string SqlServer => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER") ?? TestEngines.MsSql.ConnectionString;

    private string Sales => $"Sales_{_suffix}";

    public async Task InitializeAsync()
    {
        await using var server = new SqlConnection(SqlServer);
        await server.OpenAsync();
        await ExecAsync(server, $"CREATE DATABASE [{Sales}];");
        await using var db = new SqlConnection(new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = Sales }.ConnectionString);
        await db.OpenAsync();
        await ExecAsync(db, "CREATE TABLE dbo.orders (id INT NOT NULL PRIMARY KEY, amount DECIMAL(10, 2) NULL); CREATE TABLE dbo.customers (id INT NOT NULL PRIMARY KEY);");
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        try
        {
            await using var server = new SqlConnection(SqlServer);
            await server.OpenAsync();
            await MsSqlScratch.DropDatabaseAsync(server, Sales);
        }
        catch (DbException) { /* a scratch database left behind carries a GUID in its name */ }
        GitTempDirectory.DeleteRecursively(_root);
    }

    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Discover_AsksABoundScriptForTheDatabaseTheConnectionFileNames()
    {
        Repository.Init(_root);
        var config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.ForProviders([new InMemorySecretProvider()]));
        var author = new GitAuthor("t", "t@t");
        config.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig { Name = "by-database", Kind = ScriptSlots.MetadataProvider },
            Code = """
                using System.Collections.Generic;
                using System.Threading;
                using System.Threading.Tasks;
                using DbDataSync.Drivers.Abstractions;
                using DbDataSync.Scripting.Abstractions;

                public sealed class ByDatabase : IMetadataProvider
                {
                    public Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext c, CancellationToken ct) => c.DriverDatabases(ct);

                    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext c, string database, CancellationToken ct) => c.DriverTables(database, ct);

                    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(MetadataContext c, string database, string schema, string table, CancellationToken ct) =>
                        c.DriverColumns(database, schema, table, ct);
                }

                new ByDatabase()
                """,
        }, author);
        var input = new ConnectionInput { Name = "erp", DriverType = DriverIds.MsSql, Host = "sql01", Port = 1433, Database = Sales, AuthMode = AuthMode.IntegratedAuth };
        input.Scripts[ScriptSlots.MetadataProvider] = new ScriptBinding { ScriptName = "by-database" };
        config.SaveConnection(input, author);

        // the login opens master: only the database the script is asked for leads it to the tables of Sales
        var master = new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = "master" }.ConnectionString;
        var engine = EngineComposition.Compose(config, _root, new ScriptCacheDirectory(Path.Combine(_root, ".cache")));
        var read = new SetCatalogRead(config, new OneLogin(master), new ScriptedMetadata(engine.Scripts, config));

        var answer = await read.DiscoverAsync("erp", null, null, null, CancellationToken.None);

        Assert.Equal([("dbo", "customers"), ("dbo", "orders")], answer.Tables.Select(t => (t.Schema, t.Name)).Order());
        Assert.All(answer.Tables, t => Assert.Equal(Sales, t.Database));
        Assert.Equal(["id", "amount"], answer.Tables.Single(t => t.Name == "orders").Columns.Select(c => c.Name));
        Assert.Equal([Sales], answer.Databases.Select(d => d.Name));
    }

    private sealed class OneLogin(string connectionString) : IConnectionFactory
    {
        public async Task<(DbConnection Connection, IDriver Driver)> OpenAsync(string connectionName, CredentialRole role, CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return (connection, new MsSqlDriver());
        }
    }
}
