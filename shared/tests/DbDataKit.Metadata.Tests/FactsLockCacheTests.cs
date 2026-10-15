using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;

namespace DbDataKit.Metadata.Tests;

public class ServerFactsTests
{
    [Fact]
    public void Odbc_driver_info_becomes_facts_with_case_and_quote_rules()
    {
        var info = new OdbcDriverInfo("PostgreSQL", "16.2", "psqlodbcw.so", new OdbcIdentifierQuote("\"", "\""), OdbcIdentifierCase.Lower, OdbcIdentifierCase.Sensitive, "\\", ".", "catalog", "schema");
        var facts = ServerFactsProbe.FromOdbc(info, 63, 0);

        Assert.Equal("PostgreSQL", facts.ProductName);
        Assert.Equal(IdentifierCase.Lower, facts.StoredCase);
        Assert.Equal(IdentifierCase.Preserve, facts.QuotedCase);
        Assert.Equal(63, facts.MaxIdentifierLength);
        Assert.False(facts.SupportsMultipleStatements);
        Assert.True(facts.SupportsLimit);
        Assert.False(facts.SupportsTop);
        Assert.True(facts.HasQuoteCharacter);
    }

    [Fact]
    public void A_driver_without_a_quote_character_gives_a_dialect_that_cannot_quote()
    {
        var info = new OdbcDriverInfo("Text Files", "1", "odbcjt32.dll", null, OdbcIdentifierCase.Mixed, OdbcIdentifierCase.Mixed, null, null, null, null);
        var facts = ServerFactsProbe.FromOdbc(info);
        Assert.False(facts.HasQuoteCharacter);

        var dialect = DialectProfiles.ToDialect(facts);
        Assert.Equal("odbc:text_files", dialect.Id.Value);
        Assert.False(CatalogNames.DialectQuotes(dialect));
        Assert.Equal("none", DialectProfiles.ToProfile(facts).Quote);
    }

    [Fact]
    public void Sql_server_old_versions_have_top_but_not_offset_fetch()
    {
        Assert.Equal((true, false, false), DialectProfiles.RowLimitFor("Microsoft SQL Server", "10.50.1600"));
        Assert.Equal((true, false, true), DialectProfiles.RowLimitFor("Microsoft SQL Server", "16.00.1000"));
        Assert.Equal((null, null, null), DialectProfiles.RowLimitFor("Mystery", null));
    }

    [Theory]
    [InlineData("Microsoft SQL Server", "sqlserver")]
    [InlineData("PostgreSQL", "postgres")]
    [InlineData("MariaDB", "mysql")]
    [InlineData("MySQL", "mysql")]
    [InlineData("Oracle", "oracle")]
    [InlineData("DuckDB", "duckdb")]
    [InlineData("Microsoft Fabric", "fabric")]
    public void Known_products_use_builtin_dialects(string product, string id) =>
        Assert.Equal(id, DialectProfiles.ToDialect(new ServerFacts(product)).Id.Value);

    [Fact]
    public void Facts_from_a_dialect_round_trip_through_the_dialect_chooser()
    {
        foreach (var id in new[] { EngineId.SqlServer, EngineId.Postgres, EngineId.MySql, EngineId.DuckDb })
        {
            var dialect = DialectCatalog.For(id);
            var facts = ServerFactsProbe.FromDialect(dialect, null, "1.0");
            Assert.Equal(dialect.Quote("x"), facts.QuoteOpen + "x" + facts.QuoteClose);
            Assert.Equal(dialect.Identifiers.MaxLength, facts.MaxIdentifierLength);
        }
    }

    [Fact]
    public void The_generic_dialect_from_facts_uses_the_facts_row_limit_style()
    {
        var facts = new ServerFacts("Acme", QuoteOpen: "\"", QuoteClose: "\"", SupportsLimit: true, SupportsOffsetFetch: false, MaxIdentifierLength: 30, StoredCase: IdentifierCase.Upper);
        var dialect = DialectProfiles.ToDialect(facts);
        Assert.Equal(RowLimitStyle.LimitOffset, dialect.DefaultRowLimitStyle);
        Assert.Equal(30, dialect.Identifiers.MaxLength);
        Assert.Equal(IdentifierCase.Upper, dialect.Identifiers.Fold);
    }
}

