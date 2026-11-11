using System.Data;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata.Tests;

/// <summary>
/// SQL Server and PostgreSQL readers offline: the SQL they generate (through a recording fake DbConnection) and the mapping of canned catalog rows.
/// Live runs need a server and a provider package this repo does not reference; see the README.
/// </summary>
public class SqlServerReaderTests
{
    private static string[] ColumnNames => ["name", "column_id", "type", "is_nullable", "def", "char_len", "prec", "scale", "collation", "is_identity", "is_computed"];

    [Fact]
    public async Task Columns_come_from_sys_views_with_identity_computed_defaults_and_composed_native_types()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames,
                ["Id", 1, "int", false, null, null, null, null, null, true, false],
                ["Name", 2, "nvarchar", false, "(N'x')", 50, null, null, "SQL_Latin1_General_CP1_CI_AS", false, false],
                ["Notes", 3, "nvarchar", true, null, -1, null, null, null, false, false],
                ["Price", 4, "decimal", true, null, null, 18, 2, null, false, false],
                ["Stamp", 5, "datetime2", true, null, null, null, 7, null, false, false],
                ["Total", 6, "int", true, "([Id]*2)", null, null, null, null, false, true])
            : FakeResult.Rows(["x"]));
        var reader = new SqlServerCatalogReader(conn);

        var shape = await reader.GetShapeAsync(new TableRef(null, "dbo", "Orders"));

        var cols = shape.Columns.ToDictionary(c => c.Name);
        Assert.True(cols["Id"].IsIdentity);
        Assert.Equal(CanonicalKind.Int32, cols["Id"].Type.Kind);
        Assert.Equal("nvarchar(50)", cols["Name"].NativeType);
        Assert.Equal(CanonicalType.String(50), cols["Name"].Type);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", cols["Name"].Collation);
        Assert.Equal("(N'x')", cols["Name"].DefaultExpr);
        Assert.Equal("nvarchar(max)", cols["Notes"].NativeType);
        Assert.Equal(CanonicalType.Decimal(18, 2), cols["Price"].Type);
        Assert.Equal("datetime2(7)", cols["Stamp"].NativeType);
        Assert.True(cols["Total"].IsComputed);
        Assert.Equal("([Id]*2)", cols["Total"].DefaultExpr);
        Assert.True(shape.Support.IdentityAndComputed);
    }

    [Fact]
    public async Task Queries_bind_schema_and_name_as_named_parameters_and_never_inline_them()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames, ["Id", 1, "int", false, null, null, null, null, null, false, false]) : null);
        await new SqlServerCatalogReader(conn).GetShapeAsync(new TableRef(null, "dbo", "O'Brien; DROP TABLE x"));

        Assert.NotEmpty(conn.Log);
        Assert.All(conn.Log, c =>
        {
            Assert.DoesNotContain("DROP", c.Sql);
            Assert.Contains("@p0", c.Sql);
            Assert.Equal([("@p0", (object?)"dbo"), ("@p1", "O'Brien; DROP TABLE x")], c.Parameters);
        });
    }

    [Fact]
    public async Task Generated_catalog_queries_cover_keys_foreign_keys_indexes_and_row_estimates()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames, ["Id", 1, "int", false, null, null, null, null, null, false, false]) : null);
        await new SqlServerCatalogReader(conn).GetShapeAsync(new TableRef(null, "dbo", "Orders"));

        string all = string.Join("\n", conn.Log.Select(c => c.Sql));
        Assert.Contains("i.is_primary_key = 1", all);
        Assert.Contains("sys.foreign_keys", all);
        Assert.Contains("sys.dm_db_partition_stats", all);
        Assert.Contains("i.is_primary_key = 0", all);
        Assert.Contains("sys.default_constraints", all);
        Assert.Contains("sys.computed_columns", all);
    }

    [Fact]
    public async Task A_catalog_makes_a_three_part_name_with_the_database_quoted()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM [Sales]]DB].sys.columns", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames, ["Id", 1, "int", false, null, null, null, null, null, false, false]) : null);
        await new SqlServerCatalogReader(conn).GetShapeAsync(new TableRef("Sales]DB", "dbo", "Orders"));

        Assert.Contains("[Sales]]DB].sys.columns", conn.Log[0].Sql);
    }

    [Fact]
    public async Task A_catalog_name_with_a_control_character_is_refused_before_any_sql()
    {
        var conn = new FakeDbConnection(_ => null);
        await Assert.ThrowsAsync<CatalogNameException>(() => new SqlServerCatalogReader(conn).GetShapeAsync(new TableRef("db\u0000x", "dbo", "T")));
        Assert.Empty(conn.Log);
    }

    [Fact]
    public async Task Keys_and_foreign_keys_map_from_catalog_rows()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal)) return FakeResult.Rows(ColumnNames, ["Id", 1, "int", false, null, null, null, null, null, false, false]);
            if (cmd.Sql.Contains("i.is_primary_key = 1", StringComparison.Ordinal)) return FakeResult.Rows(["n", "c", "o"], ["PK_Orders", "B", 2], ["PK_Orders", "A", 1]);
            if (cmd.Sql.Contains("sys.foreign_keys", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "o", "c", "db", "rs", "rt", "rc", "u", "d"],
                    ["FK_Lines", 2, "Line", "Shop", "dbo", "Lines", "No", "NO_ACTION", "CASCADE"], ["FK_Lines", 1, "Order", "Shop", "dbo", "Lines", "OrderId", "NO_ACTION", "CASCADE"]);
            if (cmd.Sql.Contains("i.is_primary_key = 0", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "u", "o", "c", "d"], ["IX_A", true, 2, "Y", true], ["IX_A", true, 1, "X", false]);
            if (cmd.Sql.Contains("sys.dm_db_partition_stats", StringComparison.Ordinal)) return FakeResult.Rows(["rows"], [1234L]);
            return null;
        });

        var shape = await new SqlServerCatalogReader(conn).GetShapeAsync(new TableRef(null, "dbo", "Orders"));

        Assert.Equal(["A", "B"], shape.PrimaryKey!.Columns);
        var fk = Assert.Single(shape.ForeignKeys);
        Assert.Equal(["Order", "Line"], fk.Columns);
        Assert.Equal(["OrderId", "No"], fk.ReferencedColumns);
        Assert.Equal(ReferentialAction.NoAction, fk.OnUpdate);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        var index = Assert.Single(shape.Indexes);
        Assert.True(index.Unique);
        Assert.Equal([new IndexColumn("X", false), new IndexColumn("Y", true)], index.Columns);
        Assert.Equal(1234, shape.RowEstimate);
    }

    [Fact]
    public async Task An_index_reports_its_included_columns_the_constraint_behind_it_and_its_storage()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal)) return FakeResult.Rows(ColumnNames, ["Id", 1, "int", false, null, null, null, null, null, false, false]);
            if (cmd.Sql.Contains("sys.indexes", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "u", "o", "c", "d", "inc", "con", "comp", "parts"],
                    ["IX_A", true, 1, "X", false, false, 0, "PAGE", 3], ["IX_A", true, 3, "Z", false, true, 0, "PAGE", 3], ["IX_A", true, 2, "Y", true, false, 0, "PAGE", 3],
                    ["UQ_B", true, 1, "B", false, false, 1, "NONE", 1]);
            return null;
        });

        var shape = await new SqlServerCatalogReader(conn).GetShapeAsync(new TableRef(null, "dbo", "Orders"));

        var a = shape.Indexes.Single(i => i.Name == "IX_A");
        Assert.Equal([new IndexColumn("X", false), new IndexColumn("Y", true)], a.Columns);
        Assert.Equal(["Z"], a.IncludeList);
        Assert.False(a.BacksConstraint);
        Assert.Equal(("PAGE", 3), (a.Compression, a.Partitions));
        Assert.True(shape.Indexes.Single(i => i.Name == "UQ_B").BacksConstraint);
    }

    [Fact]
    public async Task A_schema_is_read_in_three_queries_whatever_the_number_of_tables()
    {
        var log = new List<string>();
        var conn = new FakeDbConnection(cmd =>
        {
            log.Add(cmd.Sql);
            if (cmd.Sql.Contains("FROM sys.objects o JOIN sys.schemas s", StringComparison.Ordinal) && cmd.Sql.StartsWith("SELECT DB_NAME()", StringComparison.Ordinal))
                return FakeResult.Rows(["d", "s", "n", "t"], ["Shop", "dbo", "Orders", "U"], ["Shop", "dbo", "Lines", "U"], ["Shop", "dbo", "OrderView", "V"], ["Shop", "other", "Skipped", "U"]);
            if (cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal))
                return FakeResult.Rows(ColumnNames.Append("t").ToArray(),
                    ["Id", 1, "int", false, null, null, null, null, null, false, false, "Orders"], ["Id", 1, "int", false, null, null, null, null, null, false, false, "Lines"], ["Id", 1, "int", false, null, null, null, null, null, false, false, "OrderView"]);
            if (cmd.Sql.Contains("i.type > 0", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "u", "o", "c", "d", "inc", "con", "comp", "parts", "t"], ["IX_Orders", false, 1, "Id", false, false, 0, "NONE", 1, "Orders"]);
            return null;
        });

        var shapes = await new SqlServerCatalogReader(conn).ReadSchemaAsync(null, "dbo");

        Assert.Equal(["Lines", "OrderView", "Orders"], shapes.Select(s => s.Table.Name).ToList());
        Assert.Equal(TableKind.View, shapes.Single(s => s.Table.Name == "OrderView").Table.Kind);
        Assert.Equal("IX_Orders", Assert.Single(shapes.Single(s => s.Table.Name == "Orders").Indexes).Name);
        Assert.Empty(shapes.Single(s => s.Table.Name == "Lines").Indexes);
        Assert.Equal(3, log.Count);
        Assert.False(shapes[0].Support.PrimaryKeys);      // keys are not read here, and the flag says so
    }

    [Fact]
    public async Task The_index_behind_a_primary_key_is_asked_for_only_when_the_options_say_so()
    {
        var asked = new List<string>();
        FakeDbConnection Connection() => new(cmd =>
        {
            if (cmd.Sql.Contains("FROM sys.columns", StringComparison.Ordinal)) return FakeResult.Rows(ColumnNames, ["Id", 1, "int", false, null, null, null, null, null, false, false]);
            if (cmd.Sql.Contains("i.type > 0", StringComparison.Ordinal)) { asked.Add(cmd.Sql); return FakeResult.Rows(["n", "u", "o", "c", "d"]); }
            return null;
        });

        await new SqlServerCatalogReader(Connection()).GetShapeAsync(new TableRef(null, "dbo", "Orders"));
        await new SqlServerCatalogReader(Connection(), options: new CatalogReaderOptions { IncludeConstraintIndexes = true }).GetShapeAsync(new TableRef(null, "dbo", "Orders"));

        Assert.Contains("i.is_primary_key = 0", asked[0]);
        Assert.DoesNotContain("i.is_primary_key = 0", asked[1]);
    }

    [Fact]
    public async Task Describe_calls_sp_describe_first_result_set_with_the_checked_text()
    {
        var conn = new FakeDbConnection(cmd => FakeResult.Rows(
            ["is_hidden", "column_ordinal", "name", "is_nullable", "system_type_name", "collation_name", "is_identity_column", "is_computed_column"],
            [false, 1, "Id", false, "int", null, true, false],
            [false, 2, "Name", true, "nvarchar(50)", "SQL_Latin1_General_CP1_CI_AS", false, false],
            [true, 3, "hidden", true, "int", null, false, false]));

        var shape = await new SqlServerCatalogReader(conn).DescribeQueryAsync("SELECT Id, Name FROM dbo.T;");

        var command = Assert.Single(conn.Log);
        Assert.Equal("EXEC sp_describe_first_result_set @tsql = @p0, @params = NULL, @browse_information_mode = 0", command.Sql);
        Assert.Equal([("@p0", (object?)"SELECT Id, Name FROM dbo.T")], command.Parameters);
        Assert.Equal(DescribeStrategy.SqlServerDescribeFirstResultSet, shape.Strategy);
        Assert.Equal(["Id", "Name"], shape.Columns.Select(c => c.Name));
        Assert.True(shape.Columns[0].IsIdentity);
        Assert.False(shape.Columns[0].Nullable);
        Assert.Equal(CanonicalType.String(50), shape.Columns[1].Type);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", shape.Columns[1].Collation);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.T")]
    [InlineData("SELECT 1; EXEC xp_cmdshell 'dir'")]
    [InlineData("EXEC sp_who")]
    public async Task Describe_refuses_anything_the_read_only_policy_refuses(string sql)
    {
        var conn = new FakeDbConnection(_ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlServerCatalogReader(conn).DescribeQueryAsync(sql));
        Assert.Empty(conn.Log);
    }

    [Fact]
    public async Task Tables_are_listed_with_sys_object_types_mapped_to_kinds()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM sys.objects", StringComparison.Ordinal)
            ? FakeResult.Rows(["c", "s", "n", "t"], ["Shop", "dbo", "Orders", "U"], ["Shop", "dbo", "vOrders", "V"], ["Shop", "sys", "objects", "S"], ["Shop", "sys", "views", "V"]) : null);
        var reader = new SqlServerCatalogReader(conn);

        var tables = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync()) tables.Add(t);
        Assert.Equal(["Orders", "vOrders"], tables.Select(t => t.Name));
        Assert.Equal([TableKind.Table, TableKind.View], tables.Select(t => t.Kind));

        var system = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync(new TableFilter(IncludeSystemObjects: true))) system.Add(t);
        Assert.Equal(["Orders", "vOrders", "objects", "views"], system.Select(t => t.Name));
    }

    [Fact]
    public async Task Server_facts_report_product_version_and_bracket_quotes()
    {
        var conn = new FakeDbConnection(_ => FakeResult.Rows(["v", "e"], ["16.0.1000.6", "Developer Edition"]));
        var facts = await new SqlServerCatalogReader(conn).GetServerFactsAsync();
        Assert.Equal("Microsoft SQL Server", facts.ProductName);
        Assert.Equal("16.0.1000.6", facts.ProductVersion);
        Assert.Equal("[", facts.QuoteOpen);
        Assert.Equal("]", facts.QuoteClose);
        Assert.True(facts.SupportsTop);
        Assert.Equal("sqlserver", DialectProfiles.ToDialect(facts).Id.Value);
    }
}

