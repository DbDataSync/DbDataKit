using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Execution;
using DbDataBuild.State;
using DbDataKit.Catalog;

namespace DbDataBuild.Cli;

/// <summary>
/// Where <c>project import</c> reads the shape of a database from: the live catalog through a read login (<see cref="LiveImportCatalog"/>) or, with <c>--from-cache</c>, the catalog cache of the project (<see cref="CachedImportCatalog"/>, plan 33 SC20k). Everything after it
/// (<see cref="MappedModelImport.Describe"/>, the merge with a committed descriptor, the status of each table, the diff) is the same code, so a descriptor made from the cache is the one the live catalog would have made, minus what the cache does not hold.
/// </summary>
internal interface IImportCatalog
{
    Task<IReadOnlyList<string>> SchemasAsync();

    Task<IReadOnlyDictionary<string, ObjectShape>> ObjectsAsync(string schema);

    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(string schema);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ForeignKeyShape>>> ForeignKeysAsync(string schema);

    /// <summary>The indexes of each table of the schema, or null when this catalog has no way to say (which the descriptor then says too).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<PhysicalItem>>?> IndexesAsync(string schema);

    /// <summary>Whether this catalog says anything about indexes and foreign keys. One that does not (the cache) must not make a descriptor lose the ones a person committed: they are kept as written.</summary>
    bool KnowsIndexesAndForeignKeys { get; }

    /// <summary>What the catalog could not say, once each.</summary>
    IReadOnlyList<string> Notes { get; }
}

internal sealed class LiveImportCatalog(ReadSession read, string engine) : IImportCatalog
{
    public Task<IReadOnlyList<string>> SchemasAsync() => SourceCatalogReader.SchemasAsync(read, engine);

    public Task<IReadOnlyDictionary<string, ObjectShape>> ObjectsAsync(string schema) => CatalogReader.ReadObjectsAsync(read, engine, schema);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(string schema) => SourceCatalogReader.PrimaryKeysAsync(read, engine, schema);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<ForeignKeyShape>>> ForeignKeysAsync(string schema) => SourceCatalogReader.ForeignKeysAsync(read, engine, schema);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<PhysicalItem>>?> IndexesAsync(string schema) => SourceCatalogReader.IndexesAsync(read, engine, schema);

    public bool KnowsIndexesAndForeignKeys => true;

    public IReadOnlyList<string> Notes => read.CatalogNotes;
}

/// <summary>
/// The catalog cache of a project (<c>catalog-cache.yaml</c>, format <c>catalog-cache/1</c>, <see cref="CatalogCache"/>) as a catalog: per schema, the tables and views of one connection with their columns (the type text the database gave, nullability, whether the column is part of the primary key).
/// <b>It has no indexes and no foreign keys</b>, so a descriptor made from it has none and says so (a note); the key's order is the order of the columns, which is what the cache holds; a type is read from its text (<see cref="Shape"/>), by the engine's own spelling.
/// </summary>
internal sealed class CachedImportCatalog(CacheConnection connection, string engine) : IImportCatalog
{
    public const string Note = "the catalog cache holds columns, types, nullability and the primary key only: no index and no foreign key was read, so a new descriptor has none and a committed one keeps the ones it has as written; import from the live connection to bring them in";

    public bool KnowsIndexesAndForeignKeys => false;

    public IReadOnlyList<string> Notes => [Note];

