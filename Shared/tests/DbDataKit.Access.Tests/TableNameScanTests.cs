namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP12 (QG3): the conservative lexical scan of the names a statement might use as a table. Fails closed: a name that could be a table is found, whatever its quoting, case, qualifier or the comments between its parts.</summary>
public sealed class TableNameScanTests
{
    private static bool Names(string sql, string name) =>
        TableNameScan.Scan(sql).Chains.Any(c => c.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)));

    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("select * from ORDERS")]
    [InlineData("SELECT * FROM Orders o JOIN x ON o.id = x.id")]
    [InlineData("SELECT * FROM \"orders\"")]
    [InlineData("SELECT * FROM \"Orders\"")]
    [InlineData("SELECT * FROM \"public\".\"orders\"")]
    [InlineData("SELECT * FROM public.orders")]
    [InlineData("SELECT * FROM db.dbo.orders")]
    [InlineData("SELECT * FROM [dbo].[orders]")]
    [InlineData("SELECT * FROM [ORDERS]")]
    [InlineData("SELECT * FROM `orders`")]
    [InlineData("SELECT * FROM dbo . orders")]
    [InlineData("SELECT * FROM dbo/* a */./* b */orders")]
    [InlineData("SELECT * FROM dbo -- a\n . -- b\n orders")]
    [InlineData("SELECT * FROM dbo/**/.[orders]")]
    [InlineData("SELECT * FROM /* nested /* comment */ still */ orders")]
    [InlineData("WITH x AS (SELECT * FROM orders) SELECT * FROM x")]
    [InlineData("SELECT (SELECT COUNT(*) FROM orders)")]
    [InlineData("SELECT * FROM a.b WHERE EXISTS (SELECT 1 FROM orders)")]
    [InlineData("SELECT query_to_xml('select * from orders', true, false, '')")]
    [InlineData("SELECT $$select * from orders$$")]
    [InlineData("SELECT $q$ select * from public.orders $q$")]
    [InlineData("SELECT 1 /* select * from orders */")]
    [InlineData("SELECT 1 -- from orders")]
    [InlineData("TABLE orders")]
    [InlineData("SELECT orders.id FROM x")]
    [InlineData("SELECT * FROM [odd]]name].[orders]")]
    [InlineData("SELECT * FROM \"dbo.orders\"")]
    public void A_table_name_is_found_in_every_spelling(string sql) => Assert.True(Names(sql, "orders"), sql);

    [Theory]
    [InlineData("SELECT * FROM customers", "orders")]
    [InlineData("SELECT * FROM orders_archive", "orders")]
    [InlineData("SELECT * FROM my_orders", "orders")]
    [InlineData("SELECT * FROM \"my orders\"", "orders")]
    [InlineData("SELECT $1, $2 FROM customers", "orders")]
    [InlineData("SELECT 1.5 FROM customers", "orders")]
    public void A_name_that_is_not_the_table_is_not_found(string sql, string name) => Assert.False(Names(sql, name), sql);

    [Fact]
    public void The_parts_of_a_chain_are_unquoted_and_in_order()
    {
        var chains = TableNameScan.Scan("SELECT * FROM [db].[dbo].[Or]]ders]").Chains;
        Assert.Contains(chains, c => c.SequenceEqual(["db", "dbo", "Or]ders"]));
        chains = TableNameScan.Scan("SELECT * FROM \"public\" /* c */ . \"a\"\"b\"").Chains;
        Assert.Contains(chains, c => c.SequenceEqual(["public", "a\"b"]));
    }

    [Theory]
    [InlineData("EXEC('select * from ord' + 'ers')", "EXEC")]
    [InlineData("EXECUTE sp_something", "EXECUTE")]
    [InlineData("EXEC sp_executesql N'select 1'", "EXEC")]
    [InlineData("SELECT * FROM OPENQUERY(link, 'select 1')", "OPENQUERY")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'select 1')", "OPENROWSET")]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$", "DO")]
    [InlineData("/* c */ do $$ begin end $$", "DO")]
    [InlineData("SELECT * FROM U&\"\\006frders\"", "U&")]
    public void A_statement_that_runs_text_the_scan_cannot_see_is_opaque(string sql, string why) => Assert.Contains(why, TableNameScan.Scan(sql).Opaque, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void A_plain_statement_is_not_opaque() => Assert.Null(TableNameScan.Scan("SELECT id, doe FROM customers WHERE name = 'x'").Opaque);

    [Fact]
    public void A_name_in_a_string_literal_is_found_too_because_it_may_be_dynamic_SQL() => Assert.True(Names("SELECT * FROM customers WHERE kind = 'orders'", "orders"));

    [Fact]
    public void Text_that_never_closes_does_not_hide_a_name()
    {
        Assert.True(Names("SELECT 'abc FROM orders", "orders"));
        Assert.True(Names("SELECT \"abc FROM orders", "abc FROM orders"));
        Assert.True(Names("SELECT /* open FROM orders", "orders"));
        Assert.True(Names("SELECT $$ open FROM orders", "orders"));
    }

    [Fact]
    public void Deep_nesting_is_opaque_and_never_throws() =>
        Assert.NotNull(TableNameScan.Scan(string.Concat(Enumerable.Repeat("-- ", 3000)) + "orders").Opaque);
}
