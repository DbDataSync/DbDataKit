using DbDataKit.Catalog;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Scripting;
using DbDataSync.TaskRunner.Runner;
using Microsoft.Data.Sqlite;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// The set's own <c>discover</c> and <c>describe</c> over a connection whose driver is Sync's own: a YAML driver (<c>drivers/sqlite.generic/driver.yaml</c>) on SQLite, with its catalog read by its own queries. The answer has the
/// shape the Runner's built-in package gives (one connection of <c>catalog-cache/2</c>, which the cache's strict reader accepts), and a description gives the query's columns and reads no row. SQLite needs no server, so this runs everywhere.
/// </summary>
public sealed class SetCatalogReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-setcatalog-").FullName;
    private readonly string _database;

    public SetCatalogReadTests()
    {
        _database = Path.Combine(_root, "lite.db");
        using (var connection = new SqliteConnection($"Data Source={_database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE items (id INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, price NUMERIC);
                CREATE TABLE notes (body TEXT);
                INSERT INTO items VALUES (1, 'apple', 2.5), (2, 'pear', 3.0);
                """;
            command.ExecuteNonQuery();
        }

        // the provider is the test process's own Microsoft.Data.Sqlite: the library names its factory, and nothing is restored
        Write("libraries/microsoft-data-sqlite/library.csx", """
            // factory: Microsoft.Data.Sqlite.SqliteFactory, Microsoft.Data.Sqlite
            #r "nuget: Microsoft.Data.Sqlite, 10.0.11"
            """);
        Write("drivers/sqlite.generic/driver.yaml", """
            id: sqlite.generic
            displayName: SQLite (generic)
            library: microsoft-data-sqlite
            dialect:
              quoteIdentifier: doubleQuote
              parameterPrefix: "@"
              rowLimit: limitOffset
              catalog: query
              connectionStringKeys:
                database: Data Source
                connectTimeout: Default Timeout
            metadataQueries:
              tableQuery: "SELECT 'main' AS table_schema, name AS table_name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"
              columnQuery: "SELECT name AS column_name, type AS data_type, CASE WHEN \"notnull\" = 1 OR pk > 0 THEN 'NO' ELSE 'YES' END AS is_nullable, pk > 0 AS is_primary_key FROM pragma_table_info({{table}}) ORDER BY cid"
            capabilities:
              readers: [BatchReload]
              staging: []
              writers: []
            """);
        Write("config/connections/lite.yaml", $"""
            schema: connection/1
            name: lite
            engine: sqlite.generic
            connection_string: 'Mode=ReadWrite'
            auth:
              mode: none
            extensions:
              sync:
                database: '{_database}'
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a temp folder */ }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private SetCatalogRead Read()
    {
        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(_root, "config"), secrets);
        var engine = EngineComposition.Compose(config, _root, new ScriptCacheDirectory(Path.Combine(_root, ".cache")));
        return new SetCatalogRead(config, new RunLoginConnections(config, engine.Drivers, engine.Libraries, _root, secrets), new ScriptedMetadata(engine.Scripts, config));
    }

    [Fact]
    public async Task Discover_OfAYamlDriverSqliteConnection_ReturnsItsTablesAndColumns_AsACatalogCacheConnection()
    {
        var answer = await Read().DiscoverAsync("lite", null, null, null, CancellationToken.None);

        Assert.Equal("sqlite.generic", answer.Engine);
        Assert.Equal([("main", "items"), ("main", "notes")], answer.Tables.Select(t => (t.Schema, t.Name)).Order());
        var items = answer.Tables.Single(t => t.Name == "items");
        Assert.Equal(["id", "name", "price"], items.Columns.Select(c => c.Name));
        Assert.Equal([("integer", false, true), ("text", false, false), ("numeric", true, false)], items.Columns.Select(c => (c.Type, c.Nullable, c.Key)));
        Assert.Equal("main", items.Database);
        Assert.Equal([new CacheDatabase("main", ["main"])], answer.Databases, new DatabaseComparer());

        // the shape a console reads: one connection of catalog-cache/2, which the strict reader accepts once the console records where it was read
        var json = CatalogCache.ToJson(answer);
        json.Remove("read");
        var entry = CatalogCache.FromAnswer(json, new CacheRead("runners-eu", "runner", "dev", "2026-11-25T10:00:00Z"));
        var text = CatalogCache.Write(CatalogCache.WithConnection(null, "lite", entry, DateTimeOffset.Parse("2026-11-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        var load = CatalogCache.Load(text);
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics));
        Assert.Equal(2, load.Cache!.Connections["lite"].Tables.Count);
        Assert.DoesNotContain("apple", text, StringComparison.Ordinal);   // shape only, never a row
    }

    [Fact]
    public async Task Discover_ReadsOneTableOfOneSchema_OrTheTablesAPatternMatches()
    {
        var one = await Read().DiscoverAsync("lite", ["main"], null, "notes", CancellationToken.None);
        Assert.Equal(["notes"], one.Tables.Select(t => t.Name));
        Assert.Equal([new CacheDatabase("main", ["main"])], one.Databases, new DatabaseComparer());

        var matched = await Read().DiscoverAsync("lite", null, "it%", null, CancellationToken.None);
        Assert.Equal(["items"], matched.Tables.Select(t => t.Name));

        var none = await Read().DiscoverAsync("lite", ["other"], null, null, CancellationToken.None);
        Assert.Empty(none.Tables);

        await Assert.ThrowsAsync<ArgumentException>(() => Read().DiscoverAsync("lite", null, null, "notes", CancellationToken.None));
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => Read().DiscoverAsync("nowhere", null, null, null, CancellationToken.None));
        Assert.Contains("nowhere", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_OfAQuery_GivesItsColumnsAndNoRow_AndAQueryThatWritesIsRefusedBeforeTheConnectionOpens()
    {
        var answer = await Read().DescribeAsync("lite", "SELECT id, name AS label FROM items WHERE price > 1", CancellationToken.None);

        Assert.Equal("sqlite.generic", answer.Engine);
        Assert.Equal(["id", "label"], answer.Columns.Select(c => c.Name));
        Assert.All(answer.Columns, c => Assert.False(string.IsNullOrEmpty(c.Type)));
        Assert.Equal(nameof(DbDataKit.Metadata.DescribeStrategy.WrappedZeroRowProbe), answer.Strategy);
        Assert.DoesNotContain("apple", System.Text.Json.JsonSerializer.Serialize(answer), StringComparison.Ordinal);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Read().DescribeAsync("lite", "DELETE FROM items", CancellationToken.None));
        Assert.Contains("read-only", refused.Message, StringComparison.Ordinal);
        await using var check = new SqliteConnection($"Data Source={_database}");
        await check.OpenAsync();
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM items";
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
    }

    private sealed class DatabaseComparer : IEqualityComparer<CacheDatabase>
    {
        public bool Equals(CacheDatabase? x, CacheDatabase? y) => x is not null && y is not null && x.Name == y.Name && x.Schemas.SequenceEqual(y.Schemas);

        public int GetHashCode(CacheDatabase obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }
}
