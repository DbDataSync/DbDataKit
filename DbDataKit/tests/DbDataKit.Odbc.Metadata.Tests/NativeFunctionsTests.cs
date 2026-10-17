using System.Text;

namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>The native layer (fetch loop, attributes, buffers, cancellation) against a scripted driver manager; no ODBC driver involved.</summary>
public class NativeFunctionsTests
{
    private static string?[][] Numbered(int count) => Enumerable.Range(0, count).Select(i => new string?[] { "db", "dbo", "t" + i, "TABLE", null }).ToArray();

    private static OdbcNativeFunctions Open(FakeOdbcApi api, OdbcCatalogOptions? options = null) => new("Driver={Fake};Server=x", options, api);

    [Fact]
    public async Task Rows_come_back_as_text_with_nulls_and_handles_are_freed()
    {
        var api = new FakeOdbcApi { Rows = [["a", null, ""]] };
        await using var functions = Open(api);

        var rows = await functions.TablesAsync(null, "dbo", null, null, CancellationToken.None);

        Assert.Equal([["a", null, ""]], rows.Select(r => r.ToArray()));
        Assert.Empty(api.OpenStatements);
        Assert.Equal("SQLTables", api.LastFunction);
    }

    [Fact]
    public async Task A_value_longer_than_the_fetch_buffer_is_read_in_pieces()
    {
        string big = new('x', 10_000);
        var api = new FakeOdbcApi { Rows = [[big]] };
        await using var functions = Open(api);

        var row = Assert.Single(await functions.ColumnsAsync(null, null, null, null, CancellationToken.None));
        Assert.Equal(big, row[0]);
    }

    [Fact]
    public async Task Login_and_statement_timeouts_are_set_from_the_options()
    {
        var api = new FakeOdbcApi { Rows = Numbered(1) };
        await using var functions = Open(api, new OdbcCatalogOptions { LoginTimeout = TimeSpan.FromSeconds(7), StatementTimeout = TimeSpan.FromMilliseconds(1500) });

        await functions.TablesAsync(null, null, null, null, CancellationToken.None);

        Assert.Contains((103, (nint)7), api.ConnectionAttributes);      // SQL_ATTR_LOGIN_TIMEOUT
        Assert.Equal([(0, (nint)2)], api.StatementAttributes);          // SQL_ATTR_QUERY_TIMEOUT, rounded up to whole seconds
    }

    [Fact]
    public async Task No_statement_timeout_means_the_attribute_is_not_set()
    {
        var api = new FakeOdbcApi { Rows = Numbered(1) };
        await using var functions = Open(api, new OdbcCatalogOptions { StatementTimeout = null });

        await functions.TablesAsync(null, null, null, null, CancellationToken.None);

        Assert.Empty(api.StatementAttributes);
    }

    [Fact]
    public async Task The_row_cap_stops_the_fetch_loop_and_says_so()
    {
        var api = new FakeOdbcApi { Rows = Numbered(100) };
        await using var functions = Open(api, new OdbcCatalogOptions { MaxRows = 10 });

        var rows = await functions.TablesAsync(null, null, null, null, CancellationToken.None);

        var raw = Assert.IsType<OdbcRows>(rows);
        Assert.True(raw.Truncated);
        Assert.Equal(10, raw.Count);
        Assert.True(api.FetchCalls <= 11);     // it did not read the whole result
        Assert.Empty(api.OpenStatements);
    }

    [Fact]
    public async Task A_result_exactly_at_the_cap_is_not_truncated()
    {
        var api = new FakeOdbcApi { Rows = Numbered(10) };
        await using var functions = Open(api, new OdbcCatalogOptions { MaxRows = 10 });

        var raw = Assert.IsType<OdbcRows>(await functions.TablesAsync(null, null, null, null, CancellationToken.None));
        Assert.False(raw.Truncated);
        Assert.Equal(10, raw.Count);
    }

    [Fact]
    public async Task A_catalog_over_the_native_layer_throws_or_flags_truncation_as_configured()
    {
        var api = new FakeOdbcApi { Rows = Numbered(50) };

        await using (var throwing = new OdbcCatalog(Open(api, new OdbcCatalogOptions { MaxRows = 5 }), new OdbcCatalogOptions { MaxRows = 5 }))
        {
            var ex = await Assert.ThrowsAsync<OdbcResultTruncatedException>(() => throwing.TablesAsync());
            Assert.Equal(new OdbcResultTruncated("SQLTables", 5), ex.Signal);
        }

        var truncating = new OdbcCatalogOptions { MaxRows = 5, RowLimitBehavior = OdbcRowLimitBehavior.Truncate };
        await using var catalog = new OdbcCatalog(Open(api, truncating), truncating);
        var tables = await catalog.TablesAsync();
        Assert.Equal(5, tables.Count);
        Assert.Equal(new OdbcResultTruncated("SQLTables", 5), tables.GetTruncation());
    }

