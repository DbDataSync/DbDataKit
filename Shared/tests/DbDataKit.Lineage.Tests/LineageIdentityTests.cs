using DbDataKit.Connections;

namespace DbDataKit.Lineage.Tests;

public class LineageIdentityTests
{
    private static ConnectionDefinition Sql(string engine, string? host = null, int? port = null, string? database = null, string? connectionString = null) =>
        new() { Name = "c", Engine = engine, Address = host is null && database is null ? null : new AddressSpec { Host = host, Port = port, Database = database }, ConnectionString = connectionString };

    private static string Id(ConnectionDefinition c, string? schema, string table) => LineageIdentity.Dataset(c, schema, table).ToString();

    [Fact]
    public void Two_definitions_of_one_server_give_one_identity()
    {
        var plain = Id(Sql("sqlserver", "Sales-DB.corp.", null, "Sales"), "dbo", "Orders");
        Assert.Equal("sqlserver://sales-db.corp/Sales.dbo.Orders", plain);

        Assert.Equal(plain, Id(Sql("sqlserver", "sales-db.corp", 1433, "Sales"), "dbo", "Orders"));                                    // the default port spelled out
        Assert.Equal(plain, Id(Sql("sqlserver", connectionString: "Server=tcp:Sales-DB.corp,1433;Database=Sales;Encrypt=True"), "dbo", "Orders"));
        Assert.Equal(plain, Id(Sql("sqlserver", connectionString: "Data Source=SALES-DB.CORP;Initial Catalog=Sales"), "dbo", "Orders"));
        Assert.NotEqual(plain, Id(Sql("sqlserver", "sales-db.corp", 14330, "Sales"), "dbo", "Orders"));                                // another port is another server
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("(local)")]
    [InlineData(".")]
    [InlineData("::1")]
    public void The_machines_own_names_are_one_server(string host) =>
        Assert.Equal("postgres://localhost/app.public.orders", Id(Sql("postgres", host, 5432, "app"), "public", "orders"));

    [Fact]
    public void A_named_instance_is_part_of_the_server_and_its_port_is_not()
    {
        Assert.Equal("sqlserver://sales-db\\reporting/Sales.dbo.Orders", Id(Sql("sqlserver", "Sales-DB\\Reporting", null, "Sales"), "dbo", "Orders"));
        Assert.Equal(Id(Sql("sqlserver", "sales-db\\reporting", null, "Sales"), "dbo", "Orders"), Id(Sql("sqlserver", "SALES-DB\\REPORTING", 49500, "Sales"), "dbo", "Orders"));
    }

    [Fact]
    public void Names_follow_the_case_rule_of_the_dialect()
    {
        Assert.Equal("postgres://db/App.public.orders".Replace("App", "app"), Id(Sql("postgres", "db", null, "App"), "Public", "Orders"));       // PostgreSQL folds to lower case
        Assert.Equal("oracle://db/ORCL.SALES.ORDERS", Id(Sql("oracle", "db", null, "orcl"), "sales", "orders"));                                        // Oracle to upper
        Assert.Equal("sqlserver://db/Sales.dbo.Orders", Id(Sql("sqlserver", "db", null, "Sales"), "dbo", "Orders"));                                    // SQL Server keeps what it was given
        Assert.NotEqual(Id(Sql("sqlserver", "db", null, "Sales"), "dbo", "Orders"), Id(Sql("sqlserver", "db", null, "Sales"), "dbo", "ORDERS"));
    }

    [Fact]
    public void A_column_is_its_dataset_and_its_name_and_a_file_database_is_its_path()
    {
        var connection = Sql("postgres", "db", null, "app");
        var dataset = LineageIdentity.Dataset(connection, "public", "orders");

        Assert.Equal("postgres://db/app.public.orders#amount", LineageIdentity.Column(connection, dataset, "Amount"));
        Assert.Equal("duckdb:///C:/data/shop.duckdb.main.orders", Id(Sql("duckdb", null, null, "C:\\data\\shop.duckdb"), "main", "orders"));
        Assert.Equal("postgres://db/orders", Id(Sql("postgres", "db"), null, "orders"));
        Assert.Equal("suite://build-1a2b/model/marts.orders", LineageIdentity.SuiteObject("build-1a2b", "model", "marts.orders"));
        Assert.Throws<ArgumentException>(() => LineageIdentity.Dataset(connection, "s", " "));
    }

    [Fact]
    public void An_engine_with_no_dialect_of_its_own_keeps_the_case_it_is_given() =>
        Assert.Equal("flightsql://lake:8815/Cat.Sch.Tbl", Id(Sql("flightsql", "Lake", 8815, "Cat"), "Sch", "Tbl"));
}