public class DialectLockFileTests
{
    private static DialectLockEntry Entry(string name, ServerFacts facts) => DialectLockEntry.From(name, facts);

    private static readonly ServerFacts SqlServer = new("Microsoft SQL Server", "16.00.1000", "msodbcsql18.dll", "[", "]", IdentifierCase.Preserve, IdentifierCase.Preserve, 128, ".", "database", "owner", false, true, false, true, true);
    private static readonly ServerFacts Acme = new("Acme \"DB\"", "1.2", QuoteOpen: "`", QuoteClose: "`", MaxIdentifierLength: 30, SupportsLimit: true, StoredCase: IdentifierCase.Upper);

    [Fact]
    public void Render_is_deterministic_sorted_lf_only_and_free_of_machine_details()
    {
        string a = DialectLockFile.Render([Entry("zeta", Acme), Entry("alpha", SqlServer)]);
        string b = DialectLockFile.Render([Entry("alpha", SqlServer), Entry("zeta", Acme)]);
        Assert.Equal(a, b);
        Assert.DoesNotContain('\r', a);
        Assert.True(a.IndexOf("  alpha:", StringComparison.Ordinal) < a.IndexOf("  zeta:", StringComparison.Ordinal));
        Assert.Contains("profile_hash:", a);
        Assert.Contains("dialect: sqlserver", a);
    }

    [Fact]
    public void A_file_round_trips_including_awkward_strings()
    {
        var entries = new[] { Entry("alpha", SqlServer), Entry("zeta: the \"second\"", Acme), Entry("min", new ServerFacts("Mystery")) };
        string text = DialectLockFile.Render(entries);

        var back = DialectLockFile.Parse(text);

        Assert.Equal(["alpha", "min", "zeta: the \"second\""], back.Select(e => e.Connection));
        var byName = back.ToDictionary(e => e.Connection);
        Assert.Equal(SqlServer, byName["alpha"].Reported);
        Assert.Equal(Acme, byName["zeta: the \"second\""].Reported);
        Assert.Equal(entries[1].Profile, byName["zeta: the \"second\""].Profile);
        Assert.Equal(DialectLockFile.Render(back), text);
    }

    [Fact]
    public void An_empty_file_round_trips()
    {
        string text = DialectLockFile.Render([]);
        Assert.Empty(DialectLockFile.Parse(text));
    }

    [Fact]
    public void Parsed_profiles_resolve_back_to_the_same_dialect()
    {
        var back = DialectLockFile.Parse(DialectLockFile.Render([Entry("c", Acme)])).Single();
        var dialect = DialectProfiles.ToDialect(back.Reported);
        Assert.Equal(back.Profile.Dialect, dialect.Id.Value);
        Assert.Equal(back.Profile, DialectProfile.Of(dialect));
    }

    [Fact]
    public void A_hand_edited_profile_is_rejected_by_its_hash()
    {
        string text = DialectLockFile.Render([Entry("alpha", SqlServer)]).Replace("quote: \"[]\"", "quote: \"\\\"\"", StringComparison.Ordinal);
        var ex = Assert.Throws<DialectLockException>(() => DialectLockFile.Parse(text));
        Assert.Contains("profile_hash", ex.Message);
    }

