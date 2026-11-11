using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Metadata;

namespace DbDataKit.Schema.Reading;

/// <summary>The engines whose catalogs are read into the shapes that are hashed. Each reports types in its own words, so the arguments of a type are read by what the type uses them for.</summary>
public enum ShapeEngine { SqlServer, Postgres }

/// <summary>
/// The live structure of a schema, read through the shared catalog readers of <c>DbDataKit.Metadata</c> and turned into the shapes the tool hashes (<see cref="ObjectShape"/>): lower-case types without their arguments, the columns' length,
/// precision and scale apart, and one physical item per index (with its included columns) and, on SQL Server, one for each index's compression and partition count (plan 19, H04).
/// </summary>
public static class CatalogShapes
{
    /// <summary>
    /// The tables and views of a schema (or just one object of it), keyed by their qualified name. The reader decides how the queries run: a product that guards its reads gives a reader that sends every query through its guard. Build the reader
    /// with <c>IncludeConstraintIndexes</c> on, or an index a PRIMARY KEY or UNIQUE constraint made is not seen.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadAsync(ICatalogReader reader, ShapeEngine engine, string schema, string? objectName, CancellationToken cancellationToken = default)
    {
        var shapes = await reader.ReadSchemaAsync(null, schema, objectName, cancellationToken).ConfigureAwait(false);
        return shapes.ToDictionary(s => $"{schema}.{s.Table.Name}", s => ToObjectShape(s, schema, engine));
    }

    public static ObjectShape ToObjectShape(TableShape shape, string schema, ShapeEngine engine)
    {
        var postgres = engine == ShapeEngine.Postgres;
        return new ObjectShape(schema, shape.Table.Name, shape.Table.Kind == TableKind.View ? ObjectKind.View : ObjectKind.Table,
            [.. shape.Columns.OrderBy(c => c.Ordinal).Select(c => ColumnOf(c, postgres))], PhysicalOf(shape, postgres));
    }

    private static IReadOnlyList<PhysicalItem> PhysicalOf(TableShape shape, bool postgres)
    {
        var items = new List<PhysicalItem>();
        foreach (var i in shape.Indexes)
        {
            var keys = i.Columns.Select(c => c.Descending ? c.Name + " desc" : c.Name);
            items.Add(new(i.BacksConstraint ? "constraint_index" : "index", i.Name, IndexText.Canonical(i.Unique, keys, i.IncludeList)));
            if (!postgres && i.Compression is not null) items.Add(new("compression", i.Name, $"{i.Compression} partitions={i.Partitions}"));
        }
        return items;
    }

    // "nvarchar(50)", "numeric(18,3)", "timestamp(3) without time zone": the type without its arguments, and the arguments by what the type uses them for
    private static readonly Regex ArgumentList = new(@"\(([^)]*)\)", RegexOptions.Compiled);

    private static ColumnShape ColumnOf(ColumnInfo c, bool postgres)
    {
        var native = c.NativeType.Trim();
        var arguments = ArgumentList.Match(native) is { Success: true } m
            ? m.Groups[1].Value.Split(',').Select(a => a.Trim()).Select(a => a.Equals("max", StringComparison.OrdinalIgnoreCase) ? -1 : int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (int?)n : null).ToList()
            : [];
        var type = Regex.Replace(ArgumentList.Replace(native, ""), @"\s+", " ").Trim().ToLowerInvariant();
        if (postgres && type.EndsWith("[]", StringComparison.Ordinal)) type = "array";      // information_schema says ARRAY for every array type, and the tool has always recorded that
        int? first = arguments.Count > 0 ? arguments[0] : null, second = arguments.Count > 1 ? arguments[1] : null;
        var numeric = type is "decimal" or "numeric";
        var temporal = postgres ? type.StartsWith("timestamp", StringComparison.Ordinal) || type.StartsWith("time", StringComparison.Ordinal) : type is "datetime2" or "time" or "datetimeoffset";
        var sized = postgres ? type is "character varying" or "character" or "bit" or "bit varying" : type is "char" or "varchar" or "binary" or "varbinary" or "nchar" or "nvarchar";
        return new(c.Name, type, sized ? first : null, numeric ? first : null, numeric ? (first is null ? null : second ?? 0) : temporal ? first ?? (postgres ? 6 : null) : null,   // a PostgreSQL time or timestamp without an argument has microseconds, which information_schema reports as 6
             c.Nullable, c.Collation, c.IsComputed ? c.DefaultExpr : null);
    }
}
