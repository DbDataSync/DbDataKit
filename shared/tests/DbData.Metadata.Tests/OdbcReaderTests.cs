using DbData.Dialect;
using DbData.Odbc.Metadata;

namespace DbData.Metadata.Tests;

/// <summary>A driver that answers the ODBC catalog functions from canned rows.</summary>
internal sealed class FakeOdbc : IOdbcCatalogFunctions
{
    public List<string?[]> Tables = [], Columns = [], PrimaryKeys = [], ForeignKeys = [], Statistics = [];
    public Dictionary<ushort, string> Strings = [];
    public Dictionary<ushort, uint> Numbers = [];
    public bool PrimaryKeysUnsupported, ForeignKeysUnsupported, StatisticsUnsupported, DescribeUnsupported;
    public List<OdbcResultColumn> Described = [];
    public List<string> Calls = [];
    public List<string> Prepared = [];

    private static Task<IReadOnlyList<string?[]>> Rows(IEnumerable<string?[]> rows) => Task.FromResult<IReadOnlyList<string?[]>>(rows.ToList());

    public Task<IReadOnlyList<string?[]>> TablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken ct)
    {
        Calls.Add($"SQLTables({catalog},{schema},{table},{types})");
        if (catalog == "%") return Rows(Tables.Select(t => new string?[] { t[0], null, null, null, null }));
        return Rows(Tables.Where(t => types is null || types.Split(',').Contains(t[3])));
    }

    public Task<IReadOnlyList<string?[]>> ColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken ct)
    {
        Calls.Add($"SQLColumns({table})");
        return Rows(Columns);
    }

    public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken ct)
    {
        Calls.Add($"SQLPrimaryKeys({table})");
        return PrimaryKeysUnsupported ? throw new OdbcCatalogException("SQLPrimaryKeys", "IM001") : Rows(PrimaryKeys.Where(r => r[2] == table));
    }

    public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string? catalog, string? schema, string table, CancellationToken ct)
    {
        Calls.Add($"SQLForeignKeys({table})");
        return ForeignKeysUnsupported ? throw new OdbcCatalogException("SQLForeignKeys", "IM001") : Rows(ForeignKeys.Where(r => r[6] == table));
    }

    public Task<IReadOnlyList<string?[]>> StatisticsAsync(string? catalog, string? schema, string table, CancellationToken ct)
    {
        Calls.Add($"SQLStatistics({table})");
        return StatisticsUnsupported ? throw new OdbcCatalogException("SQLStatistics", "IM001") : Rows(Statistics.Where(r => r[2] == table));
    }

    public Task<string?> GetInfoStringAsync(ushort infoType, CancellationToken ct) =>
        Strings.TryGetValue(infoType, out var s) ? Task.FromResult<string?>(s) : throw new OdbcCatalogException("SQLGetInfo", "HY096");

    public Task<uint> GetInfoNumberAsync(ushort infoType, CancellationToken ct) =>
        Numbers.TryGetValue(infoType, out var n) ? Task.FromResult(n) : throw new OdbcCatalogException("SQLGetInfo", "HY096");

    public Task<IReadOnlyList<OdbcResultColumn>> DescribeQueryAsync(string sql, CancellationToken ct)
    {
        Prepared.Add(sql);
        return DescribeUnsupported ? throw new OdbcCatalogException("SQLPrepare", "IM001") : Task.FromResult<IReadOnlyList<OdbcResultColumn>>(Described);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // SQLGetInfo ids: SQL_DBMS_NAME 17, SQL_DBMS_VER 18, SQL_DRIVER_NAME 6, SQL_IDENTIFIER_QUOTE_CHAR 29, SQL_IDENTIFIER_CASE 28, SQL_MAX_IDENTIFIER_LEN 10005.
    public static FakeOdbc SqlServerLike() => new()
    {
        Strings = { [17] = "Microsoft SQL Server", [18] = "16.00.1000", [6] = "msodbcsql18.dll", [29] = "\"", [14] = "\\", [41] = "." },
        Numbers = { [28] = 4, [93] = 4, [10005] = 128, [121] = 1 },
        Tables =
        [
            ["Shop", "dbo", "Orders", "TABLE", null], ["Shop", "dbo", "Lines", "TABLE", null], ["Shop", "dbo", "vOrders", "VIEW", null], ["Shop", "sys", "objects", "SYSTEM TABLE", null],
        ],
        Columns =
        [
            ["Shop", "dbo", "Orders", "Id", "4", "int", "10", "4", "0", "10", "0", null, null, "4", null, null, "1", "NO"],
            ["Shop", "dbo", "Orders", "Name", "-9", "nvarchar", "50", "100", null, null, "1", null, "(N'x')", "-9", null, "100", "2", "YES"],
            ["Shop", "dbo", "Orders", "Price", "3", "decimal", "18", "20", "2", "10", "1", null, null, "3", null, null, "3", "YES"],
            ["Shop", "dbo", "Orders", "At", "-155", "datetimeoffset", "34", "68", "7", null, "1", null, null, "-155", null, null, "4", "YES"],
            ["Shop", "dbo", "Orders", "Uid", "-11", "uniqueidentifier", "36", "16", null, null, "1", null, null, "-11", null, null, "5", "YES"],
            ["Shop", "dbo", "Lines", "OrderId", "4", "int", "10", "4", "0", "10", "0", null, null, "4", null, null, "1", "NO"],
            ["Shop", "dbo", "Lines", "No", "5", "smallint", "5", "2", "0", "10", "0", null, null, "5", null, null, "2", "NO"],
            ["Shop", "dbo", "vOrders", "Id", "4", "int", "10", "4", "0", "10", "1", null, null, "4", null, null, "1", "YES"],
        ],
        PrimaryKeys =
        [
            ["Shop", "dbo", "Orders", "Id", "1", "PK_Orders"], ["Shop", "dbo", "Lines", "No", "2", "PK_Lines"], ["Shop", "dbo", "Lines", "OrderId", "1", "PK_Lines"],
        ],
        ForeignKeys =
        [
            // PKTABLE_CAT, PKTABLE_SCHEM, PKTABLE_NAME, PKCOLUMN_NAME, FKTABLE_CAT, FKTABLE_SCHEM, FKTABLE_NAME, FKCOLUMN_NAME, KEY_SEQ, UPDATE_RULE, DELETE_RULE, FK_NAME
            ["Shop", "dbo", "Orders", "Id", "Shop", "dbo", "Lines", "OrderId", "1", "3", "0", "FK_Lines_Orders", "PK_Orders", "7"],
        ],
        Statistics =
        [
            // TABLE_CAT, TABLE_SCHEM, TABLE_NAME, NON_UNIQUE, INDEX_QUALIFIER, INDEX_NAME, TYPE, ORDINAL_POSITION, COLUMN_NAME, ASC_OR_DESC
            ["Shop", "dbo", "Orders", null, null, null, "0", null, null, null], ["Shop", "dbo", "Orders", "1", null, "IX_Name", "3", "1", "Name", "D"],
        ],
    };
}

