namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>Type info, procedures, the row cap through the typed layer, and the fakes' default interface members.</summary>
public class CatalogAdditionsTests
{
    /// <summary>Extends the shared fake with the newer catalog functions.</summary>
    private sealed class RichFake : IOdbcCatalogFunctions
    {
        public readonly FakeFunctions Inner = new();
        public List<string?[]> Types = [], Procedures = [], ProcedureColumns = [];

        public Task<IReadOnlyList<string?[]>> TablesAsync(string? c, string? s, string? t, string? ty, CancellationToken ct) => Inner.TablesAsync(c, s, t, ty, ct);
        public Task<IReadOnlyList<string?[]>> ColumnsAsync(string? c, string? s, string? t, string? col, CancellationToken ct) => Inner.ColumnsAsync(c, s, t, col, ct);
        public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string? c, string? s, string t, CancellationToken ct) => Inner.PrimaryKeysAsync(c, s, t, ct);
        public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string? c, string? s, string t, CancellationToken ct) => Inner.ForeignKeysAsync(c, s, t, ct);
        public Task<IReadOnlyList<string?[]>> StatisticsAsync(string? c, string? s, string t, CancellationToken ct) => Inner.StatisticsAsync(c, s, t, ct);
        public Task<string?> GetInfoStringAsync(ushort i, CancellationToken ct) => Inner.GetInfoStringAsync(i, ct);
        public Task<uint> GetInfoNumberAsync(ushort i, CancellationToken ct) => Inner.GetInfoNumberAsync(i, ct);
        public Task<IReadOnlyList<string?[]>> TypeInfoAsync(int sqlType, CancellationToken ct) => Task.FromResult<IReadOnlyList<string?[]>>(Types);
        public Task<IReadOnlyList<string?[]>> ProceduresAsync(string? c, string? s, string? p, CancellationToken ct) => Task.FromResult<IReadOnlyList<string?[]>>(Procedures);
        public Task<IReadOnlyList<string?[]>> ProcedureColumnsAsync(string? c, string? s, string? p, string? col, CancellationToken ct) => Task.FromResult<IReadOnlyList<string?[]>>(ProcedureColumns);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Type_info_rows_become_typed_records()
    {
        var fake = new RichFake
        {
            Types =
            [
                // NAME TYPE SIZE PREFIX SUFFIX PARAMS NULLABLE CASE SEARCH UNSIGNED FIXED AUTO LOCAL MINSC MAXSC
                ["varchar", "12", "8000", "'", "'", "max length", "1", "0", "3", null, "0", null, "varchar", null, null, "12", null, null, null],
                ["int identity", "4", "10", null, null, null, "0", "0", "2", "0", "0", "1", "int identity", "0", "0", "4", null, "10", null],
                [null, "4", "1", null, null, null, "1", "0", "3", null, "0", null, null, null, null],   // no name: skipped
            ],
        };
        var types = await new OdbcCatalog(fake).TypeInfoAsync();

        Assert.Equal(["varchar", "int identity"], types.Select(t => t.TypeName));
        var varchar = types[0];
        Assert.Equal((OdbcSqlType.Varchar, 8000, "'", "max length", true, OdbcSearchable.Searchable), (varchar.SqlType, varchar.ColumnSize, varchar.LiteralPrefix, varchar.CreateParams, varchar.Nullable, varchar.Searchable));
        Assert.Null(varchar.AutoUnique);
        var identity = types[1];
        Assert.False(identity.Nullable);
        Assert.Equal((false, true, OdbcSearchable.AllExceptLike), (identity.Unsigned, identity.AutoUnique, identity.Searchable));
    }

    [Fact]
    public async Task A_driver_without_the_newer_functions_reports_not_supported_through_the_default_members()
    {
        // FakeFunctions implements none of the added members: they fall back to the interface defaults.
        var catalog = new OdbcCatalog(new FakeFunctions());

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => catalog.TypeInfoAsync());
        Assert.True(ex.NotSupported);
        Assert.Equal("SQLGetTypeInfo", ex.Function);
        Assert.True((await Assert.ThrowsAsync<OdbcCatalogException>(() => catalog.ProceduresAsync())).NotSupported);
        Assert.True((await Assert.ThrowsAsync<OdbcCatalogException>(() => catalog.ProcedureColumnsAsync())).NotSupported);
    }

    [Fact]
    public async Task Procedures_lose_their_version_suffix_and_carry_counts()
    {
        var fake = new RichFake
        {
            Procedures =
            [
                // CAT SCHEM NAME IN OUT RESULTS REMARKS TYPE
                ["db", "dbo", "usp_load;1", "2", "1", "0", null, "1"],
                ["db", "dbo", "fn_total;1", "1", "0", "0", "sums", "2"],
            ],
        };
        var procedures = await new OdbcCatalog(fake).ProceduresAsync(schema: "dbo");

        Assert.Equal(["usp_load", "fn_total"], procedures.Select(p => p.Name));
        Assert.Equal((2, 1, OdbcProcedureKind.NoResult), (procedures[0].InputParameters, procedures[0].OutputParameters, procedures[0].Kind));
        Assert.Equal(("sums", OdbcProcedureKind.ReturnsValue), (procedures[1].Remarks, procedures[1].Kind));
    }

    [Fact]
    public async Task A_name_that_merely_contains_a_semicolon_is_kept_whole()
    {
        var fake = new RichFake { Procedures = [["db", "dbo", "odd;name", null, null, null, null, "1"]] };
        Assert.Equal("odd;name", Assert.Single(await new OdbcCatalog(fake).ProceduresAsync()).Name);
    }

    [Fact]
    public async Task Procedure_columns_carry_direction_type_and_position()
    {
        var fake = new RichFake
        {
            ProcedureColumns =
            [
                // CAT SCHEM PROC COLUMN COLTYPE DATATYPE TYPENAME SIZE BUFLEN DECDIG RADIX NULLABLE REMARKS DEFAULT SQLTYPE SUBTYPE OCTET ORDINAL ISNULLABLE
                ["db", "dbo", "usp_load;1", "@out", "4", "4", "int", "10", "4", "0", "10", "1", null, null, "4", null, null, "2", "YES"],
                ["db", "dbo", "usp_load;1", "@id", "1", "4", "int", "10", "4", "0", "10", "0", "key", "0", "4", null, null, "1", "NO"],
                ["db", "dbo", "usp_load;1", "RETURN_VALUE", "5", "4", "int", "10", "4", "0", "10", "0", null, null, "4", null, null, "0", "NO"],
            ],
        };
        var columns = await new OdbcCatalog(fake).ProcedureColumnsAsync(schema: "dbo", procedure: "usp_load");

        Assert.Equal(["RETURN_VALUE", "@id", "@out"], columns.Select(c => c.Name));
        Assert.Equal([OdbcParameterKind.ReturnValue, OdbcParameterKind.Input, OdbcParameterKind.Output], columns.Select(c => c.Kind));
        Assert.Equal("usp_load", columns[0].Procedure);
        Assert.False(columns[1].Nullable);
        Assert.Equal(("key", "0"), (columns[1].Remarks, columns[1].Default));
        Assert.True(columns[2].Nullable);
    }

    [Fact]
    public async Task The_typed_layer_caps_rows_from_any_functions_implementation()
    {
        var fake = new FakeFunctions { Tables = Enumerable.Range(0, 20).Select(i => new string?[] { null, "dbo", "t" + i, "TABLE", null }).ToList() };

        await Assert.ThrowsAsync<OdbcResultTruncatedException>(() => new OdbcCatalog(fake, new OdbcCatalogOptions { MaxRows = 5 }).TablesAsync());

        var tables = await new OdbcCatalog(fake, new OdbcCatalogOptions { MaxRows = 5, RowLimitBehavior = OdbcRowLimitBehavior.Truncate }).TablesAsync();
        Assert.Equal(5, tables.Count);
        Assert.Equal(5, tables.GetTruncation()!.MaxRows);

        var all = await new OdbcCatalog(fake, new OdbcCatalogOptions { MaxRows = null }).TablesAsync();
        Assert.Equal(20, all.Count);
        Assert.Null(all.GetTruncation());
    }

    [Fact]
    public async Task Default_streaming_falls_back_to_the_buffered_call()
    {
        var fake = new FakeFunctions { Tables = [[null, "dbo", "a", "TABLE", null], [null, "dbo", "b", "TABLE", null]] };
        var names = new List<string>();
        await foreach (var t in new OdbcCatalog(fake, new OdbcCatalogOptions { MaxRows = 1 }).StreamTablesAsync()) names.Add(t.Name);
        Assert.Equal(["a", "b"], names);   // streams are not capped
    }

    [Fact]
    public void Sql_type_names_cover_the_codes_consumers_map()
    {
        Assert.Equal("SQL_VARCHAR", OdbcSqlType.Name(OdbcSqlType.Varchar));
        Assert.Equal("SQL_WVARCHAR", OdbcSqlType.Name(-9));
        Assert.Equal("SQL_TYPE_TIMESTAMP", OdbcSqlType.Name(93));
        Assert.Equal("SQL_SS_TIMESTAMPOFFSET", OdbcSqlType.Name(-155));
        Assert.Equal("SQL_TYPE_424242", OdbcSqlType.Name(424242));
    }
}
