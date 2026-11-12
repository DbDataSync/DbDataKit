using DbDataKit.Odbc.Metadata;
using DuckDB.NET.Data;

namespace DbDataKit.Metadata.Tests;

/// <summary>The keys and foreign keys of a whole schema (<see cref="ICatalogReader.ReadSchemaKeysAsync"/>): one primary-key query and one foreign-key query per schema, whatever the number of tables.</summary>
public class SchemaKeysTests
{
    [Fact]
    public async Task SqlServer_reads_the_keys_of_a_schema_in_two_queries_and_groups_them_by_table()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("i.is_primary_key = 1", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "c", "o", "t"], ["PK_Orders", "Id", 1, "Orders"], ["PK_Lines", "No", 2, "Lines"], ["PK_Lines", "OrderId", 1, "Lines"]);
            if (cmd.Sql.Contains("sys.foreign_keys", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "o", "c", "db", "rs", "rt", "rc", "u", "d", "t"],
                    ["FK_Lines", 2, "Line", "Shop", "dbo", "Orders", "No", "NO_ACTION", "CASCADE", "Lines"], ["FK_Lines", 1, "Order", "Shop", "dbo", "Orders", "Id", "NO_ACTION", "CASCADE", "Lines"]);
            return null;
        });

        var keys = await new SqlServerCatalogReader(conn).ReadSchemaKeysAsync(null, "dbo");

        Assert.Equal(2, conn.Log.Count);
        Assert.All(conn.Log, c => Assert.DoesNotContain("o.name = ", c.Sql, StringComparison.Ordinal));
        Assert.Equal(["dbo"], conn.Log.SelectMany(c => c.Parameters).Select(p => p.Value).Distinct());
        Assert.Equal(["Id"], keys.PrimaryKeys["Orders"].Columns);
        Assert.Equal(["OrderId", "No"], keys.PrimaryKeys["Lines"].Columns);
        var fk = Assert.Single(keys.ForeignKeys["Lines"]);
        Assert.Equal(["Order", "Line"], fk.Columns);
        Assert.Equal(["Id", "No"], fk.ReferencedColumns);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        Assert.False(keys.ForeignKeys.ContainsKey("Orders"));
        Assert.True(keys.PrimaryKeysRead && keys.ForeignKeysRead);
    }

    [Fact]
    public async Task Postgres_reads_the_keys_of_a_schema_in_two_queries()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("con.contype = 'p'", StringComparison.Ordinal)) return FakeResult.Rows(["n", "c", "o", "t"], ["orders_pkey", "id", 1, "orders"]);
            if (cmd.Sql.Contains("con.contype = 'f'", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "o", "c", "db", "rs", "rt", "rc", "u", "d", "t"], ["lines_fk", 1, "order_id", "app", "public", "orders", "id", "NO ACTION", "CASCADE", "lines"]);
            return null;
        });

        var keys = await new PostgresCatalogReader(conn).ReadSchemaKeysAsync(null, "public");

        Assert.Equal(2, conn.Log.Count);
        Assert.All(conn.Log, c => Assert.DoesNotContain("c.relname = ", c.Sql, StringComparison.Ordinal));
        Assert.Equal(["id"], keys.PrimaryKeys["orders"].Columns);
        Assert.Equal("orders", Assert.Single(keys.ForeignKeys["lines"]).Referenced.Name);
    }

    [Fact]
    public async Task A_failing_catalog_query_says_unknown_instead_of_none()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("sys.foreign_keys", StringComparison.Ordinal) ? throw new FakeDbException("denied") : FakeResult.Rows(["n", "c", "o", "t"]));
        var keys = await new SqlServerCatalogReader(conn).ReadSchemaKeysAsync(null, "dbo");
        Assert.True(keys.PrimaryKeysRead);
        Assert.False(keys.ForeignKeysRead);
        Assert.Contains("foreign keys not read", Assert.Single(keys.WarningList), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_generic_reader_groups_by_the_trailing_table_column()
    {
        var conn = new FakeDbConnection(cmd =>
            cmd.Sql.Contains("PRIMARY KEY", StringComparison.Ordinal) ? FakeResult.Rows(["n", "c", "o", "t"], ["pk", "a", 1, "t1"], ["pk", "b", 2, "t2"]) : FakeResult.Rows(["n", "o", "c", "x", "y", "z", "rc", "u", "d", "t"]));
        var keys = await new InformationSchemaCatalogReader(conn, DbDataKit.Dialect.DialectCatalog.For(DbDataKit.Dialect.EngineId.Postgres)).ReadSchemaKeysAsync(null, "s");
        Assert.Equal(["a"], keys.PrimaryKeys["t1"].Columns);
        Assert.Equal(["b"], keys.PrimaryKeys["t2"].Columns);
        Assert.Empty(keys.ForeignKeys);
    }

    [Fact]
    public async Task The_odbc_reader_reads_only_the_tables_of_the_schema_and_no_columns_or_indexes()
    {
        var fake = FakeOdbc.SqlServerLike();
        var session = await OdbcCatalogSession.OpenOverAsync(fake, null, CancellationToken.None);
        var reader = await OdbcCatalogReader.CreateAsync(session);
        fake.Calls.Clear();

        var keys = await ((ICatalogReader)reader).ReadSchemaKeysAsync("Shop", "dbo");

        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("SQLColumns", StringComparison.Ordinal) || c.StartsWith("SQLStatistics", StringComparison.Ordinal));
        Assert.Equal(["Id"], keys.PrimaryKeys["Orders"].Columns);
        Assert.Equal(["OrderId", "No"], keys.PrimaryKeys["Lines"].Columns);
        Assert.Equal("FK_Lines_Orders", Assert.Single(keys.ForeignKeys["Lines"]).Name);
        Assert.False(keys.PrimaryKeys.ContainsKey("vOrders"));
    }

    [Fact]
    public async Task The_odbc_reader_says_unknown_for_a_driver_without_the_function()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.ForeignKeysUnsupported = true;
        var reader = await OdbcCatalogReader.CreateAsync(await OdbcCatalogSession.OpenOverAsync(fake, null, CancellationToken.None));
        var keys = await ((ICatalogReader)reader).ReadSchemaKeysAsync("Shop", "dbo");
        Assert.True(keys.PrimaryKeysRead);
        Assert.False(keys.ForeignKeysRead);
        Assert.Empty(keys.ForeignKeys);
    }

    /// <summary>A reader that knows nothing of the new member: the default reads each table's shape.</summary>
    private sealed class PerTableOnlyReader(Dictionary<string, TableShape> shapes) : ICatalogReader
    {
        public int ShapeCalls;
        public CatalogCapabilities Capabilities => new();
        public Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> ListCatalogsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var matches = (filter ?? TableFilter.All).Compile();
            foreach (var t in shapes.Values.Select(s => s.Table).Where(matches)) yield return t;
        }
        public Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default) { ShapeCalls++; return Task.FromResult(shapes[table.Name]); }
        public Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_reader_without_the_member_falls_back_to_the_per_table_read()
    {
        static TableShape Shape(string name, TableKind kind, KeyInfo? pk, params ForeignKeyInfo[] fks) =>
            new(new TableRef(null, "s", name, kind), [], pk, fks, [], null, ShapeSupport.Full);
        var reader = new PerTableOnlyReader(new()
        {
            ["a"] = Shape("a", TableKind.Table, new KeyInfo("pk_a", ["id"])),
            ["b"] = Shape("b", TableKind.Table, null, new ForeignKeyInfo("fk_b", ["a_id"], new TableRef(null, "s", "a"), ["id"])),
            ["v"] = Shape("v", TableKind.View, null),
        });

        var keys = await ((ICatalogReader)reader).ReadSchemaKeysAsync(null, "s");

        Assert.Equal(2, reader.ShapeCalls);   // the view is not asked
        Assert.Equal(["id"], keys.PrimaryKeys["a"].Columns);
        Assert.Equal("fk_b", Assert.Single(keys.ForeignKeys["b"]).Name);
    }
}