public class OdbcReaderTests
{
    private static async Task<OdbcCatalogReader> Open(FakeOdbc fake, System.Data.Common.DbConnection? probe = null)
    {
        var session = await OdbcCatalogSession.OpenOverAsync(fake, null, CancellationToken.None);
        return await OdbcCatalogReader.CreateAsync(session, probeConnection: probe);
    }

    private static async Task<List<T>> ToList<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [Fact]
    public async Task The_dialect_comes_from_the_driver_facts_and_known_products_get_their_builtin_dialect()
    {
        var reader = await Open(FakeOdbc.SqlServerLike());
        Assert.Equal("Microsoft SQL Server", reader.Facts.ProductName);
        Assert.Equal(128, reader.Facts.MaxIdentifierLength);
        Assert.True(reader.Facts.SupportsMultipleStatements);
        Assert.Equal("sqlserver", reader.Dialect.Id.Value);
    }

    [Fact]
    public async Task An_unknown_driver_gets_the_generic_odbc_dialect_with_the_reported_quote()
    {
        var fake = new FakeOdbc { Strings = { [17] = "Acme DB", [29] = "`" }, Numbers = { [28] = 1 } };
        var reader = await Open(fake);
        Assert.Equal("odbc:acme_db", reader.Dialect.Id.Value);
        Assert.Equal("`x`", reader.Dialect.Quote("x"));
        Assert.Equal(IdentifierCase.Upper, reader.Dialect.Identifiers.Fold);
    }

