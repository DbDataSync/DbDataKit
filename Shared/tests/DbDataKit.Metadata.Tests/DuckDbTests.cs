using System.Data.Common;
using DbDataKit.Dialect;
using DuckDB.NET.Data;

namespace DbDataKit.Metadata.Tests;

/// <summary>The generic INFORMATION_SCHEMA reader and the DuckDB reader against a real in-process DuckDB.</summary>
public sealed class DuckDbFixture : IDisposable
{
    public DuckDBConnection Connection { get; } = new("DataSource=:memory:");

    public DuckDbFixture()
    {
        Connection.Open();
        foreach (string sql in new[]
        {
            "CREATE SCHEMA sales",
            "CREATE TABLE sales.customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, balance DECIMAL(18,3), created TIMESTAMP DEFAULT current_timestamp, tz TIMESTAMPTZ, flag BOOLEAN, big BIGINT, raw BLOB, uid UUID)",
            "CREATE TABLE sales.orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES sales.customers(id), total DOUBLE, note VARCHAR(40))",
            "CREATE TABLE sales.order_lines (order_id INTEGER, line INTEGER, sku VARCHAR, PRIMARY KEY (order_id, line))",
            "CREATE VIEW sales.big_orders AS SELECT * FROM sales.orders WHERE total > 100",
            "CREATE INDEX ix_orders_total ON sales.orders (total)",
            "CREATE UNIQUE INDEX ux_customers_name ON sales.customers (name)",
            "INSERT INTO sales.customers (id, name) VALUES (1, 'a'), (2, 'b'), (3, 'c')",
        })
        {
            using var c = Connection.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }

    public void Dispose() => Connection.Dispose();
}

public class DuckDbTests(DuckDbFixture fixture) : IClassFixture<DuckDbFixture>
{
    private DbConnection Connection => fixture.Connection;
    private static readonly ISqlDialect Dialect = DialectCatalog.For(EngineId.DuckDb);

    private static async Task<List<T>> ToList<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [Fact]
    public async Task Lists_catalogs_without_the_system_ones()
    {
        var catalogs = await ToList(new DuckDbCatalogReader(Connection).ListCatalogsAsync());
        Assert.Contains("memory", catalogs);
        Assert.DoesNotContain("system", catalogs);
        Assert.DoesNotContain("temp", catalogs);
    }

    [Fact]
    public async Task Lists_every_schema_including_empty_ones_and_system_catalogs()
    {
        using (var c = Connection.CreateCommand()) { c.CommandText = "CREATE SCHEMA IF NOT EXISTS empty_one"; c.ExecuteNonQuery(); }
        var schemas = await ToList(new DuckDbCatalogReader(Connection).ListSchemasAsync());
        Assert.Contains(new SchemaRef("memory", "empty_one"), schemas);
        Assert.Contains(new SchemaRef("memory", "sales"), schemas);
        Assert.Contains(schemas, s => s.Catalog == "system" && s.Name == "information_schema");
        Assert.Equal(schemas.OrderBy(s => s.Catalog, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal), schemas);
    }

    [Fact]
    public async Task Reads_the_columns_alone_and_fails_for_a_missing_table()
    {
        var reader = new DuckDbCatalogReader(Connection);
        var columns = await reader.GetColumnsAsync(new TableRef("memory", "sales", "customers"));
        Assert.Equal("id", columns[0].Name);
        Assert.False(columns[0].Nullable);
        Assert.Equal(CanonicalType.Decimal(18, 3), columns.Single(c => c.Name == "balance").Type);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetColumnsAsync(new TableRef(null, "sales", "nope")));
    }

    [Fact]
    public async Task Lists_tables_and_views_of_a_schema_with_kinds()
    {
        var tables = await ToList(new DuckDbCatalogReader(Connection).ListTablesAsync(new TableFilter(SchemaPattern: "sales")));
        Assert.Equal(["big_orders", "customers", "order_lines", "orders"], tables.Select(t => t.Name));
        Assert.Equal(TableKind.View, tables.Single(t => t.Name == "big_orders").Kind);
        Assert.All(tables.Where(t => t.Name != "big_orders"), t => Assert.Equal(TableKind.Table, t.Kind));
    }

