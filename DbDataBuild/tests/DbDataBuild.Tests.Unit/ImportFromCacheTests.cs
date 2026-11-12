using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// `project import --from-cache` (plan 33 SC20k): the mapped models are made from the catalog cache of the project (`catalog-cache.yaml`, `catalog-cache/1`) with no connection and no login, by the same code that describes a live table
/// (`MappedModelImport`), so what the cache cannot say (indexes, foreign keys) is said and what a person committed is kept. The type mapping is the one of the live catalog, read from the cache's type text per engine.
/// </summary>
public class ImportFromCacheTests : IDisposable
{
    private readonly string dir = NewProjectDir();

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        TestFiles.WriteAllText(p, text);
    }

    private void Project(string engine = "postgres")
    {
        Write("dbdatabuild.yml", "defaults: {connections: [warehouse]}\ntracking: { connection: warehouse }\n");
        Write("connections/warehouse.yaml", $"schema: connection/1\nname: warehouse\nengine: {engine}\n");
    }

    private const string PostgresCache = """
        schema: catalog-cache/1
        generated_at: 2026-11-12T10:00:00Z
        source: {server: prod-gateway, product: gateway}
        connections:
          warehouse:
            engine: postgres
            tables:
              - schema: src
                name: events
                kind: table
                columns:
                  - {name: id, type: integer, nullable: false, key: true}
                  - {name: label, type: 'character varying(50)', nullable: true}
                  - {name: amount, type: 'numeric(10,2)', nullable: true}
                  - {name: placed_at, type: timestamp without time zone, nullable: true}
                  - {name: note, type: text, nullable: true}
                  - {name: short, type: int4, nullable: false}
                  - {name: seen, type: timestamptz, nullable: true}
                  - {name: shape, type: geometry, nullable: true}
              - schema: src
                name: events_view
                kind: view
                columns:
                  - {name: id, type: integer, nullable: false}
        """;

    private (int Exit, JsonNode Doc, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "import", .. args, "--project", dir, "--format", "json"], o, e, environment: _ => null);   // no login anywhere
        return (exit, JsonNode.Parse(o.ToString())!, e.ToString());
    }

    private static string Said(JsonNode doc) => string.Join(" ", doc["errors"]!.AsArray().Select(e => (string)e!));

    private static JsonNode Model(JsonNode doc, string name) => doc["data"]!["mapped_models"]!.AsArray().Single(m => (string)m!["name"]! == name)!;

    private static string Logical(JsonNode model, string column) => (string?)model["columns"]!.AsArray().Single(c => (string)c!["name"]! == column)!["logical_type"] ?? "(none)";

    [Fact]
    public void Tables_are_described_from_the_cache_with_no_login_and_the_types_of_the_live_catalog_and_nothing_is_written_without_write()
    {
        Project();
        Write("catalog-cache.yaml", PostgresCache);
        var before = Snapshot(dir);

        var (exit, doc, err) = Run("warehouse", "src.*", "--from-cache", "catalog-cache.yaml");

        Assert.True(exit == 0, err + doc.ToJsonString());
        Assert.Equal("preview", (string)doc["data"]!["mode"]!);
        var events = Model(doc, "src.events");
        Assert.Equal("new", (string)events["status"]!);
        Assert.Equal("table", (string)events["object_kind"]!);
        Assert.Equal("view", (string)Model(doc, "src.events_view")["object_kind"]!);
        Assert.Equal(["id"], events["grain"]!.AsArray().Select(g => (string)g!).ToArray());
        foreach (var (column, logical) in new[] { ("id", "INTEGER"), ("label", "VARCHAR(50)"), ("amount", "DECIMAL(10, 2)"), ("placed_at", "TIMESTAMP"), ("note", "VARCHAR"), ("short", "INTEGER"), ("seen", "TIMESTAMP WITH TIME ZONE"), ("shape", "(none)") })
            Assert.Equal(logical, Logical(events, column));
        Assert.Equal("none", (string)events["columns"]!.AsArray().Single(c => (string)c!["name"]! == "shape")!["fit"]!);
        Assert.Contains(doc["data"]!["catalog_notes"]!.AsArray(), n => ((string)n!).Contains("no index and no foreign key was read"));
        Assert.Empty(events["indexes"]!.AsArray());
        Assert.Empty(events["foreign_keys"]!.AsArray());
        Assert.Equal(before, Snapshot(dir));      // a preview writes nothing
    }

    [Fact]
    public void Write_makes_the_descriptor_files_and_a_column_with_no_logical_type_is_left_out_and_said()
    {
        Project();
        Write("catalog-cache.yaml", PostgresCache);

        var (exit, doc, err) = Run("warehouse", "src.events", "--from-cache", "catalog-cache.yaml", "--write");

        Assert.True(exit == 0, err);
        Assert.Contains("models/src/events.yml", doc["data"]!["written"]!.AsArray().Select(w => (string)w!));
        var text = File.ReadAllText(Path.Combine(dir, "models", "src", "events.yml"));
        Assert.Contains("name: src.events", text);
        Assert.Contains("kind: mapped", text);
        Assert.Contains("grain: [id]", text);
        Assert.DoesNotContain("shape", text);      // geometry has no logical type
        Assert.Contains("src.events.shape", doc.ToJsonString());      // said in the document's findings, with the type that has no logical type
    }

    [Fact]
    public void A_committed_descriptor_keeps_its_indexes_and_foreign_keys_because_the_cache_knows_none_and_its_absence_is_not_a_removal()
    {
        Project();
        Write("catalog-cache.yaml", PostgresCache.Replace("{name: shape, type: geometry, nullable: true}", "{name: shape, type: text, nullable: true}"));
        Write("models/src/events.yml", "name: src.events\nkind: mapped\nconnections=: [warehouse]\ngrain: [id]\nindexes:\n  - {name: ix_events_label, columns: [label]}\ncolumns:\n  - {name: id, type: INTEGER, nullable: false}\n  - {name: label, type: 'VARCHAR(50)'}\n");

        var (exit, doc, err) = Run("warehouse", "src.events", "--from-cache", "catalog-cache.yaml");

        Assert.True(exit == 0, err);
        var events = Model(doc, "src.events");
        Assert.Equal("changed", (string)events["status"]!);      // the columns the cache has that the file lacks
        Assert.DoesNotContain(events["changes"]!.AsArray(), c => ((string)c!["kind"]!).StartsWith("index"));
        Assert.Equal("ix_events_label", (string)events["indexes"]![0]!["name"]!);
    }

    [Fact]
    public void A_table_the_file_already_matches_is_unchanged_and_nothing_is_rewritten()
    {
        Project();
        Write("catalog-cache.yaml", "schema: catalog-cache/1\ngenerated_at: 2026-11-12T10:00:00Z\nsource: {server: s, product: gateway}\nconnections:\n  warehouse:\n    engine: postgres\n    tables:\n      - schema: src\n        name: events\n        kind: table\n        columns:\n          - {name: id, type: bigint, nullable: false, key: true}\n          - {name: label, type: text, nullable: true}\n");
        Write("models/src/events.yml", "name: src.events\nkind: mapped\nconnections=: [warehouse]\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: label, type: VARCHAR}\n");

        var (exit, doc, _) = Run("warehouse", "src.events", "--from-cache", "catalog-cache.yaml", "--check");

        Assert.Equal(0, exit);
        Assert.Equal("unchanged", (string)Model(doc, "src.events")["status"]!);
    }

    [Fact]
    public void The_cache_is_refused_by_name_when_it_is_missing_unsound_for_another_connection_or_another_engine_or_an_engine_only_a_connection_can_map()
    {
        Project();
        var (exit, doc0, err) = Run("warehouse", "src.*", "--from-cache", "nope.yaml");
        Assert.Equal(2, exit);
        Assert.Contains("nope.yaml does not exist", err + Said(doc0));

        Write("catalog-cache.yaml", PostgresCache.Replace("        kind: table\n", "        kind: table\n        rows: []\n"));
        var (exitRow, docRow, errRow) = Run("warehouse", "src.*", "--from-cache", "catalog-cache.yaml");
        Assert.Equal(1, exitRow);
        Assert.Contains("DBD-CAT-002", errRow + Said(docRow));

        Write("catalog-cache.yaml", PostgresCache.Replace("  warehouse:", "  elsewhere:"));
        var (exitOther, docOther, errOther) = Run("warehouse", "src.*", "--from-cache", "catalog-cache.yaml");
        Assert.Equal(1, exitOther);
        Assert.Contains("no entry for the connection `warehouse`", errOther + Said(docOther));

        Write("catalog-cache.yaml", PostgresCache.Replace("engine: postgres", "engine: sqlserver"));
        var (exitEngine, docEngine, errEngine) = Run("warehouse", "src.*", "--from-cache", "catalog-cache.yaml");
        Assert.Equal(1, exitEngine);
        Assert.Contains("says `warehouse` is `sqlserver`", errEngine + Said(docEngine));

        Project("odbc");
        Write("catalog-cache.yaml", PostgresCache.Replace("engine: postgres", "engine: odbc"));
        var (exitOdbc, docOdbc, errOdbc) = Run("warehouse", "src.*", "--from-cache", "catalog-cache.yaml");
        Assert.Equal(1, exitOdbc);
        Assert.Contains("only a connection can load", errOdbc + Said(docOdbc));
    }

    [Theory]
    [InlineData("postgres", "int8", "bigint", "BIGINT")]
    [InlineData("postgres", "varchar", null, "VARCHAR")]
    [InlineData("postgres", "character varying(20)", null, "VARCHAR(20)")]
    [InlineData("postgres", "bpchar(3)", null, "VARCHAR(3)")]
    [InlineData("postgres", "timestamp(3) without time zone", null, "TIMESTAMP")]
    [InlineData("postgres", "decimal(12,4)", null, "DECIMAL(12, 4)")]
    [InlineData("postgres", "numeric", null, "(none)")]
    [InlineData("sqlserver", "nvarchar(max)", null, "VARCHAR")]
    [InlineData("sqlserver", "nvarchar(40)", null, "VARCHAR(40)")]
    [InlineData("sqlserver", "decimal(18,2)", null, "DECIMAL(18, 2)")]
    [InlineData("sqlserver", "datetime2(7)", null, "TIMESTAMP")]
    [InlineData("sqlserver", "uniqueidentifier", null, "UUID")]
    [InlineData("sqlserver", "geography", null, "(none)")]
    [InlineData("duckdb", "DECIMAL(18,3)", null, "DECIMAL(18, 3)")]
    [InlineData("duckdb", "HUGEINT", null, "(none)")]
    public async Task A_type_text_is_read_by_the_engines_own_spelling(string engine, string text, string? _, string logical)
    {
        var column = CachedImportCatalog.Shape(engine, new DbDataKit.Catalog.CacheColumn("c", text, true, false));
        var catalog = new CachedImportCatalog(new DbDataKit.Catalog.CacheConnection(engine, [new("s", "t", "table", [new("c", text, true, false)])]), engine);
        var shape = (await catalog.ObjectsAsync("s")).Values.Single();
        var imported = DbDataBuild.State.MappedModelImport.Describe(engine, shape, null);
        Assert.Equal(logical, imported.Columns.Single().LogicalType ?? "(none)");
        Assert.Equal(column, shape.Columns.Single());
    }
}