    [Fact]
    public async Task Lists_catalogs_and_tables_with_kinds_and_filters()
    {
        var reader = await Open(FakeOdbc.SqlServerLike());
        Assert.Equal(["Shop"], await ToList(reader.ListCatalogsAsync()));

        var tables = await ToList(reader.ListTablesAsync());
        Assert.Equal([("Lines", TableKind.Table), ("Orders", TableKind.Table), ("vOrders", TableKind.View)], tables.Select(t => (t.Name, t.Kind)));

        var views = await ToList(reader.ListTablesAsync(new TableFilter(Kinds: new HashSet<TableKind> { TableKind.View })));
        Assert.Equal(["vOrders"], views.Select(t => t.Name));

        var system = await ToList(reader.ListTablesAsync(new TableFilter(IncludeSystemObjects: true)));
        Assert.Contains(system, t => t.Kind == TableKind.SystemTable);

        var pattern = await ToList(reader.ListTablesAsync(new TableFilter(NamePattern: "O%")));
        Assert.Equal(["Orders"], pattern.Select(t => t.Name));
    }

    [Fact]
    public async Task A_name_pattern_without_wildcards_is_pushed_to_the_driver_as_an_exact_name()
    {
        var fake = FakeOdbc.SqlServerLike();
        var reader = await Open(fake);
        await ToList(reader.ListTablesAsync(new TableFilter(SchemaPattern: "dbo", NamePattern: "Orders")));
        Assert.Contains("SQLTables(,dbo,Orders,TABLE,VIEW)", fake.Calls.Select(c => c.Replace("(null", "(")));
    }

    [Fact]
    public async Task Shape_maps_odbc_types_to_canonical_and_keeps_native_names()
    {
        var reader = await Open(FakeOdbc.SqlServerLike());
        var shape = await reader.GetShapeAsync(new TableRef("Shop", "dbo", "Orders"));

        var cols = shape.Columns.ToDictionary(c => c.Name);
        Assert.Equal(CanonicalKind.Int32, cols["Id"].Type.Kind);
        Assert.False(cols["Id"].Nullable);
        Assert.Equal("int", cols["Id"].NativeType);
        Assert.Equal(CanonicalType.String(50), cols["Name"].Type);
        Assert.Equal("(N'x')", cols["Name"].DefaultExpr);
        Assert.Equal(CanonicalType.Decimal(18, 2), cols["Price"].Type);
        Assert.Equal(CanonicalType.Timestamp(7, tz: true), cols["At"].Type);
        Assert.Equal(CanonicalKind.Guid, cols["Uid"].Type.Kind);
        Assert.Equal(["Id"], shape.PrimaryKey!.Columns);
        Assert.Equal("PK_Orders", shape.PrimaryKey.Name);
        var index = Assert.Single(shape.Indexes);
        Assert.Equal("IX_Name", index.Name);
        Assert.False(index.Unique);
        Assert.Equal([new IndexColumn("Name", true)], index.Columns);
        Assert.False(shape.Support.IdentityAndComputed);
        Assert.False(shape.Support.RowEstimate);
        Assert.Null(shape.RowEstimate);
    }

    [Fact]
    public async Task Composite_primary_keys_and_foreign_keys_come_back_ordered_with_actions()
    {
        var reader = await Open(FakeOdbc.SqlServerLike());
        var lines = await reader.GetShapeAsync(new TableRef("Shop", "dbo", "Lines"));
        Assert.Equal(["OrderId", "No"], lines.PrimaryKey!.Columns);
        var fk = Assert.Single(lines.ForeignKeys);
        Assert.Equal("FK_Lines_Orders", fk.Name);
        Assert.Equal(new TableRef("Shop", "dbo", "Orders"), fk.Referenced);
        Assert.Equal(ReferentialAction.NoAction, fk.OnUpdate);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
    }

