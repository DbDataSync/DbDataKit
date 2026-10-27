using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.State;
using DbDataKit.Metadata;

namespace DbDataBuild.Execution;

/// <summary>
/// The live shape of the objects under a schema name on SQL Server or PostgreSQL, read by the shared catalog readers of <c>DbDataKit.Metadata</c> (plan 13, B04) and run through the read session: the readers write the queries, the session
/// guards, observes and runs every one of them, so nothing about the gate changes. What comes back is turned into the shapes the tool hashes (<see cref="ObjectShape"/>): lower-case types without their arguments, the columns' length,
/// precision and scale apart, and one physical item per index (with its included columns) and, on SQL Server, one for each index's compression and partition count.
/// </summary>
internal static class SharedCatalog
{
    public static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadObjectsAsync(ReadSession read, bool postgres, string schema, string? objectName, CancellationToken ct)
    {
        var options = new CatalogReaderOptions { IncludeConstraintIndexes = true };
        ICatalogReader reader = postgres ? new GatedPostgresReader(read, options) : new GatedSqlServerReader(read, options);
        var shapes = await reader.ReadSchemaAsync(null, schema, objectName, ct);
        return shapes.ToDictionary(
            s => $"{schema}.{s.Table.Name}",
            s => new ObjectShape(schema, s.Table.Name, s.Table.Kind == TableKind.View ? ObjectKind.View : ObjectKind.Table,
                [.. s.Columns.OrderBy(c => c.Ordinal).Select(c => ColumnOf(c, postgres))], PhysicalOf(s, postgres)));
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
        int? first = arguments.Count > 0 ? arguments[0] : null, second = arguments.Count > 1 ? arguments[1] : null;
        var numeric = type is "decimal" or "numeric";
        var temporal = postgres ? type.StartsWith("timestamp", StringComparison.Ordinal) || type.StartsWith("time", StringComparison.Ordinal) : type is "datetime2" or "time" or "datetimeoffset";
        var sized = postgres ? type is "character varying" or "character" : type is "char" or "varchar" or "binary" or "varbinary" or "nchar" or "nvarchar";
        return new(c.Name, type, sized ? first : null, numeric ? first : null, numeric ? second ?? 0 : temporal ? first : null, c.Nullable, c.Collation, c.IsComputed ? c.DefaultExpr : null);
    }

    /// <summary>Turns the readers' <c>{0}</c> markers into this session's named parameters and runs the query through it.</summary>
    private static async Task<IReadOnlyList<CatalogRow>> RunAsync(ReadSession read, string sql, object?[] args, CancellationToken ct)
    {
        var parameters = args.Select((a, i) => new GateParameter($"p{i}", a is int ? DbType.Int32 : a is bool ? DbType.Boolean : DbType.String, a)).ToList();
        for (var i = 0; i < args.Length; i++) sql = sql.Replace("{" + i + "}", "@p" + i, StringComparison.Ordinal);
        var rows = await read.QueryAsync(sql, parameters, ct);
        return [.. rows.Select(r => new CatalogRow([.. r.Select(_ => "")], [.. r]))];
    }

    private sealed class GatedSqlServerReader(ReadSession read, CatalogReaderOptions options) : SqlServerCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken);
    }

    private sealed class GatedPostgresReader(ReadSession read, CatalogReaderOptions options) : PostgresCatalogReader(new UnusedConnection(), options: options)
    {
        protected override Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default) => RunAsync(read, sql, args, cancellationToken);
    }
}
