using DbDataKit.Dialect;

namespace DbDataKit.Metadata.Tests;

/// <summary>MySQL and Oracle readers offline: the SQL they generate (through a recording fake DbConnection) and the mapping of canned catalog rows.</summary>
public class MySqlReaderTests
{
    private static string[] ColumnNames => ["column_name", "ordinal_position", "column_type", "is_nullable", "column_default", "a", "b", "c", "collation_name", "identity", "computed"];

    [Fact]
    public async Task Columns_keep_the_full_column_type_and_report_auto_increment_and_generated()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("information_schema.columns", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames,
                ["id", 1, "bigint unsigned", "NO", null, null, null, null, null, true, false],
                ["name", 2, "varchar(50)", "YES", "x", null, null, null, "utf8mb4_0900_ai_ci", false, false],
                ["price", 3, "decimal(18,2)", "YES", null, null, null, null, null, false, false],
                ["total", 4, "int", "YES", null, null, null, null, null, false, true])
            : FakeResult.Rows(["x"]));

        var shape = await new MySqlCatalogReader(conn).GetShapeAsync(new TableRef(null, "shop", "orders"));

        var cols = shape.Columns.ToDictionary(c => c.Name);
        Assert.True(cols["id"].IsIdentity);
        Assert.False(cols["id"].Nullable);
        Assert.Equal("bigint unsigned", cols["id"].NativeType);
        Assert.Equal("varchar(50)", cols["name"].NativeType);
        Assert.Equal(CanonicalType.String(50), cols["name"].Type);
        Assert.Equal("utf8mb4_0900_ai_ci", cols["name"].Collation);
        Assert.Equal(CanonicalType.Decimal(18, 2), cols["price"].Type);
        Assert.True(cols["total"].IsComputed);
        Assert.True(shape.Support.IdentityAndComputed);
    }

    [Fact]
    public async Task Queries_bind_database_and_table_as_positional_parameters_in_order()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("information_schema.columns", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames, ["id", 1, "int", "NO", null, null, null, null, null, false, false]) : null);

        await new MySqlCatalogReader(conn).GetShapeAsync(new TableRef(null, "shop", "O'Brien; DROP TABLE x"));

        Assert.NotEmpty(conn.Log);
        Assert.All(conn.Log, c =>
        {
            Assert.DoesNotContain("DROP", c.Sql);
            Assert.Equal(["shop", "O'Brien; DROP TABLE x"], c.Parameters.Select(p => p.Value));
        });
        Assert.Contains("statistics", string.Join("\n", conn.Log.Select(c => c.Sql)));
    }

    [Fact]
    public async Task Tables_are_listed_with_the_system_schemas_flagged()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("information_schema.tables", StringComparison.Ordinal)
            ? FakeResult.Rows(["c", "s", "n", "t"], [null, "shop", "orders", "BASE TABLE"], [null, "shop", "v_orders", "VIEW"], [null, "mysql", "user", "BASE TABLE"]) : null);
        var reader = new MySqlCatalogReader(conn);

        var tables = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync()) tables.Add(t);
        Assert.Equal(["orders", "v_orders"], tables.Select(t => t.Name));
        Assert.Equal([TableKind.Table, TableKind.View], tables.Select(t => t.Kind));
        Assert.All(tables, t => Assert.Null(t.Catalog));

        var catalogs = new List<string>();
        await foreach (var c in reader.ListCatalogsAsync()) catalogs.Add(c);
        Assert.Empty(catalogs);
    }

    [Fact]
    public async Task Indexes_exclude_the_primary_key_and_row_estimates_come_from_table_rows()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("information_schema.columns", StringComparison.Ordinal)) return FakeResult.Rows(ColumnNames, ["id", 1, "int", "NO", null, null, null, null, null, false, false]);
            if (cmd.Sql.Contains("information_schema.statistics", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "u", "o", "c", "d"], ["IX_A", 1L, 2, "y", 1L], ["IX_A", 1L, 1, "x", 0L]);
            if (cmd.Sql.Contains("table_rows", StringComparison.Ordinal)) return FakeResult.Rows(["rows"], [987L]);
            return null;
        });

        var shape = await new MySqlCatalogReader(conn).GetShapeAsync(new TableRef(null, "shop", "orders"));

        Assert.Contains("index_name <> 'PRIMARY'", string.Join("\n", conn.Log.Select(c => c.Sql)));
        var index = Assert.Single(shape.Indexes);
        Assert.True(index.Unique);
        Assert.Equal([new IndexColumn("x", false), new IndexColumn("y", true)], index.Columns);
        Assert.Equal(987, shape.RowEstimate);
    }

    [Theory]
    [InlineData("8.4.0", "MySQL")]
    [InlineData("11.4.2-MariaDB-ubu2404", "MariaDB")]
    public async Task Server_facts_name_the_product_from_the_version_string(string version, string product)
    {
        var conn = new FakeDbConnection(_ => FakeResult.Rows(["v"], [version]));
        var facts = await new MySqlCatalogReader(conn).GetServerFactsAsync();
        Assert.Equal(product, facts.ProductName);
        Assert.Equal(version, facts.ProductVersion);
        Assert.Equal("`", facts.QuoteOpen);
    }
}