    [Fact]
    public async Task Unsupported_driver_functions_set_flags_instead_of_looking_like_empty_results()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.PrimaryKeysUnsupported = true;
        fake.ForeignKeysUnsupported = true;
        fake.StatisticsUnsupported = true;
        var shape = await (await Open(fake)).GetShapeAsync(new TableRef("Shop", "dbo", "Orders"));

        Assert.False(shape.Support.PrimaryKeys);
        Assert.False(shape.Support.ForeignKeys);
        Assert.False(shape.Support.Indexes);
        Assert.Null(shape.PrimaryKey);
        Assert.Empty(shape.ForeignKeys);
        Assert.Equal(5, shape.Columns.Count);
    }

    [Fact]
    public async Task Views_do_not_ask_for_keys_or_indexes()
    {
        var fake = FakeOdbc.SqlServerLike();
        await (await Open(fake)).GetShapeAsync(new TableRef("Shop", "dbo", "vOrders", TableKind.View));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("SQLPrimaryKeys", StringComparison.Ordinal) || c.StartsWith("SQLStatistics", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_tables_are_errors()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.Columns.Clear();
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await (await Open(fake)).GetShapeAsync(new TableRef("Shop", "dbo", "Nope")));
    }

    [Fact]
    public async Task A_crawl_uses_one_session_with_one_columns_call_and_a_whole_catalog_of_shapes()
    {
        var fake = FakeOdbc.SqlServerLike();
        var reader = await Open(fake);
        fake.Calls.Clear();

        var shapes = await ToList(((ICatalogReader)reader).CrawlAsync());

        Assert.Equal(["Lines", "Orders", "vOrders"], shapes.Select(s => s.Table.Name));
        Assert.Single(fake.Calls, c => c.StartsWith("SQLColumns", StringComparison.Ordinal));
        Assert.Single(fake.Calls, c => c.StartsWith("SQLTables", StringComparison.Ordinal));
        Assert.Equal(["OrderId", "No"], shapes[0].PrimaryKey!.Columns);
        Assert.Equal(5, shapes[1].Columns.Count);
        Assert.Empty(shapes[2].ForeignKeys);
        Assert.Null(shapes[2].PrimaryKey);
    }

    [Fact]
    public async Task A_crawl_propagates_the_supported_flags()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.ForeignKeysUnsupported = true;
        var shapes = await ToList(((ICatalogReader)await Open(fake)).CrawlAsync());
        Assert.All(shapes, s => { Assert.False(s.Support.ForeignKeys); Assert.True(s.Support.PrimaryKeys); });
    }

    [Fact]
    public async Task A_truncated_catalog_call_becomes_a_warning_not_a_silent_cut()
    {
        var fake = FakeOdbc.SqlServerLike();
        var options = new OdbcCatalogOptions { MaxRows = 3, RowLimitBehavior = OdbcRowLimitBehavior.Truncate };
        var session = await OdbcCatalogSession.OpenOverAsync(fake, options, CancellationToken.None);
        var reader = await OdbcCatalogReader.CreateAsync(session, odbcOptions: options);

        var shapes = await ToList(((ICatalogReader)reader).CrawlAsync());

        Assert.All(shapes, s => Assert.Contains("cap", Assert.Single(s.WarningList)));
    }

    [Fact]
    public async Task Describe_uses_sqlprepare_and_maps_result_columns()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.Described =
        [
            new(1, "id", OdbcSqlType.Integer, 10, 0, false), new(2, "name", OdbcSqlType.WVarchar, 50, 0, true),
            new(3, "total", OdbcSqlType.Decimal, 18, 2, null), new(4, "d", OdbcSqlType.TypeDate, 10, 0, true),
        ];
        var shape = await (await Open(fake)).DescribeQueryAsync("SELECT id, name, total, d FROM dbo.Orders");

        Assert.Equal(DescribeStrategy.OdbcPrepare, shape.Strategy);
        Assert.Equal(["SELECT id, name, total, d FROM dbo.Orders"], fake.Prepared);
        Assert.Equal([CanonicalKind.Int32, CanonicalKind.String, CanonicalKind.Decimal, CanonicalKind.Date], shape.Columns.Select(c => c.Type.Kind));
        Assert.Equal("SQL_WVARCHAR", shape.Columns[1].NativeType);
        Assert.False(shape.Columns[0].Nullable);
        Assert.True(shape.Columns[2].Nullable);
    }

    [Fact]
    public async Task Describe_refuses_writes_before_the_driver_is_called()
    {
        var fake = FakeOdbc.SqlServerLike();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await (await Open(fake)).DescribeQueryAsync("UPDATE dbo.Orders SET Name = 'x'"));
        Assert.Empty(fake.Prepared);
    }

    [Fact]
    public async Task A_driver_that_cannot_describe_falls_back_to_the_zero_row_probe_when_a_connection_is_given()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.DescribeUnsupported = true;
        var probe = new FakeDbConnection(_ => new FakeResult(FakeResult.Rows(["id", "name"], [1, "a"]).Data.Clone()));

        var shape = await (await Open(fake, probe)).DescribeQueryAsync("SELECT id, name FROM dbo.Orders");

        Assert.Equal(DescribeStrategy.WrappedZeroRowProbe, shape.Strategy);
        Assert.Contains("WHERE 1 = 0", Assert.Single(probe.Log).Sql);
    }

    [Fact]
    public async Task Without_a_fallback_connection_an_undescribable_driver_reports_not_supported()
    {
        var fake = FakeOdbc.SqlServerLike();
        fake.DescribeUnsupported = true;
        await Assert.ThrowsAsync<NotSupportedException>(async () => await (await Open(fake)).DescribeQueryAsync("SELECT 1"));
    }
}