public class PostgresReaderTests
{
    private static string[] ColumnNames => ["attname", "attnum", "type", "notnull", "def", "a", "b", "c", "collation", "identity", "generated"];

    [Fact]
    public async Task Columns_use_format_type_and_report_identity_serial_and_generated()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM pg_attribute a", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames,
                ["id", 1, "integer", false, "nextval('orders_id_seq'::regclass)", null, null, null, null, true, false],
                ["name", 2, "character varying(50)", true, null, null, null, null, "en_US", false, false],
                ["price", 3, "numeric(18,2)", true, null, null, null, null, null, false, false],
                ["at", 4, "timestamp with time zone", true, "now()", null, null, null, null, false, false],
                ["doubled", 5, "integer", true, "(id * 2)", null, null, null, null, false, true])
            : null);

        var shape = await new PostgresCatalogReader(conn).GetShapeAsync(new TableRef(null, "public", "orders"));

        var cols = shape.Columns.ToDictionary(c => c.Name);
        Assert.True(cols["id"].IsIdentity);
        Assert.False(cols["id"].Nullable);
        Assert.Equal(CanonicalType.String(50), cols["name"].Type);
        Assert.Equal("en_US", cols["name"].Collation);
        Assert.Equal(CanonicalType.Decimal(18, 2), cols["price"].Type);
        Assert.True(cols["at"].Type.TimeZone);
        Assert.True(cols["doubled"].IsComputed);
    }

    [Fact]
    public async Task Queries_use_numbered_markers_with_unnamed_parameters()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM pg_attribute a", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames, ["id", 1, "integer", false, null, null, null, null, null, false, false]) : null);
        await new PostgresCatalogReader(conn).GetShapeAsync(new TableRef(null, "public", "o'; DROP"));

        Assert.All(conn.Log, c =>
        {
            Assert.Contains("$1", c.Sql);
            Assert.DoesNotContain("DROP", c.Sql);
            Assert.Equal([("", (object?)"public"), ("", "o'; DROP")], c.Parameters);
        });
        string all = string.Join("\n", conn.Log.Select(c => c.Sql));
        Assert.Contains("pg_constraint", all);
        Assert.Contains("pg_index", all);
        Assert.Contains("reltuples", all);
    }

    [Fact]
    public async Task Describe_uses_a_schema_only_command_and_the_provider_schema_table()
    {
        var schema = new DataTable();
        foreach (var (n, t) in new (string, Type)[] { ("ColumnName", typeof(string)), ("DataTypeName", typeof(string)), ("AllowDBNull", typeof(bool)), ("NumericPrecision", typeof(int)), ("NumericScale", typeof(int)), ("ColumnSize", typeof(int)), ("IsAutoIncrement", typeof(bool)) })
            schema.Columns.Add(n, t);
        schema.Rows.Add("id", "integer", false, DBNull.Value, DBNull.Value, 4, true);
        schema.Rows.Add("total", "numeric", true, 18, 2, -1, false);
        schema.Rows.Add("label", "character varying", true, DBNull.Value, DBNull.Value, 40, false);
        var data = new FakeResult(FakeResult.Rows(["id", "total", "label"]).Data, schema);
        data.Data.Columns[0].ExtendedProperties["typeName"] = "integer";
        data.Data.Columns[1].ExtendedProperties["typeName"] = "numeric";
        data.Data.Columns[2].ExtendedProperties["typeName"] = "character varying";
        var conn = new FakeDbConnection(_ => data);

        var shape = await new PostgresCatalogReader(conn).DescribeQueryAsync("SELECT id, total, label FROM orders");

        var command = Assert.Single(conn.Log);
        Assert.Equal(CommandBehavior.SchemaOnly, command.Behavior);
        Assert.Equal(DescribeStrategy.PreparedStatement, shape.Strategy);
        Assert.Equal(["id", "total", "label"], shape.Columns.Select(c => c.Name));
        Assert.False(shape.Columns[0].Nullable);
        Assert.True(shape.Columns[0].IsIdentity);
        Assert.Equal(CanonicalType.Decimal(18, 2), shape.Columns[1].Type);
        Assert.Equal(CanonicalType.String(40), shape.Columns[2].Type);
    }

    [Fact]
    public async Task Describe_refuses_writes()
    {
        var conn = new FakeDbConnection(_ => null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresCatalogReader(conn).DescribeQueryAsync("INSERT INTO t VALUES (1)"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresCatalogReader(conn).DescribeQueryAsync("SELECT 1; SELECT pg_sleep(100)"));
        Assert.Empty(conn.Log);
    }

    [Fact]
    public async Task Table_kinds_map_from_relkind_and_pg_schemas_are_system()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM pg_class c", StringComparison.Ordinal)
            ? FakeResult.Rows(["d", "n", "r", "k"], ["app", "public", "orders", "r"], ["app", "public", "v_orders", "v"], ["app", "public", "mv", "m"], ["app", "pg_toast", "pg_toast_1", "r"], ["app", "pg_catalog", "pg_class", "r"]) : null);
        var tables = new List<TableRef>();
        await foreach (var t in new PostgresCatalogReader(conn).ListTablesAsync()) tables.Add(t);
        Assert.Equal([("mv", TableKind.MaterializedView), ("orders", TableKind.Table), ("v_orders", TableKind.View)], tables.Select(t => (t.Name, t.Kind)));
    }

    [Fact]
    public async Task Server_facts_include_the_identifier_length_the_server_reports()
    {
        var conn = new FakeDbConnection(_ => FakeResult.Rows(["v", "m"], ["16.2", "63"]));
        var facts = await new PostgresCatalogReader(conn).GetServerFactsAsync();
        Assert.Equal("PostgreSQL", facts.ProductName);
        Assert.Equal("16.2", facts.ProductVersion);
        Assert.Equal(63, facts.MaxIdentifierLength);
        Assert.Equal("postgres", DialectProfiles.ToDialect(facts).Id.Value);
    }

    [Fact]
    public async Task A_failing_catalog_query_is_a_warning_and_an_unsupported_flag_not_a_silent_empty()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("FROM pg_attribute a", StringComparison.Ordinal)) return FakeResult.Rows(ColumnNames, ["id", 1, "integer", false, null, null, null, null, null, false, false]);
            if (cmd.Sql.Contains("FROM pg_index", StringComparison.Ordinal)) throw new FakeDbException("permission denied for pg_index");
            return null;
        });

        var shape = await new PostgresCatalogReader(conn).GetShapeAsync(new TableRef(null, "public", "orders"));

        Assert.False(shape.Support.Indexes);
        Assert.True(shape.Support.PrimaryKeys);
        Assert.Contains("permission denied", Assert.Single(shape.Warnings!));
    }
}

public sealed class FakeDbException(string message) : System.Data.Common.DbException(message);