public class OracleReaderTests
{
    private static string[] ColumnNames => ["column_name", "column_id", "type", "nullable", "def", "a", "b", "c", "d", "identity", "virtual"];

    [Fact]
    public async Task Columns_report_the_composed_type_identity_and_virtual_columns()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM all_tab_cols", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames,
                ["ID", 1, "NUMBER(10,0)", 0, null, null, null, null, null, 1, 0],
                ["NAME", 2, "VARCHAR2(50)", 1, null, null, null, null, null, 0, 0],
                ["STAMP", 3, "TIMESTAMP(6)", 1, null, null, null, null, null, 0, 0],
                ["TOTAL", 4, "NUMBER", 1, null, null, null, null, null, 0, 1])
            : FakeResult.Rows(["x"]));

        var shape = await new OracleCatalogReader(conn).GetShapeAsync(new TableRef(null, "HR", "ORDERS"));

        var cols = shape.Columns.ToDictionary(c => c.Name);
        Assert.True(cols["ID"].IsIdentity);
        Assert.False(cols["ID"].Nullable);
        Assert.True(cols["NAME"].Nullable);
        Assert.Equal("VARCHAR2(50)", cols["NAME"].NativeType);
        Assert.Equal("TIMESTAMP(6)", cols["STAMP"].NativeType);
        Assert.True(cols["TOTAL"].IsComputed);
        Assert.Null(cols["NAME"].DefaultExpr);
    }

    [Fact]
    public async Task Queries_bind_owner_and_table_as_colon_parameters_in_order()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM all_tab_cols", StringComparison.Ordinal)
            ? FakeResult.Rows(ColumnNames, ["ID", 1, "NUMBER", 0, null, null, null, null, null, 0, 0]) : null);

        await new OracleCatalogReader(conn).GetShapeAsync(new TableRef(null, "HR", "O'Brien; DROP TABLE x"));

        Assert.NotEmpty(conn.Log);
        Assert.All(conn.Log, c =>
        {
            Assert.DoesNotContain("DROP", c.Sql);
            Assert.Contains(":p0", c.Sql);
            Assert.Equal(["HR", "O'Brien; DROP TABLE x"], c.Parameters.Select(p => p.Value));
        });
    }

    [Fact]
    public async Task Keys_foreign_keys_indexes_and_row_estimates_map_from_dictionary_rows()
    {
        var conn = new FakeDbConnection(cmd =>
        {
            if (cmd.Sql.Contains("FROM all_tab_cols", StringComparison.Ordinal)) return FakeResult.Rows(ColumnNames, ["ID", 1, "NUMBER", 0, null, null, null, null, null, 0, 0]);
            if (cmd.Sql.Contains("FROM all_indexes", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "u", "o", "c", "d"], ["IX_A", 1, 2, "Y", 1], ["IX_A", 1, 1, "X", 0]);
            if (cmd.Sql.Contains("constraint_type = 'P'", StringComparison.Ordinal)) return FakeResult.Rows(["n", "c", "p"], ["PK_O", "B", 2], ["PK_O", "A", 1]);
            if (cmd.Sql.Contains("constraint_type = 'R'", StringComparison.Ordinal))
                return FakeResult.Rows(["n", "p", "c", "rc", "rs", "rt", "rcol", "u", "d"],
                    ["FK_L", 2, "LINE", null, "HR", "LINES", "NO", "NO ACTION", "CASCADE"], ["FK_L", 1, "ORD", null, "HR", "LINES", "ORDER_ID", "NO ACTION", "CASCADE"]);
            if (cmd.Sql.Contains("num_rows", StringComparison.Ordinal)) return FakeResult.Rows(["rows"], [55L]);
            return null;
        });

        var shape = await new OracleCatalogReader(conn).GetShapeAsync(new TableRef(null, "HR", "ORDERS"));

        Assert.Equal(["A", "B"], shape.PrimaryKey!.Columns);
        var fk = Assert.Single(shape.ForeignKeys);
        Assert.Equal(["ORD", "LINE"], fk.Columns);
        Assert.Equal(["ORDER_ID", "NO"], fk.ReferencedColumns);
        Assert.Equal(new TableRef(null, "HR", "LINES", TableKind.Table), fk.Referenced);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        var index = Assert.Single(shape.Indexes);
        Assert.True(index.Unique);
        Assert.Equal([new IndexColumn("X", false), new IndexColumn("Y", true)], index.Columns);
        Assert.Equal(55, shape.RowEstimate);
    }

    [Fact]
    public async Task Oracle_maintained_schemas_are_flagged_as_system_objects()
    {
        var conn = new FakeDbConnection(cmd => cmd.Sql.Contains("FROM all_tables", StringComparison.Ordinal)
            ? FakeResult.Rows(["c", "s", "n", "t"], [null, "HR", "ORDERS", "TABLE"], [null, "HR", "V_ORDERS", "VIEW"], [null, "SYS", "OBJ$", "SYSTEM TABLE"]) : null);
        var reader = new OracleCatalogReader(conn);

        var tables = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync()) tables.Add(t);
        Assert.Equal(["ORDERS", "V_ORDERS"], tables.Select(t => t.Name));

        var all = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync(new TableFilter(IncludeSystemObjects: true))) all.Add(t);
        Assert.Contains(all, t => t.Name == "OBJ$" && t.Kind == TableKind.SystemTable);
    }
}