    [Fact]
    public async Task Cancellation_calls_SQLCancel_and_surfaces_as_cancellation()
    {
        var api = new FakeOdbcApi { Rows = Numbered(5), BlockFetchUntilCancelled = true, FetchEntered = new ManualResetEventSlim() };
        await using var functions = Open(api);
        using var cts = new CancellationTokenSource();

        var call = functions.TablesAsync(null, null, null, null, cts.Token);
        Assert.True(api.FetchEntered!.Wait(TimeSpan.FromSeconds(10)));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, api.Cancels);
        Assert.Empty(api.OpenStatements);
    }

    [Fact]
    public async Task A_failed_catalog_call_reports_the_sqlstate_and_frees_the_statement()
    {
        var api = new FakeOdbcApi { CatalogResult = -1, State = "42S02" };
        await using var functions = Open(api);

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => functions.TablesAsync(null, null, null, null, CancellationToken.None));
        Assert.Equal(("SQLTables", "42S02"), (ex.Function, ex.SqlState));
        Assert.Empty(api.OpenStatements);
    }

    [Fact]
    public async Task A_failed_connect_is_not_reported_as_not_supported()
    {
        var api = new FakeOdbcApi { DriverConnectResult = -1, State = "IM002" };
        await using var functions = Open(api);

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => functions.TablesAsync(null, null, null, null, CancellationToken.None));
        Assert.Equal("SQLDriverConnect", ex.Function);
        Assert.False(ex.NotSupported);        // a missing data source must not look like "driver lacks SQLPrimaryKeys"
        Assert.True(new OdbcCatalogException("SQLPrimaryKeys", "IM002").NotSupported);
    }

    [Fact]
    public async Task The_connection_is_made_once_and_closed_on_dispose()
    {
        var api = new FakeOdbcApi { Rows = Numbered(2) };
        var functions = Open(api);

        for (int i = 0; i < 5; i++) await functions.TablesAsync(null, null, null, null, CancellationToken.None);
        Assert.Equal(1, api.Connects);

        await functions.DisposeAsync();
        Assert.Equal(1, api.Disconnects);
    }

    // ---- SQLGetInfo -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_short_info_string_is_read_in_one_call()
    {
        var api = new FakeOdbcApi { Info = { [17] = "Microsoft SQL Server" } };
        await using var functions = Open(api);

        Assert.Equal("Microsoft SQL Server", await functions.GetInfoStringAsync(17, CancellationToken.None));
    }

    [Fact]
    public async Task A_long_info_string_is_retried_with_the_reported_length_not_cut_at_1_KB()
    {
        string longValue = new string('v', 3000);   // 6000 bytes of UTF-16
        var api = new FakeOdbcApi { Info = { [17] = "name", [999] = longValue } };
        await using var functions = Open(api);

        Assert.Equal(longValue, await functions.GetInfoStringAsync(999, CancellationToken.None));
        // 512 first, then exactly what the driver reported plus the terminator.
        Assert.Equal([512, 6002], api.InfoBufferSizes.Skip(api.InfoBufferSizes.Count - 2));
    }

    [Fact]
    public async Task An_info_string_too_long_for_a_SQLSMALLINT_length_is_an_error_not_a_silent_cut()
    {
        var api = new FakeOdbcApi { Info = { [17] = "name", [999] = new string((char)118, 16_383) } };   // 32,766 bytes: with the terminator it no longer fits a SQLSMALLINT
        await using var functions = Open(api);

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => functions.GetInfoStringAsync(999, CancellationToken.None));
        Assert.Equal("01004", ex.SqlState);
    }

    // ---- SQLWCHAR width ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_four_byte_SQLWCHAR_driver_manager_fails_with_a_clear_message_and_disconnects()
    {
        var api = new FakeOdbcApi { InfoAsUtf32 = true, Info = { [17] = "Microsoft SQL Server" } };
        await using var functions = Open(api);

        var ex = await Assert.ThrowsAsync<OdbcConfigurationException>(() => functions.TablesAsync(null, null, null, null, CancellationToken.None));
        Assert.Contains("4-byte SQLWCHAR", ex.Message);
        Assert.Equal(1, api.Disconnects);
    }

    [Theory]
    [InlineData("Microsoft SQL Server", false)]
    [InlineData("a", false)]
    public void Utf16_strings_are_not_mistaken_for_utf32(string text, bool expected)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        Assert.Equal(expected, OdbcWideChar.LooksLikeFourByte(bytes, bytes.Length));
    }

    [Theory]
    [InlineData("Microsoft SQL Server")]
    [InlineData("a")]
    public void Utf32_strings_are_recognised(string text)
    {
        byte[] bytes = Encoding.UTF32.GetBytes(text);
        Assert.True(OdbcWideChar.LooksLikeFourByte(bytes, bytes.Length));
    }

    // ---- Streaming --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Streaming_is_not_capped_and_frees_the_statement_when_done()
    {
        var api = new FakeOdbcApi { Rows = Numbered(1000) };
        await using var functions = Open(api, new OdbcCatalogOptions { MaxRows = 10 });

        int count = 0;
        await foreach (var _ in functions.StreamColumnsAsync(null, null, null, null, CancellationToken.None)) count++;

        Assert.Equal(1000, count);
        Assert.Empty(api.OpenStatements);
    }

    [Fact]
    public async Task Stopping_a_stream_early_frees_the_statement_and_releases_the_connection()
    {
        var api = new FakeOdbcApi { Rows = Numbered(1000) };
        await using var functions = Open(api);

        await foreach (var _ in functions.StreamTablesAsync(null, null, null, null, CancellationToken.None)) break;
        Assert.Empty(api.OpenStatements);

        // the connection is usable again (the serialising lock was released)
        Assert.Equal(1000, (await functions.TablesAsync(null, null, null, null, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task The_typed_streams_match_names_ignoring_case_without_buffering()
    {
        var api = new FakeOdbcApi
        {
            Info = { [14] = "\\" },
            Rows = [[null, "DBO", "ORDERS", "TABLE", null], [null, "DBO", "OTHER", "TABLE", null]],
        };
        await using var catalog = new OdbcCatalog(Open(api));

        var found = new List<OdbcTable>();
        await foreach (var table in catalog.StreamTablesAsync(schema: "dbo", table: "orders")) found.Add(table);

        Assert.Equal("ORDERS", Assert.Single(found).Name);
    }

    // ---- Session ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_session_crawls_a_schema_over_one_connection()
    {
        var api = new FakeOdbcApi { Rows = [[null, "dbo", "t", "TABLE", null]], Info = { [17] = "Fake DB", [29] = "\"" } };
        var functions = Open(api);

        await using var session = await OdbcCatalogSession.OpenOverAsync(functions, null, CancellationToken.None);
        var crawl = await session.CrawlAsync(schema: "dbo");
        await session.Catalog.TablesAsync();
        await session.Catalog.TablesAsync();

        Assert.Equal(1, api.Connects);
        Assert.Equal("Fake DB", session.Info.DbmsName);
        Assert.Equal("t", Assert.Single(crawl.Tables).Name);
        Assert.True(api.StatementsOpened > 3);   // many statements, still one connection
    }

    [Fact]
    public async Task A_session_fails_at_open_not_at_first_use_and_cleans_up()
    {
        var api = new FakeOdbcApi { DriverConnectResult = -1, State = "28000" };

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => OdbcCatalogSession.OpenOverAsync(Open(api), null, CancellationToken.None));
        Assert.Equal("28000", ex.SqlState);
    }

    [Fact]
    public async Task A_crawl_reports_unsupported_key_functions_instead_of_failing()
    {
        var fake = new FakeFunctions
        {
            Tables = [[null, "dbo", "t", "TABLE", null]],
            Columns = [[null, "dbo", "t", "c", "4", "int", "10", "4", "0", "10", "1", null, null, "4", null, null, "1", "YES"]],
            PrimaryKeysUnsupported = true,
        };
        await using var session = await OdbcCatalogSession.OpenOverAsync(fake, null, CancellationToken.None);

        var crawl = await session.CrawlAsync(schema: "dbo");

        Assert.False(crawl.PrimaryKeysSupported);
        Assert.Equal("c", Assert.Single(crawl.Columns).Name);
        Assert.True(crawl.ForeignKeysSupported);
    }
}