/// <summary>The same on a real in-process DuckDB.</summary>
public class SchemaKeysDuckDbTests(DuckDbFixture fixture) : IClassFixture<DuckDbFixture>
{
    [Fact]
    public async Task DuckDb_reads_the_keys_of_a_schema_with_two_queries()
    {
        var logged = new LoggingConnection(fixture.Connection);
        var keys = await new DuckDbCatalogReader(logged).ReadSchemaKeysAsync(null, "sales");

        Assert.Equal(2, logged.Statements);
        Assert.Equal(["id"], keys.PrimaryKeys["customers"].Columns);
        Assert.Equal(["order_id", "line"], keys.PrimaryKeys["order_lines"].Columns);
        var fk = Assert.Single(keys.ForeignKeys["orders"]);
        Assert.Equal(["customer_id"], fk.Columns);
        Assert.Equal("customers", fk.Referenced.Name);
        Assert.Equal("sales", fk.Referenced.Schema);
        Assert.Equal(["id"], fk.ReferencedColumns);
        Assert.False(keys.ForeignKeys.ContainsKey("customers"));
        Assert.False(keys.PrimaryKeys.ContainsKey("big_orders"));
    }

    [Fact]
    public async Task The_schema_wide_read_equals_the_per_table_reads()
    {
        var reader = new DuckDbCatalogReader(fixture.Connection);
        var keys = await reader.ReadSchemaKeysAsync("memory", "sales");
        foreach (var name in new[] { "customers", "orders", "order_lines" })
        {
            var shape = await reader.GetShapeAsync(new TableRef("memory", "sales", name));
            Assert.Equal(shape.PrimaryKey?.Columns, keys.PrimaryKeys.TryGetValue(name, out var pk) ? pk.Columns : null);
            Assert.Equal(shape.ForeignKeys.Select(f => (f.Name, f.Referenced.Name)), keys.ForeignKeys.TryGetValue(name, out var fks) ? fks.Select(f => (f.Name, f.Referenced.Name)) : []);
        }
    }

    /// <summary>Counts the commands that reach the connection.</summary>
    private sealed class LoggingConnection(DuckDBConnection inner) : System.Data.Common.DbConnection
    {
        public int Statements;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get => inner.ConnectionString; set { } }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override System.Data.ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() { }
        public override void Open() { }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override System.Data.Common.DbCommand CreateDbCommand() { Statements++; return inner.CreateCommand(); }
    }
}