    [Fact]
    public async Task Filters_by_escaped_like_pattern_in_memory()
    {
        var reader = new DuckDbCatalogReader(Connection);
        var underscore = await ToList(reader.ListTablesAsync(new TableFilter(SchemaPattern: "sales", NamePattern: "order\\_%")));
        Assert.Equal(["order_lines"], underscore.Select(t => t.Name));
        var wildcard = await ToList(reader.ListTablesAsync(new TableFilter(SchemaPattern: "sales", NamePattern: "order_%")));
        Assert.Equal(["order_lines", "orders"], wildcard.Select(t => t.Name));
        var onlyViews = await ToList(reader.ListTablesAsync(new TableFilter(SchemaPattern: "sales", Kinds: new HashSet<TableKind> { TableKind.View })));
        Assert.Equal(["big_orders"], onlyViews.Select(t => t.Name));
    }

    [Fact]
    public async Task System_schemas_are_hidden_unless_asked_for()
    {
        var reader = new DuckDbCatalogReader(Connection);
        Assert.DoesNotContain(await ToList(reader.ListTablesAsync()), t => t.Schema == "information_schema");
        var all = await ToList(reader.ListTablesAsync(new TableFilter(IncludeSystemObjects: true)));
        Assert.Contains(all, t => t.Kind == TableKind.SystemTable);
    }

    [Fact]
    public async Task Shape_has_columns_with_canonical_types_defaults_and_nullability()
    {
        var shape = await new DuckDbCatalogReader(Connection).GetShapeAsync(new TableRef("memory", "sales", "customers"));

        Assert.Equal(["id", "name", "balance", "created", "tz", "flag", "big", "raw", "uid"], shape.Columns.Select(c => c.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], shape.Columns.Select(c => c.Ordinal));
        var byName = shape.Columns.ToDictionary(c => c.Name);
        Assert.Equal(CanonicalKind.Int32, byName["id"].Type.Kind);
        Assert.False(byName["name"].Nullable);
        Assert.Equal(CanonicalKind.String, byName["name"].Type.Kind);
        Assert.Equal(CanonicalType.Decimal(18, 3), byName["balance"].Type);
        Assert.Equal("DECIMAL(18,3)", byName["balance"].NativeType);
        Assert.Equal(CanonicalKind.Timestamp, byName["created"].Type.Kind);
        Assert.NotNull(byName["created"].DefaultExpr);
        Assert.True(byName["tz"].Type.TimeZone);
        Assert.Equal(CanonicalKind.Boolean, byName["flag"].Type.Kind);
        Assert.Equal(CanonicalKind.Int64, byName["big"].Type.Kind);
        Assert.Equal(CanonicalKind.Binary, byName["raw"].Type.Kind);
        Assert.Equal(CanonicalKind.Guid, byName["uid"].Type.Kind);
        Assert.False(shape.Support.IdentityAndComputed);
    }

    [Fact]
    public async Task Shape_has_primary_key_foreign_key_indexes_and_a_row_estimate()
    {
        var reader = new DuckDbCatalogReader(Connection);

        var lines = await reader.GetShapeAsync(new TableRef("memory", "sales", "order_lines"));
        Assert.Equal(["order_id", "line"], lines.PrimaryKey!.Columns);

        var orders = await reader.GetShapeAsync(new TableRef("memory", "sales", "orders"));
        Assert.Equal(["id"], orders.PrimaryKey!.Columns);
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal(["customer_id"], fk.Columns);
        Assert.Equal("customers", fk.Referenced.Name);
        Assert.Equal(["id"], fk.ReferencedColumns);
        var index = Assert.Single(orders.Indexes, i => i.Name == "ix_orders_total");
        Assert.False(index.Unique);
        Assert.Equal(["total"], index.Columns.Select(c => c.Name));

        var customers = await reader.GetShapeAsync(new TableRef("memory", "sales", "customers"));
        Assert.True(customers.Indexes.Single(i => i.Name == "ux_customers_name").Unique);
        Assert.Equal(3, customers.RowEstimate);
        Assert.True(customers.Support.RowEstimate);
        Assert.Null(customers.Warnings);
    }

    [Fact]
    public async Task Views_have_columns_and_no_keys()
    {
        var shape = await new DuckDbCatalogReader(Connection).GetShapeAsync(new TableRef("memory", "sales", "big_orders", TableKind.View));
        Assert.Equal(4, shape.Columns.Count);
        Assert.Null(shape.PrimaryKey);
        Assert.Empty(shape.ForeignKeys);
    }