public class OdbcTypeMapperTests
{
    [Theory]
    [InlineData(OdbcSqlType.Integer, null, null, null, CanonicalKind.Int32, null)]
    [InlineData(OdbcSqlType.SmallInt, null, null, null, CanonicalKind.Int16, null)]
    [InlineData(OdbcSqlType.BigInt, null, null, null, CanonicalKind.Int64, null)]
    [InlineData(OdbcSqlType.Real, null, null, null, CanonicalKind.Float, null)]
    [InlineData(OdbcSqlType.Double, null, null, null, CanonicalKind.Double, null)]
    [InlineData(OdbcSqlType.Float, null, 53, null, CanonicalKind.Double, null)]
    [InlineData(OdbcSqlType.Float, null, 24, null, CanonicalKind.Float, null)]
    [InlineData(OdbcSqlType.Bit, null, null, null, CanonicalKind.Boolean, null)]
    [InlineData(OdbcSqlType.Guid, null, null, null, CanonicalKind.Guid, null)]
    [InlineData(OdbcSqlType.SqlServerXml, null, null, null, CanonicalKind.Xml, null)]
    [InlineData(OdbcSqlType.TypeDate, null, 10, null, CanonicalKind.Date, null)]
    [InlineData(OdbcSqlType.IntervalDayToSecond, null, null, null, CanonicalKind.Interval, null)]
    [InlineData(OdbcSqlType.SqlServerVariant, null, null, null, CanonicalKind.Unmappable, null)]
    [InlineData(OdbcSqlType.SqlServerUdt, null, null, null, CanonicalKind.Unmappable, null)]
    public void Fixed_types(int code, string? name, int? size, int? scale, CanonicalKind kind, string? _) =>
        Assert.Equal(kind, OdbcTypeMapper.ToCanonical(code, name, size, scale).Kind);

    [Fact]
    public void Character_types_carry_length_and_unicode()
    {
        Assert.Equal(CanonicalType.String(20, unicode: false), OdbcTypeMapper.ToCanonical(OdbcSqlType.Varchar, "varchar", 20, null));
        Assert.Equal(CanonicalType.String(20), OdbcTypeMapper.ToCanonical(OdbcSqlType.WVarchar, "nvarchar", 20, null));
        Assert.Equal(CanonicalType.String(), OdbcTypeMapper.ToCanonical(OdbcSqlType.WLongVarchar, "ntext", 1_073_741_823, null));
        Assert.Equal(CanonicalType.String(null, unicode: false), OdbcTypeMapper.ToCanonical(OdbcSqlType.Varchar, "varchar", int.MaxValue, null));
    }

