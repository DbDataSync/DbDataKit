using System.Data.Common;
using DbDataKit.Runner;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;
using DbDataKit.Sync.Provisioning;
using DbDataSync.TaskRunner.Runner;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;
using DbDataSync.TestSupport;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// The replication set's preflight on SQL Server and PostgreSQL (Design: docs/authoring-metadata.md#a-sync-project): the checks of each mapping with the login a run uses, read only. A source whose
/// change tracking is off is an error with the statement that enables it, and satisfied once it is on; a target login without INSERT is an error, and one without UPDATE is not for a writer that never updates (DeleteInsert); a filter naming a column the table does not have
/// is an error; and every table of both databases, with its row count, is the same after the preflight as before it. Needs the engines of <c>testenv.sh env dbdatakit</c> (SQL Server and the plain
/// PostgreSQL: <c>DBDATAKIT_TEST_SQLSERVER</c>, <c>DBDATAKIT_TEST_POSTGRES</c>), or <c>DBDATAKIT_TEST_MSSQL</c> and the PostgreSQL of <c>testenv.sh env dbdatasync</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SetPreflightConformanceTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private const string LimitedPassword = "Pf_Limited_Pw1!";
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-preflight-").FullName;
    private readonly List<Func<Task>> _cleanups = [];

    private static string SqlServer => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER") ?? TestEngines.MsSql.ConnectionString;

    private static string Postgres => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES") ?? TestEngines.Postgres.ConnectionString;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        foreach (var cleanup in Enumerable.Reverse(_cleanups))
        {
            try { await cleanup(); }
            catch (DbException) { /* a scratch object left behind carries a GUID in its name */ }
        }
        GitTempDirectory.DeleteRecursively(_root);
    }

    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, "config", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Connection(string name, string engine, string host, int port, string database, string user) => Write($"connections/{name}.yaml", $"""
        schema: connection/2
        name: {name}
        engine: {engine}
        address:
          host: {host}
          port: {port}
          database: {database}
        auth:
          mode: sql
          user: {user}
          password_secret: secret:dbdatakit:sync:connection:{name}

        """);

    /// <summary>A replication from <paramref name="source"/> to <paramref name="target"/> with one mapping <c>main</c> of the columns id, name and region.</summary>
    private void Replication(string name, string reader, string writer, string cache, string source, string sourceDatabase, string sourceSchema, string sourceTable,
        string target, string targetDatabase, string targetSchema, string targetTable, string? filter = null, string? tracking = null)
    {
        Write($"replications/{name}/task.yaml", $"""
            schemaVersion: 2
            name: {name}
            scheduling:
              mode: Periodic
              cronExpression: 0 3 * * *
            changeProcessing:
              reader:
                kind: {reader}
              cache:
                kind: {cache}
              writer:
                kind: {writer}
            endpoints:
              source:
                connectionName: {source}
                database: {sourceDatabase}
              target:
                connectionName: {target}
                database: {targetDatabase}
            {tracking ?? ""}
            """);
        Write($"replications/{name}/table-mappings/main.yaml", $"""
            schemaVersion: 1
            name: main
            sources:
            - schema: {sourceSchema}
              table: {sourceTable}
            {(filter is null ? "" : $"  filter: \"{filter}\"\n")}targets:
            - schema: {targetSchema}
              table: {targetTable}
            columnMappings:
            - sourceColumn: id
              targetColumn: id
            - sourceColumn: name
              targetColumn: name
            - sourceColumn: region
              targetColumn: region

            """);
    }

    /// <summary>The preflight of the set in <c>config/</c>, each connection opened with the connection string <paramref name="logins"/> gives it: what the run login of its slot is on a runner.</summary>
    private async Task<IReadOnlyList<SetPreflightPart>> PreflightAsync(IReadOnlyDictionary<string, (string ConnectionString, IDriver Driver)> logins)
    {
        var config = ConfigRepository.ReadOnly(Path.Combine(_root, "config"), SecretStore.ForProviders());
        var drivers = new DriverRegistry();
        foreach (var driver in logins.Values.Select(l => l.Driver).DistinctBy(d => d.DriverType)) drivers.Register(driver);
        var connections = new Logins(logins);
        var provisioner = new MappingProvisioner(config, connections, new NothingAfterApply(), new MappingColumnReader(new Catalog(connections)), drivers);
        return await new SetPreflight(config, connections, provisioner).CheckAsync(null, CancellationToken.None);
    }

    private static IReadOnlyList<PreflightFinding> Findings(IReadOnlyList<SetPreflightPart> parts, string part) => Assert.Single(parts, p => p.Part == part).Findings;

    private static string Show(IEnumerable<SetPreflightPart> parts) =>
        string.Join("\n", parts.SelectMany(p => p.Findings.Select(f => $"{p.Part} [{f.Severity}] {f.About}: {f.Message}")));

    /// <summary>Every user table of the database and its row count: what must be the same after a preflight.</summary>
    private static async Task<List<string>> TablesAsync(DbConnection connection, string listSql)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = listSql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        var counted = new List<string>();
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table}";
            counted.Add($"{table}={await count.ExecuteScalarAsync()}");
        }
        return counted;
    }

    // ---- SQL Server

    [Fact]
    public async Task OnSqlServer_ChangeTrackingOff_ALoginWithoutInsert_AndAFilterOnAMissingColumn_AreFindings_AndNothingIsWritten()
    {
        var builder = new SqlConnectionStringBuilder(SqlServer);
        var (host, port) = (builder.DataSource.Split(',')[0], int.Parse(builder.DataSource.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture));
        var source = $"PfSrc_{_suffix}";
        var target = $"PfTgt_{_suffix}";
        var login = $"pf_limited_{_suffix}";
        var writer = $"pf_writer_{_suffix}";
        await using (var server = new SqlConnection(SqlServer))
        {
            await server.OpenAsync();
            await ExecAsync(server, $"CREATE DATABASE [{source}]; ALTER DATABASE [{source}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF); CREATE DATABASE [{target}];");
            await ExecAsync(server, $"CREATE LOGIN [{login}] WITH PASSWORD = '{LimitedPassword}', CHECK_POLICY = OFF;");
            await ExecAsync(server, $"CREATE LOGIN [{writer}] WITH PASSWORD = '{LimitedPassword}', CHECK_POLICY = OFF;");
        }
        _cleanups.Add(async () =>
        {
            SqlConnection.ClearAllPools();
            await using var server = new SqlConnection(SqlServer);
            await server.OpenAsync();
            await MsSqlScratch.DropDatabaseAsync(server, source);
            await MsSqlScratch.DropDatabaseAsync(server, target);
            await ExecAsync(server, $"IF SUSER_ID('{login}') IS NOT NULL DROP LOGIN [{login}];");
            await ExecAsync(server, $"IF SUSER_ID('{writer}') IS NOT NULL DROP LOGIN [{writer}];");
        });

        var sourceAdmin = new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = source }.ConnectionString;
        var targetAdmin = new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = target }.ConnectionString;
        await using (var db = new SqlConnection(sourceAdmin))
        {
            await db.OpenAsync();
            await ExecAsync(db, "CREATE TABLE dbo.orders (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NOT NULL, region INT NOT NULL); INSERT INTO dbo.orders VALUES (1, N'a', 1), (2, N'b', 2);");
        }
        await using (var db = new SqlConnection(targetAdmin))
        {
            await db.OpenAsync();
            await ExecAsync(db, "CREATE TABLE dbo.orders (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NOT NULL, region INT NOT NULL); INSERT INTO dbo.orders VALUES (1, N'a', 1);");
            await ExecAsync(db, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT ON dbo.orders TO [{login}];");
            await ExecAsync(db, $"CREATE USER [{writer}] FOR LOGIN [{writer}]; GRANT SELECT, INSERT, DELETE ON dbo.orders TO [{writer}];");
        }

        Connection("src", "sqlserver", host, port, source, builder.UserID);
        Connection("tgt", "sqlserver", host, port, target, builder.UserID);
        Connection("tgt_limited", "sqlserver", host, port, target, login);
        Connection("tgt_writer", "sqlserver", host, port, target, writer);
        Replication("ct", MsSqlDriverKinds.ChangeTracking, MsSqlDriverKinds.Merge, MsSqlDriverKinds.StagingTable, "src", source, "dbo", "orders", "tgt", target, "dbo", "orders", filter: "region > 0");
        Replication("limited", "BatchReload", "DeleteInsert", "StagingTable", "src", source, "dbo", "orders", "tgt_limited", target, "dbo", "orders");
        Replication("filtered", "BatchReload", "DeleteInsert", "StagingTable", "src", source, "dbo", "orders", "tgt", target, "dbo", "orders", filter: "regoin > 0");
        Replication("refill", "BatchReload", MsSqlDriverKinds.DeleteInsert, "StagingTable", "src", source, "dbo", "orders", "tgt_writer", target, "dbo", "orders");
        Replication("merge", "BatchReload", MsSqlDriverKinds.Merge, "StagingTable", "src", source, "dbo", "orders", "tgt_writer", target, "dbo", "orders");
        var logins = new Dictionary<string, (string, IDriver)>
        {
            ["src"] = (sourceAdmin, new MsSqlDriver()),
            ["tgt"] = (targetAdmin, new MsSqlDriver()),
            ["tgt_limited"] = (new SqlConnectionStringBuilder(targetAdmin) { UserID = login, Password = LimitedPassword }.ConnectionString, new MsSqlDriver()),
            ["tgt_writer"] = (new SqlConnectionStringBuilder(targetAdmin) { UserID = writer, Password = LimitedPassword }.ConnectionString, new MsSqlDriver()),
        };

        const string List = "SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id";
        async Task<string> SnapshotAsync()
        {
            await using var s = new SqlConnection(sourceAdmin);
            await s.OpenAsync();
            await using var t = new SqlConnection(targetAdmin);
            await t.OpenAsync();
            return $"source: {string.Join(", ", await TablesAsync(s, List))}; target: {string.Join(", ", await TablesAsync(t, List))}";
        }

        var before = await SnapshotAsync();
        var parts = await PreflightAsync(logins);
        var shown = Show(parts);
        output.WriteLine(shown);

        // change tracking is on for the database and off on the table: an error with the statement that enables it
        var ct = Findings(parts, "ct/main");
        var tracking = Assert.Single(ct, f => f.Message.Contains("'MsSqlChangeTracking' needs the source", StringComparison.Ordinal));
        Assert.True(tracking.Severity == Preflight.Error, shown);
        Assert.Contains("ENABLE CHANGE_TRACKING", tracking.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("src", tracking.About);
        Assert.DoesNotContain(ct, f => f.Message.Contains("may not", StringComparison.Ordinal));   // sa may read and write

        // the run login of the target holds SELECT only: INSERT and DELETE, what DeleteInsert does, are each an error naming the writer, and nothing was tried;
        // UPDATE, which DeleteInsert never does, is not asked
        var limited = Findings(parts, "limited/main");
        Assert.Contains(limited, f => f.Severity == Preflight.Error && f.About == "tgt_limited" && f.Message.Contains("may not INSERT into", StringComparison.Ordinal)
            && f.Message.Contains("the writer 'DeleteInsert' does", StringComparison.Ordinal));
        Assert.Contains(limited, f => f.Severity == Preflight.Error && f.Message.Contains("may not DELETE from", StringComparison.Ordinal));
        Assert.DoesNotContain(limited, f => f.Message.Contains("may not UPDATE", StringComparison.Ordinal));

        // a login with INSERT and DELETE and no UPDATE: nothing about privileges for MsSqlDeleteInsert, and UPDATE is an error for a MERGE, which updates
        Assert.DoesNotContain(Findings(parts, "refill/main"), f => f.Message.Contains("may not", StringComparison.Ordinal));
        var merge = Assert.Single(Findings(parts, "merge/main"), f => f.Message.Contains("may not", StringComparison.Ordinal));
        Assert.Equal(Preflight.Error, merge.Severity);
        Assert.Contains("may not UPDATE", merge.Message, StringComparison.Ordinal);
        Assert.Contains("the writer 'MsSqlMerge' does", merge.Message, StringComparison.Ordinal);

        // a filter that names a column the table does not have fails its zero-row read, naming the column
        var filtered = Findings(parts, "filtered/main");
        var filter = Assert.Single(filtered, f => f.Message.StartsWith("The source filter of the mapping 'main'", StringComparison.Ordinal));
        Assert.Equal(Preflight.Error, filter.Severity);
        Assert.Contains("regoin", filter.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Findings(parts, "ct/main"), f => f.Message.StartsWith("The source filter", StringComparison.Ordinal));   // `region > 0` compiles

        // the documents read back as the console reads them
        var documents = parts.Select(p => (System.Text.Json.Nodes.JsonNode?)Preflight.Document(p.Part, p.Findings)).ToList();
        Assert.Contains(Preflight.Findings(documents), f => f.Severity == Preflight.Error && f.Message.Contains("ENABLE CHANGE_TRACKING", StringComparison.OrdinalIgnoreCase));

        // nothing was written: the same tables, with the same rows, on both databases (no staging table, no state table)
        Assert.Equal(before, await SnapshotAsync());

        // change tracking on: the reader's need is satisfied, and no error is left on the mapping
        await using (var db = new SqlConnection(sourceAdmin))
        {
            await db.OpenAsync();
            await ExecAsync(db, "ALTER TABLE dbo.orders ENABLE CHANGE_TRACKING;");
        }
        var again = await PreflightAsync(logins);
        var satisfied = Findings(again, "ct/main");
        Assert.True(satisfied.All(f => f.Severity != Preflight.Error), Show(again));
        Assert.Contains(satisfied, f => f.Severity == Preflight.Info && f.Message.Contains("needs nothing more for the reader 'MsSqlChangeTracking'", StringComparison.Ordinal));
    }

    // ---- PostgreSQL

    [Fact]
    public async Task OnPostgres_ALoginWithoutInsert_AFilterOnAMissingColumn_AndAStateTableItMayNotCreate_AreFindings_AndNothingIsWritten()
    {
        var builder = new NpgsqlConnectionStringBuilder(Postgres);
        var database = $"pf_{_suffix}";
        var role = $"pf_limited_{_suffix}";
        var writer = $"pf_writer_{_suffix}";
        await using (var server = new NpgsqlConnection(Postgres))
        {
            await server.OpenAsync();
            await ExecAsync(server, $"CREATE DATABASE {database}");
            await ExecAsync(server, $"CREATE ROLE {role} LOGIN PASSWORD '{LimitedPassword}'");
            await ExecAsync(server, $"CREATE ROLE {writer} LOGIN PASSWORD '{LimitedPassword}'");
        }
        _cleanups.Add(async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(Postgres);
            await server.OpenAsync();
            await ExecAsync(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            await ExecAsync(server, $"DROP ROLE IF EXISTS {role}");
            await ExecAsync(server, $"DROP ROLE IF EXISTS {writer}");
        });

        var admin = new NpgsqlConnectionStringBuilder(Postgres) { Database = database }.ConnectionString;
        await using (var db = new NpgsqlConnection(admin))
        {
            await db.OpenAsync();
            await ExecAsync(db, """
                CREATE TABLE public.orders (id integer PRIMARY KEY, name text NOT NULL, region integer NOT NULL);
                INSERT INTO public.orders VALUES (1, 'a', 1), (2, 'b', 2);
                CREATE TABLE public.orders_copy (id integer PRIMARY KEY, name text NOT NULL, region integer NOT NULL);
                INSERT INTO public.orders_copy VALUES (1, 'a', 1);
                """);
            await ExecAsync(db, $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE CREATE ON DATABASE {database} FROM PUBLIC; GRANT CONNECT ON DATABASE {database} TO {role}; "
                + $"GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT ON public.orders, public.orders_copy TO {role};");
            await ExecAsync(db, $"GRANT CONNECT ON DATABASE {database} TO {writer}; GRANT USAGE ON SCHEMA public TO {writer}; "
                + $"GRANT SELECT ON public.orders TO {writer}; GRANT SELECT, INSERT, DELETE ON public.orders_copy TO {writer};");
        }

        Connection("pg", "postgres", builder.Host!, builder.Port, database, builder.Username!);
        Connection("pg_limited", "postgres", builder.Host!, builder.Port, database, role);
        Connection("pg_writer", "postgres", builder.Host!, builder.Port, database, writer);
        Replication("refill", "BatchReload", "DeleteInsert", "StagingTable", "pg", database, "public", "orders", "pg_writer", database, "public", "orders_copy");
        Replication("apply", "BatchReload", "ChangeApply", "StagingTable", "pg", database, "public", "orders", "pg_writer", database, "public", "orders_copy");
        Replication("limited", "BatchReload", "DeleteInsert", "StagingTable", "pg", database, "public", "orders", "pg_limited", database, "public", "orders_copy",
            tracking: "tracking:\n  mode: target\n");
        Replication("filtered", "BatchReload", "DeleteInsert", "StagingTable", "pg", database, "public", "orders", "pg", database, "public", "orders_copy", filter: "regoin > 0");
        Replication("clean", "BatchReload", "DeleteInsert", "StagingTable", "pg", database, "public", "orders", "pg", database, "public", "orders_copy", filter: "region > 0");
        var logins = new Dictionary<string, (string, IDriver)>
        {
            ["pg"] = (admin, new PostgresDriver()),
            ["pg_limited"] = (new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = LimitedPassword }.ConnectionString, new PostgresDriver()),
            ["pg_writer"] = (new NpgsqlConnectionStringBuilder(admin) { Username = writer, Password = LimitedPassword }.ConnectionString, new PostgresDriver()),
        };

        const string List = "SELECT quote_ident(table_schema) || '.' || quote_ident(table_name) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')";
        async Task<List<string>> SnapshotAsync()
        {
            await using var db = new NpgsqlConnection(admin);
            await db.OpenAsync();
            var tables = await TablesAsync(db, List);
            await using var schemas = db.CreateCommand();
            schemas.CommandText = "SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace";
            tables.Add("schemas=" + await schemas.ExecuteScalarAsync());
            return tables;
        }

        var before = await SnapshotAsync();
        var parts = await PreflightAsync(logins);
        var shown = Show(parts);
        output.WriteLine(shown);

        var limited = Findings(parts, "limited/main");
        Assert.Contains(limited, f => f.Severity == Preflight.Error && f.About == "pg_limited" && f.Message.Contains("may not INSERT into", StringComparison.Ordinal));
        Assert.Contains(limited, f => f.Severity == Preflight.Error && f.Message.Contains("may not DELETE from", StringComparison.Ordinal));
        Assert.DoesNotContain(limited, f => f.Message.Contains("may not UPDATE", StringComparison.Ordinal));   // DeleteInsert never updates

        // a login with INSERT and DELETE and no UPDATE: nothing about privileges for DeleteInsert, and UPDATE is an error for ChangeApply, which updates
        var refill = Findings(parts, "refill/main");
        Assert.DoesNotContain(refill, f => f.Message.Contains("may not", StringComparison.Ordinal));
        Assert.True(refill.All(f => f.Severity == Preflight.Info), shown);
        var apply = Assert.Single(Findings(parts, "apply/main"), f => f.Message.Contains("may not", StringComparison.Ordinal));
        Assert.Equal(Preflight.Error, apply.Severity);
        Assert.Contains("may not UPDATE", apply.Message, StringComparison.Ordinal);
        Assert.Contains("the writer 'ChangeApply' does", apply.Message, StringComparison.Ordinal);

        // the state on the target: the schema dbdatasync is not there, and the run login may not create it
        var state = Findings(parts, "limited tracking");
        Assert.Contains(state, f => f.Severity == Preflight.Error && f.Message.Contains("dbdatasync.position_log is not on 'pg_limited'", StringComparison.Ordinal));

        var filter = Assert.Single(Findings(parts, "filtered/main"), f => f.Message.StartsWith("The source filter of the mapping 'main'", StringComparison.Ordinal));
        Assert.Equal(Preflight.Error, filter.Severity);
        Assert.Contains("regoin", filter.Message, StringComparison.Ordinal);

        // a mapping with nothing in its way says so, with information only
        Assert.True(Findings(parts, "clean/main").All(f => f.Severity == Preflight.Info), shown);

        Assert.Equal(before, await SnapshotAsync());
    }

    /// <summary>
    /// The set's <c>preview</c> and <c>try-strategy</c> on PostgreSQL (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): the preview reads the mapped columns with the mapping's filter
    /// through the source's run login, and a strategy's SQL on the target runs as the target's run login, a role that may only read.
    /// </summary>
    [Fact]
    public async Task OnPostgres_APreviewReadsTheFilteredRowsWithTheSourcesLogin_AndAStrategysSqlRunsAsTheTargetsLogin()
    {
        var builder = new NpgsqlConnectionStringBuilder(Postgres);
        var database = $"pv_{_suffix}";
        var role = $"pv_limited_{_suffix}";
        await using (var server = new NpgsqlConnection(Postgres))
        {
            await server.OpenAsync();
            await ExecAsync(server, $"CREATE DATABASE {database}");
            await ExecAsync(server, $"CREATE ROLE {role} LOGIN PASSWORD '{LimitedPassword}'");
        }
        _cleanups.Add(async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(Postgres);
            await server.OpenAsync();
            await ExecAsync(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            await ExecAsync(server, $"DROP ROLE IF EXISTS {role}");
        });

        var admin = new NpgsqlConnectionStringBuilder(Postgres) { Database = database }.ConnectionString;
        await using (var db = new NpgsqlConnection(admin))
        {
            await db.OpenAsync();
            await ExecAsync(db, """
                CREATE TABLE public.orders (id integer PRIMARY KEY, name text NOT NULL, region integer NOT NULL);
                INSERT INTO public.orders VALUES (1, 'a', 1), (2, 'b', 2), (3, 'c', 2);
                CREATE TABLE public.orders_copy (id integer PRIMARY KEY, name text NOT NULL, region integer NOT NULL);
                """);
            await ExecAsync(db, $"GRANT CONNECT ON DATABASE {database} TO {role}; GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT ON public.orders, public.orders_copy TO {role};");
        }

        Connection("pg", "postgres", builder.Host!, builder.Port, database, builder.Username!);
        Connection("pg_limited", "postgres", builder.Host!, builder.Port, database, role);
        Replication("west", "BatchReload", "DeleteInsert", "StagingTable", "pg", database, "public", "orders", "pg_limited", database, "public", "orders_copy", filter: "region = 2");
        var connections = new Logins(new Dictionary<string, (string, IDriver)>
        {
            ["pg"] = (admin, new PostgresDriver()),
            ["pg_limited"] = (new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = LimitedPassword }.ConnectionString, new PostgresDriver()),
        });
        var config = ConfigRepository.ReadOnly(Path.Combine(_root, "config"), SecretStore.ForProviders());

        var preview = await new MappingPreview(config, connections).ReadAsync("west", "main", 1, CancellationToken.None);
        Assert.Equal(["id", "name", "region"], preview.Columns);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(2L, (long)row[2]!);                   // the filter holds: only region 2
        Assert.True(preview.More);                         // two rows match, one was read
        Assert.Equal("pg", preview.Connection);

        var trial = new StrategyTrial(config, connections, new DbDataSync.Scripting.CustomSegmentExpansion(new DbDataSync.Scripting.SegmentingStrategyRunner(null!)));   // no script runs: the SQL kinds need no script host
        var strategy = System.Text.Json.JsonSerializer.Serialize(new { name = "by-region", kind = "TargetSql", sql = "SELECT current_user AS label, '1' AS range_start, '3' AS range_end, true AS selected" });
        var answer = await trial.TryAsync("west", "main", strategy, "", "region", CancellationToken.None);
        var candidate = Assert.Single(answer.Candidates);
        Assert.Equal((role, "region", "1", "3", true), (candidate.Label, candidate.Column, candidate.Start, candidate.End, candidate.Selected));

        // the target's login may not write: a strategy's SQL that writes is refused by the database, and the reason is the person's
        var writes = System.Text.Json.JsonSerializer.Serialize(new { name = "writes", kind = "TargetSql", sql = "INSERT INTO public.orders_copy VALUES (9, 'z', 9) RETURNING 'x' AS label, '1' AS range_start, '2' AS range_end" });
        var refused = await Assert.ThrowsAsync<PreviewException>(() => trial.TryAsync("west", "main", writes, "", "region", CancellationToken.None));
        Assert.Contains("permission denied", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission denied", refused.Kept, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The draft's mapping on PostgreSQL (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): the version's mapping has no filter, and a preview and a trial given the text of the draft's file,
    /// whose filter is <c>region = 2</c>, read with it: the preview answers only rows of region 2, and the trial runs over the draft's mapping. A text that does not read is refused naming the file.
    /// </summary>
    [Fact]
    public async Task OnPostgres_APreviewGivenTheDraftsMappingReadsWithItsFilter_OverAVersionWithoutIt()
    {
        var builder = new NpgsqlConnectionStringBuilder(Postgres);
        var database = $"pd_{_suffix}";
        await using (var server = new NpgsqlConnection(Postgres))
        {
            await server.OpenAsync();
            await ExecAsync(server, $"CREATE DATABASE {database}");
        }
        _cleanups.Add(async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(Postgres);
            await server.OpenAsync();
            await ExecAsync(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        });

        var admin = new NpgsqlConnectionStringBuilder(Postgres) { Database = database }.ConnectionString;
        await using (var db = new NpgsqlConnection(admin))
        {
            await db.OpenAsync();
            await ExecAsync(db, """
                CREATE TABLE public.orders (id integer PRIMARY KEY, name text NOT NULL, region integer NOT NULL);
                INSERT INTO public.orders VALUES (1, 'a', 1), (2, 'b', 2), (3, 'c', 2), (4, 'd', 3);
                CREATE TABLE public.orders_copy (id integer PRIMARY KEY, name text NOT NULL, region integer NOT NULL);
                """);
        }

        Connection("pg", "postgres", builder.Host!, builder.Port, database, builder.Username!);
        Replication("west", "BatchReload", "DeleteInsert", "StagingTable", "pg", database, "public", "orders", "pg", database, "public", "orders_copy");
        var connections = new Logins(new Dictionary<string, (string, IDriver)> { ["pg"] = (admin, new PostgresDriver()) });
        var config = ConfigRepository.ReadOnly(Path.Combine(_root, "config"), SecretStore.ForProviders());
        var published = File.ReadAllText(Path.Combine(_root, "config", "replications", "west", "table-mappings", "main.yaml"));
        var draft = published.ReplaceLineEndings("\n").Replace("  table: orders\n", "  table: orders\n  filter: \"region = 2\"\n", StringComparison.Ordinal);
        Assert.Contains("region = 2", draft);

        // the version's mapping: every row
        var all = await new MappingPreview(config, connections).ReadAsync("west", "main", 100, CancellationToken.None);
        Assert.Equal(4, all.Rows.Count);

        // the draft's mapping, unsaved edits included: only region 2
        var preview = await new MappingPreview(config, connections).ReadAsync("west", "main", 100, CancellationToken.None, draft);
        Assert.Equal([2L, 2L], preview.Rows.Select(r => (long)r[2]!).ToArray());
        Assert.False(preview.More);

        // the trial runs over the draft's mapping too
        var trial = new StrategyTrial(config, connections, new DbDataSync.Scripting.CustomSegmentExpansion(new DbDataSync.Scripting.SegmentingStrategyRunner(null!)));
        var strategy = System.Text.Json.JsonSerializer.Serialize(new { name = "one", kind = "SourceSql", sql = "SELECT 'all' AS label, '1' AS range_start, '9' AS range_end, true AS selected" });
        Assert.Single((await trial.TryAsync("west", "main", strategy, "", "region", CancellationToken.None, draft)).Candidates);

        // a text that does not read is refused, naming the mapping's file
        var bad = await Assert.ThrowsAsync<PreviewException>(() => new MappingPreview(config, connections).ReadAsync("west", "main", 20, CancellationToken.None, draft + "unknownKey: 1\n"));
        Assert.Contains("main.yaml", bad.Message, StringComparison.Ordinal);
        Assert.Contains("unknownKey", bad.Message, StringComparison.Ordinal);
    }

    /// <summary>Each connection with the connection string given for it, as a runner opens it with its slot's run login.</summary>
    private sealed class Logins(IReadOnlyDictionary<string, (string ConnectionString, IDriver Driver)> logins) : IConnectionFactory
    {
        public async Task<(DbConnection Connection, IDriver Driver)> OpenAsync(string connectionName, CredentialRole role, CancellationToken cancellationToken)
        {
            var (text, driver) = logins[connectionName];
            DbConnection connection = driver is PostgresDriver ? new NpgsqlConnection(text) : new SqlConnection(text);
            await connection.OpenAsync(cancellationToken);
            return (connection, driver);
        }
    }

    private sealed class Catalog(IConnectionFactory connections) : IColumnCatalog
    {
        public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
        {
            var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Run, cancellationToken);
            await using (connection) return await driver.ListColumnsAsync(connection, database, schema, table, cancellationToken);
        }
    }
}