    [Theory]
    [InlineData("format: 2\nconnections: {}\n", "format")]
    [InlineData("format: 1\n", "missing key 'connections'")]
    [InlineData("format: 1\nconnections: {}\nextra: 1\n", "unknown key 'extra'")]
    [InlineData("- a\n- b\n", "not a mapping")]
    [InlineData("format: [1\n", "not valid YAML")]
    public void Damaged_files_are_errors_never_guesses(string text, string fragment)
    {
        var ex = Assert.Throws<DialectLockException>(() => DialectLockFile.Parse(text));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Unknown_keys_inside_an_entry_are_errors()
    {
        string text = DialectLockFile.Render([Entry("alpha", SqlServer)]).Replace("    dialect:", "    colour: red\n    dialect:", StringComparison.Ordinal);
        Assert.Throws<DialectLockException>(() => DialectLockFile.Parse(text));
    }

    [Fact]
    public void Values_that_look_like_other_yaml_types_are_quoted()
    {
        Assert.Equal("\"true\"", DialectLockFile.Scalar("true"));
        Assert.Equal("\"16.0\"", DialectLockFile.Scalar("16.0"));
        Assert.Equal("plain_word", DialectLockFile.Scalar("plain_word"));
        Assert.Equal("\"a\\nb\"", DialectLockFile.Scalar("a\nb"));
    }
}

public class CacheTests
{
    private sealed class CountingReader : ICatalogReader
    {
        public int Shapes, Facts;
        public CatalogCapabilities Capabilities => new();
        public Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default) { Facts++; return Task.FromResult(new ServerFacts("x")); }
        public async IAsyncEnumerable<string> ListCatalogsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield return "c"; }
        public async IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new TableRef(null, "s", "a");
            yield return new TableRef(null, "s", "b");
        }

        public Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default)
        {
            Shapes++;
            return Task.FromResult(new TableShape(table, [], null, [], [], Shapes, ShapeSupport.Full));
        }

        public Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default) => Task.FromResult(new QueryShape([], DescribeStrategy.None));
    }

    [Fact]
    public async Task Shapes_are_cached_until_the_epoch_moves()
    {
        var inner = new CountingReader();
        var cache = new MemoryCatalogCache();
        var reader = new CachingCatalogReader(inner, cache, "fp1");
        var table = new TableRef(null, "s", "a");

        var first = await reader.GetShapeAsync(table);
        var second = await reader.GetShapeAsync(table);
        Assert.Same(first, second);
        Assert.Equal(1, inner.Shapes);

        Assert.Equal(1, reader.Invalidate());
        var third = await reader.GetShapeAsync(table);
        Assert.NotSame(first, third);
        Assert.Equal(2, inner.Shapes);
        Assert.Equal(1, cache.Count);   // the stale entry was dropped, not just shadowed
    }

    [Fact]
    public async Task Different_fingerprints_do_not_share_entries_and_invalidate_is_per_fingerprint()
    {
        var inner = new CountingReader();
        var cache = new MemoryCatalogCache();
        var a = new CachingCatalogReader(inner, cache, "a");
        var b = new CachingCatalogReader(inner, cache, "b");
        var table = new TableRef(null, "s", "t");

        await a.GetShapeAsync(table);
        await b.GetShapeAsync(table);
        Assert.Equal(2, inner.Shapes);

        a.Invalidate();
        await b.GetShapeAsync(table);
        Assert.Equal(2, inner.Shapes);
        await a.GetShapeAsync(table);
        Assert.Equal(3, inner.Shapes);
    }

    [Fact]
    public async Task A_result_computed_before_an_invalidation_is_not_stored()
    {
        var cache = new MemoryCatalogCache();
        var key = new CatalogCacheKey("fp", cache.GetEpoch("fp"), "x");
        cache.Invalidate("fp");
        cache.Set(key, "stale");
        Assert.False(cache.TryGet<string>(key, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task A_crawl_refreshes_the_cache_and_facts_are_cached()
    {
        var inner = new CountingReader();
        var reader = new CachingCatalogReader(inner, new MemoryCatalogCache(), "fp");

        var shapes = new List<TableShape>();
        await foreach (var s in ((ICatalogReader)reader).CrawlAsync()) shapes.Add(s);
        Assert.Equal(2, inner.Shapes);

        await reader.GetShapeAsync(new TableRef(null, "s", "a"));
        Assert.Equal(2, inner.Shapes);

        await reader.GetServerFactsAsync();
        await reader.GetServerFactsAsync();
        Assert.Equal(1, inner.Facts);
    }
}
