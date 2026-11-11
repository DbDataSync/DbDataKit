using DbDataKit.Odbc.Metadata;

namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>A driver that answers from canned rows, so the typed layer is tested without any ODBC driver.</summary>
internal sealed class FakeFunctions : IOdbcCatalogFunctions
{
    public List<string?[]> Tables = [], Columns = [], PrimaryKeys = [], ForeignKeys = [], Statistics = [];
    public Dictionary<ushort, string> Strings = [];
    public Dictionary<ushort, uint> Numbers = [];
    public bool PrimaryKeysUnsupported;
    public List<(string? Schema, string? Table)> TableCalls = [];

    public Task<IReadOnlyList<string?[]>> TablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken ct)
    {
        TableCalls.Add((schema, table));
        // A driver that treats the name as a pattern and does not understand the escape finds nothing for an escaped name.
        if (table is not null && table.Contains('\\')) return Task.FromResult<IReadOnlyList<string?[]>>([]);
        return Task.FromResult<IReadOnlyList<string?[]>>(Tables);
    }

    public Task<IReadOnlyList<string?[]>> ColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken ct) => Task.FromResult<IReadOnlyList<string?[]>>(Columns);

    public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken ct) =>
        PrimaryKeysUnsupported ? throw new OdbcCatalogException("SQLPrimaryKeys", "IM001") : Task.FromResult<IReadOnlyList<string?[]>>(PrimaryKeys.Where(r => r[2] == table).ToList());

    public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string? catalog, string? schema, string table, CancellationToken ct) => Task.FromResult<IReadOnlyList<string?[]>>(ForeignKeys);
    public Task<IReadOnlyList<string?[]>> StatisticsAsync(string? catalog, string? schema, string table, CancellationToken ct) => Task.FromResult<IReadOnlyList<string?[]>>(Statistics);

    public Task<string?> GetInfoStringAsync(ushort infoType, CancellationToken ct) =>
        Strings.TryGetValue(infoType, out var s) ? Task.FromResult<string?>(s) : throw new OdbcCatalogException("SQLGetInfo", "HY096");

    public Task<uint> GetInfoNumberAsync(ushort infoType, CancellationToken ct) =>
        Numbers.TryGetValue(infoType, out var n) ? Task.FromResult(n) : throw new OdbcCatalogException("SQLGetInfo", "HY096");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class OdbcCatalogTests
{
    [Fact]
    public async Task Primary_key_columns_come_back_in_key_order()
    {
        var fake = new FakeFunctions
        {
            PrimaryKeys =
            [
                ["db", "dbo", "orders", "line", "2", "pk_orders"],
                ["db", "dbo", "orders", "id", "1", "pk_orders"],
                ["db", "dbo", "other", "x", "1", "pk_other"],
            ],
        };
        var key = await new OdbcCatalog(fake).PrimaryKeyAsync("db", "dbo", "orders");

        Assert.NotNull(key);
        Assert.Equal("pk_orders", key.Name);
        Assert.Equal(["id", "line"], key.Columns);
    }

    [Fact]
    public async Task A_table_without_a_key_has_none()
    {
        Assert.Null(await new OdbcCatalog(new FakeFunctions()).PrimaryKeyAsync(null, null, "t"));
    }

    [Fact]
    public async Task Primary_keys_for_a_table_list_skip_views_and_report_unsupported_drivers()
    {
        OdbcTable[] tables = [new(null, "dbo", "t", "TABLE"), new(null, "dbo", "v", "VIEW")];

        var supported = new FakeFunctions { PrimaryKeys = [[null, "dbo", "t", "id", "1", null], [null, "dbo", "v", "id", "1", null]] };
        var (keys, ok) = await new OdbcCatalog(supported).PrimaryKeysAsync(tables);
        Assert.True(ok);
        Assert.Equal("t", Assert.Single(keys).Table);

        var (none, supportedFlag) = await new OdbcCatalog(new FakeFunctions { PrimaryKeysUnsupported = true }).PrimaryKeysAsync(tables);
        Assert.False(supportedFlag);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Foreign_key_rows_group_by_name_and_keep_column_pairs_in_order()
    {
        var fake = new FakeFunctions
        {
            ForeignKeys =
            [
                // PKCAT PKSCHEM PKTABLE PKCOL FKCAT FKSCHEM FKTABLE FKCOL SEQ UPD DEL FKNAME PKNAME
                ["db", "dbo", "parent", "b", "db", "dbo", "child", "cb", "2", "1", "1", "fk_parent", "pk"],
                ["db", "dbo", "parent", "a", "db", "dbo", "child", "ca", "1", "1", "1", "fk_parent", "pk"],
                ["db", "dbo", "other", "z", "db", "dbo", "child", "cz", "1", "0", "0", "fk_other", "pk2"],
            ],
        };
        var keys = await new OdbcCatalog(fake).ForeignKeysAsync("db", "dbo", "child");

        Assert.Equal(["fk_other", "fk_parent"], keys.Select(k => k.Name));
        var parent = keys.Single(k => k.Name == "fk_parent");
        Assert.Equal(["ca", "cb"], parent.Columns);
        Assert.Equal(["a", "b"], parent.ReferencedColumns);
        Assert.Equal("parent", parent.ReferencedTable);
    }

    [Fact]
    public async Task Index_rows_group_by_name_and_skip_the_table_statistics_row()
    {
        var fake = new FakeFunctions
        {
            Statistics =
            [
                // CAT SCHEM TABLE NON_UNIQUE QUAL NAME TYPE SEQ COLUMN ASC
                ["db", "dbo", "t", null, null, null, "0", null, null, null],
                ["db", "dbo", "t", "0", null, "ux", "3", "2", "b", "D"],
                ["db", "dbo", "t", "0", null, "ux", "3", "1", "a", "A"],
                ["db", "dbo", "t", "1", null, "ix", "3", "1", "c", "A"],
            ],
        };
        var indexes = await new OdbcCatalog(fake).IndexesAsync("db", "dbo", "t");

        Assert.Equal(["ix", "ux"], indexes.Select(i => i.Name));
        var ux = indexes.Single(i => i.Name == "ux");
        Assert.True(ux.Unique);
        Assert.Equal([("a", false), ("b", true)], ux.Columns.Select(c => (c.Name, c.Descending)));
        Assert.False(indexes.Single(i => i.Name == "ix").Unique);
    }

    [Theory]
    [InlineData("0", "YES", false)]   // either column saying "not nullable" wins
    [InlineData("1", "NO", false)]
    [InlineData("1", "YES", true)]
    [InlineData("2", "", true)]       // unknown is treated as nullable
    [InlineData(null, null, true)]
    public async Task Nullability_follows_both_nullable_columns(string? nullable, string? isNullable, bool expected)
    {
        string?[] row = [null, "dbo", "t", "c", "3", "decimal", "38", "40", "0", "10", nullable, null, null, "3", null, null, "1", isNullable];
        var column = Assert.Single(await new OdbcCatalog(new FakeFunctions { Columns = [row] }).ColumnsAsync(null, "dbo", "t"));
        Assert.Equal(expected, column.Nullable);
    }

    [Fact]
    public async Task Columns_carry_size_scale_and_position()
    {
        var fake = new FakeFunctions
        {
            Columns =
            [
                [null, "dbo", "t", "second", "12", "varchar", "20", "20", null, null, "1", null, "'x'", "12", null, "20", "2", "YES"],
                [null, "dbo", "t", "first", "3", "decimal", "38", "40", "0", "10", "0", "note", null, "3", null, null, "1", "NO"],
            ],
        };
        var columns = await new OdbcCatalog(fake).ColumnsAsync(null, "dbo", "t");

        Assert.Equal(["first", "second"], columns.Select(c => c.Name));
        Assert.Equal((3, 38, 0, false), (columns[0].SqlType, columns[0].Size, columns[0].Scale, columns[0].Nullable));
        Assert.Equal("'x'", columns[1].Default);
        Assert.Equal("note", columns[0].Remarks);
    }

    [Fact]
    public async Task Names_with_wildcard_characters_are_escaped_then_matched_exactly()
    {
        var fake = new FakeFunctions
        {
            Strings = { [14] = "\\" },
            Tables = [[null, "dbo", "my_table", "TABLE", null], [null, "dbo", "myXtable", "TABLE", null]],
        };
        var tables = await new OdbcCatalog(fake).TablesAsync(schema: "dbo", table: "my_table");

        // The first call (escaped) finds nothing in this fake driver, so the unescaped name is tried, and the pattern
        // match on myXtable is discarded by the exact comparison.
        Assert.Equal("my_table", Assert.Single(tables).Name);
        Assert.Equal(["my\\_table", "my_table"], fake.TableCalls.Select(c => c.Table));
    }

    [Fact]
    public async Task Names_match_ignoring_case_when_nothing_matches_exactly()
    {
        var fake = new FakeFunctions { Tables = [[null, "DBO", "ORDERS", "TABLE", null]] };
        var tables = await new OdbcCatalog(fake).TablesAsync(schema: "dbo", table: "orders");
        Assert.Equal("ORDERS", Assert.Single(tables).Name);
    }

    [Fact]
    public async Task Views_are_recognised_by_type()
    {
        var fake = new FakeFunctions { Tables = [[null, "dbo", "t", "TABLE", null], [null, "dbo", "v", "VIEW", null], [null, "dbo", "sv", "SYSTEM VIEW", null]] };
        var tables = await new OdbcCatalog(fake).TablesAsync();
        Assert.Equal([false, true, true], tables.Select(t => t.IsView));
    }

    [Fact]
    public async Task Schemas_and_catalogs_are_listed_distinct_and_sorted()
    {
        var fake = new FakeFunctions { Tables = [["b", "s2", null, null, null], ["a", "s1", null, null, null], ["a", "s1", null, null, null], ["a", null, null, null, null]] };
        var catalog = new OdbcCatalog(fake);
        Assert.Equal(["a", "b"], await catalog.CatalogsAsync());
        Assert.Equal(["s1", "s2"], await catalog.SchemasAsync());
    }

    [Fact]
    public async Task Driver_info_reads_quote_case_and_names()
    {
        var fake = new FakeFunctions
        {
            Strings = { [17] = "Microsoft SQL Server", [18] = "16.00.1000", [6] = "msodbcsql18.dll", [29] = "\"", [14] = "\\", [41] = ".", [42] = "database", [39] = "schema" },
            Numbers = { [28] = 4, [93] = 4 },
        };
        var info = await new OdbcCatalog(fake).GetInfoAsync();

        Assert.Equal("Microsoft SQL Server", info.DbmsName);
        Assert.Equal("16.00.1000", info.DbmsVersion);
        Assert.Equal("\"x\"", info.IdentifierQuote!.Apply("x"));
        Assert.Equal(OdbcIdentifierCase.Mixed, info.IdentifierCase);
        Assert.Equal("schema", info.SchemaTerm);
    }

    [Fact]
    public async Task A_driver_reporting_a_space_as_quote_has_no_quoting_and_missing_facts_are_null()
    {
        var info = await new OdbcCatalog(new FakeFunctions { Strings = { [29] = " " } }).GetInfoAsync();
        Assert.Null(info.IdentifierQuote);
        Assert.Null(info.DbmsName);
        Assert.Equal(OdbcIdentifierCase.Unknown, info.IdentifierCase);
    }

    [Fact]
    public void Quote_doubles_an_embedded_quote_character()
    {
        Assert.Equal("\"a\"\"b\"", new OdbcIdentifierQuote("\"", "\"").Apply("a\"b"));
        Assert.Equal("[a]]b]", new OdbcIdentifierQuote("[", "]").Apply("a]b"));
    }

    [Theory]
    [InlineData("IM001", true)]
    [InlineData("HYC00", true)]
    [InlineData("42S02", false)]
    public void Not_supported_is_recognised_from_the_sqlstate(string state, bool expected) =>
        Assert.Equal(expected, new OdbcCatalogException("SQLPrimaryKeys", state).NotSupported);
}

public class OdbcConnectionStringTests
{
    [Fact]
    public void Parses_braced_values_with_semicolons_and_escaped_braces()
    {
        var pairs = OdbcConnectionString.Parse("Driver={SQL Server};Server=x;PWD={p;a}}ss};UID=bob");
        Assert.Equal([("driver", "SQL Server"), ("server", "x"), ("pwd", "p;a}ss"), ("uid", "bob")], pairs);
    }

    [Theory]
    [InlineData("mydsn", "DSN=mydsn")]
    [InlineData("Driver={x};Server=y", "Driver={x};Server=y")]
    public void A_bare_name_is_a_dsn(string input, string expected) => Assert.Equal(expected, OdbcConnectionString.Normalize(input));

    [Fact]
    public void Gets_the_first_matching_key() =>
        Assert.Equal("bob", OdbcConnectionString.Get("DSN=x;User=bob", "uid", "user"));
}