    [Fact]
    public void Decimals_keep_precision_and_scale_and_unconstrained_numeric_is_unbounded()
    {
        Assert.Equal(CanonicalType.Decimal(18, 2), OdbcTypeMapper.ToCanonical(OdbcSqlType.Decimal, "decimal", 18, 2));
        Assert.Equal(CanonicalType.Decimal(10, 0), OdbcTypeMapper.ToCanonical(OdbcSqlType.Numeric, "numeric", 10, null));
        Assert.Equal(CanonicalType.Of(CanonicalKind.Decimal), OdbcTypeMapper.ToCanonical(OdbcSqlType.Numeric, "numeric", 131089, 0));
    }

    [Fact]
    public void Temporal_types_read_the_fraction_digits_from_scale_or_size()
    {
        Assert.Equal(CanonicalType.Timestamp(3), OdbcTypeMapper.ToCanonical(OdbcSqlType.TypeTimestamp, "datetime", 23, 3));
        Assert.Equal(CanonicalType.Timestamp(6), OdbcTypeMapper.ToCanonical(OdbcSqlType.TypeTimestamp, "timestamp", 26, null));
        Assert.Equal(CanonicalType.Timestamp(), OdbcTypeMapper.ToCanonical(OdbcSqlType.TypeTimestamp, "datetime", 19, null));
        Assert.Equal(CanonicalType.Timestamp(7, tz: true), OdbcTypeMapper.ToCanonical(OdbcSqlType.SqlServerTimestampOffset, "datetimeoffset", 34, 7));
        Assert.Equal(CanonicalType.Time(7), OdbcTypeMapper.ToCanonical(OdbcSqlType.SqlServerTime2, "time", 16, 7));
        Assert.Equal(CanonicalType.Timestamp(6, tz: true), OdbcTypeMapper.ToCanonical(OdbcSqlType.TypeTimestamp, "timestamptz", 29, 6));
    }

    [Fact]
    public void Type_names_decide_for_engine_types_odbc_has_no_code_for()
    {
        Assert.Equal(CanonicalKind.Json, OdbcTypeMapper.ToCanonical(OdbcSqlType.LongVarchar, "jsonb", null, null).Kind);
        Assert.Equal(CanonicalKind.Guid, OdbcTypeMapper.ToCanonical(OdbcSqlType.Varchar, "uuid", 36, null).Kind);
    }

    [Fact]
    public void Tinyint_is_unsigned_on_sql_server_and_signed_elsewhere()
    {
        var sqlServer = DialectCatalog.For(EngineId.SqlServer);
        var mysql = DialectCatalog.For(EngineId.MySql);
        Assert.Equal(new CanonicalType(CanonicalKind.Int8, Unsigned: true), OdbcTypeMapper.ToCanonical(OdbcSqlType.TinyInt, "tinyint", 3, null, sqlServer));
        Assert.Equal(CanonicalType.Of(CanonicalKind.Int8), OdbcTypeMapper.ToCanonical(OdbcSqlType.TinyInt, "tinyint", 3, null, mysql));
        Assert.Equal(new CanonicalType(CanonicalKind.Int8, Unsigned: true), OdbcTypeMapper.ToCanonical(OdbcSqlType.TinyInt, "tinyint unsigned", 3, null, mysql));
    }

    [Fact]
    public void Unknown_codes_fall_back_to_the_dialects_reading_of_the_type_name_then_unmappable()
    {
        var pg = DialectCatalog.For(EngineId.Postgres);
        Assert.Equal(CanonicalKind.Boolean, OdbcTypeMapper.ToCanonical(0, "boolean", null, null, pg).Kind);
        Assert.Equal(CanonicalKind.Unmappable, OdbcTypeMapper.ToCanonical(9999, "mystery", null, null, pg).Kind);
        Assert.Equal(CanonicalKind.Unmappable, OdbcTypeMapper.ToCanonical(null, null, null, null).Kind);
    }
}