    public Task<IReadOnlyList<string>> SchemasAsync() => Task.FromResult<IReadOnlyList<string>>([.. connection.Tables.Select(t => t.Schema).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)]);

    public Task<IReadOnlyDictionary<string, ObjectShape>> ObjectsAsync(string schema)
    {
        var shapes = new Dictionary<string, ObjectShape>(StringComparer.Ordinal);
        foreach (var table in connection.Tables.Where(t => string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase)))
            shapes[$"{table.Schema}.{table.Name}"] = new ObjectShape(table.Schema, table.Name, string.Equals(table.Kind, "view", StringComparison.OrdinalIgnoreCase) ? ObjectKind.View : ObjectKind.Table,
                [.. table.Columns.Select(c => Shape(engine, c))], []);
        return Task.FromResult<IReadOnlyDictionary<string, ObjectShape>>(shapes);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(string schema) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(connection.Tables.Where(t => string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase) && t.Columns.Any(c => c.Key))
            .ToDictionary(t => t.Name, t => (IReadOnlyList<string>)[.. t.Columns.Where(c => c.Key).Select(c => c.Name)], StringComparer.Ordinal));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<ForeignKeyShape>>> ForeignKeysAsync(string schema) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ForeignKeyShape>>>(new Dictionary<string, IReadOnlyList<ForeignKeyShape>>());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<PhysicalItem>>?> IndexesAsync(string schema) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<PhysicalItem>>?>(null);

    /// <summary>The engines whose type spellings <see cref="ImportedTypes"/> reads from a type name: the ones the tool builds on and DuckDB. An `odbc` or `flightsql` source is mapped through its dialect's `types:`, which only a connection can load.</summary>
    public static bool Supports(string engine) => engine is "sqlserver" or "postgres" or "duckdb";

    /// <summary>The spellings PostgreSQL's own tools use for the names `information_schema` reports in full (`int4` is `integer`): only the ones that mean exactly that, so a type written in either form is read the same.</summary>
    private static readonly Dictionary<string, string> PostgresNames = new(StringComparer.Ordinal)
    {
        ["int"] = "integer", ["int4"] = "integer", ["int8"] = "bigint", ["int2"] = "smallint", ["bool"] = "boolean", ["float8"] = "double precision", ["float4"] = "real",
        ["varchar"] = "character varying", ["char"] = "character", ["bpchar"] = "character", ["decimal"] = "numeric", ["timestamp"] = "timestamp without time zone",
        ["timestamptz"] = "timestamp with time zone", ["time"] = "time without time zone",
    };

    private static int? Number(string? text) => text is null ? null : text.Equals("max", StringComparison.OrdinalIgnoreCase) ? -1 : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>One cached column as the catalog shape the type mapping reads: the type name in lower case without its arguments, and the length, precision or scale the arguments mean for that type. A text type with no length is unbounded, as the live catalog reports <c>varchar(max)</c> and <c>text</c>.</summary>
    public static ColumnShape Shape(string engine, CacheColumn column)
    {
        // `numeric(10,2)`, `character varying(50)`, `timestamp(6) without time zone`: the name, the arguments in the parentheses and what follows them
        var text = column.Type.Trim();
        var open = text.IndexOf('(');
        var close = open >= 0 ? text.IndexOf(')', open) : -1;
        var (head, argsText, tail) = open >= 0 && close > open ? (text[..open], text[(open + 1)..close], text[(close + 1)..]) : (text, "", "");
        var name = Regex.Replace($"{head} {tail}".Trim().ToLowerInvariant(), @"\s+", " ");
        if (engine == "postgres" && PostgresNames.TryGetValue(name, out var full)) name = full;
        var args = argsText.Length > 0 ? argsText.Split(',', StringSplitOptions.TrimEntries) : [];
        int? length = null, precision = null, scale = null;
        switch (name)
        {
            case "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary" or "character varying" or "character":
                length = args.Length > 0 ? Number(args[0]) : engine == "postgres" && name == "character" ? 1 : null;
                break;
            case "decimal" or "numeric":
                (precision, scale) = (args.Length > 0 ? Number(args[0]) : null, args.Length > 1 ? Number(args[1]) : args.Length > 0 ? 0 : null);
                break;
            case "datetime2" or "time" or "datetimeoffset":
                scale = args.Length > 0 ? Number(args[0]) : null;
                break;
        }
        return new ColumnShape(column.Name, name, length, precision, scale, column.Nullable, null);
    }
}