    [Fact]
    public async Task A_missing_table_is_an_error_not_an_empty_shape() =>
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new DuckDbCatalogReader(Connection).GetShapeAsync(new TableRef(null, "sales", "nope")));

    [Fact]
    public async Task Names_with_quotes_and_wildcards_are_parameters_not_sql_text()
    {
        // A hostile name travels as a parameter value and simply matches nothing.
        var reader = new DuckDbCatalogReader(Connection);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reader.GetShapeAsync(new TableRef(null, "sales", "x' OR '1'='1")));
    }

    [Fact]
    public async Task Crawl_returns_every_shape_of_the_filter()
    {
        var shapes = await ToList(((ICatalogReader)new DuckDbCatalogReader(Connection)).CrawlAsync(new TableFilter(SchemaPattern: "sales")));
        Assert.Equal(["big_orders", "customers", "order_lines", "orders"], shapes.Select(s => s.Table.Name));
    }

    [Fact]
    public async Task The_generic_information_schema_reader_also_works_on_duckdb()
    {
        var reader = new InformationSchemaCatalogReader(Connection, Dialect);
        var tables = await ToList(reader.ListTablesAsync(new TableFilter(SchemaPattern: "sales", Kinds: new HashSet<TableKind> { TableKind.Table })));
        Assert.Equal(["customers", "order_lines", "orders"], tables.Select(t => t.Name));

        var shape = await reader.GetShapeAsync(new TableRef("memory", "sales", "orders"));
        Assert.Equal(["id", "customer_id", "total", "note"], shape.Columns.Select(c => c.Name));
        Assert.Equal(CanonicalKind.String, shape.Columns.Single(c => c.Name == "note").Type.Kind);
        Assert.Equal(["id"], shape.PrimaryKey!.Columns);
        Assert.False(shape.Support.Indexes);   // INFORMATION_SCHEMA has no index view: unknown, not "none"
        Assert.Empty(shape.Indexes);
        Assert.False(reader.Capabilities.Indexes);
    }

    [Fact]
    public async Task Server_facts_come_from_the_dialect_and_the_server_version()
    {
        var facts = await new DuckDbCatalogReader(Connection).GetServerFactsAsync();
        Assert.Equal("DuckDB", facts.ProductName);
        Assert.NotNull(facts.ProductVersion);
        Assert.Equal("\"", facts.QuoteOpen);
        Assert.Equal("\"", facts.QuoteClose);
        Assert.True(facts.SupportsLimit);
        Assert.Equal("duckdb", DialectProfiles.ToDialect(facts).Id.Value);
    }

    [Fact]
    public async Task Describe_uses_the_engine_describe_statement()
    {
        var shape = await new DuckDbCatalogReader(Connection).DescribeQueryAsync("SELECT id, name, balance * 2 AS twice FROM sales.customers;");
        Assert.Equal(DescribeStrategy.EngineDescribeStatement, shape.Strategy);
        Assert.Equal(["id", "name", "twice"], shape.Columns.Select(c => c.Name));
        Assert.Equal(CanonicalKind.Int32, shape.Columns[0].Type.Kind);
        Assert.Equal(CanonicalKind.String, shape.Columns[1].Type.Kind);
        Assert.Equal(CanonicalKind.Decimal, shape.Columns[2].Type.Kind);
    }

    [Fact]
    public async Task Describe_does_not_run_the_query()
    {
        // The statement would fail at runtime (division by zero) if it executed; describing only plans it.
        var shape = await new DuckDbCatalogReader(Connection).DescribeQueryAsync("SELECT 1 / 0 AS boom FROM sales.customers");
        Assert.Single(shape.Columns);
    }

    [Theory]
    [InlineData("DELETE FROM sales.customers")]
    [InlineData("SELECT 1; DROP TABLE sales.customers")]
    [InlineData("CREATE TABLE x AS SELECT 1")]
    public async Task Describe_is_guarded_by_the_read_only_policy(string sql)
    {
        var reader = new DuckDbCatalogReader(Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.DescribeQueryAsync(sql));
        Assert.Equal(3, (await new DuckDbCatalogReader(Connection).GetShapeAsync(new TableRef(null, "sales", "customers"))).RowEstimate);
    }

    [Fact]
    public async Task The_wrapped_zero_row_probe_describes_a_query()
    {
        // The generic strategy: the base reader has no engine describe, so it wraps the query.
        var shape = await new InformationSchemaCatalogReader(Connection, Dialect).DescribeQueryAsync("SELECT id, name FROM sales.customers -- trailing comment");
        Assert.Equal(DescribeStrategy.WrappedZeroRowProbe, shape.Strategy);
        Assert.Equal(["id", "name"], shape.Columns.Select(c => c.Name));
        Assert.Equal(CanonicalKind.Int32, shape.Columns[0].Type.Kind);
    }
}
