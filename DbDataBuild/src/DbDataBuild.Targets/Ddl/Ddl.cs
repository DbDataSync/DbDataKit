using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataKit.Schema.Ddl;

namespace DbDataBuild.Targets.Ddl;

/// <summary>Thrown when a declared type or collation has no faithful native form. Carries the diagnostic the planner reports.</summary>
public sealed class DdlUnsupportedException(Diagnostic diagnostic) : Exception(diagnostic.Found)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>
/// Per-engine DDL for a model (DESIGN.md 8: `IDdlGenerator`): the logical-to-native type table and the project's collations, which are Build's, and the statements, which are the shared <see cref="TableDdl"/>
/// of the engine (plan 19, H03).
/// </summary>
public abstract partial class DdlGenerator(string target, ProjectConfig config, TableDdl statements)
{
    public string Target => target;

    protected abstract (string Declaration, string CatalogType, int? Length, int? Precision, int? Scale)? MapType(string normalized);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SafeCollation();

    public string Qualified(string schema, string name) => statements.Qualified(schema, name);

    /// <summary>An identifier quoted for this engine (a column name in an INSERT list).</summary>
    public string QuoteIdentifier(string identifier) => statements.Quote(identifier);

    /// <summary>Drops a table that may not exist (SQL Server 2016 and later, PostgreSQL, Fabric).</summary>
    public string DropTableIfExists(string schema, string name) => statements.DropTableIfExists(schema, name);

    /// <summary>Splits `marts.fct_orders` into schema and name.</summary>
    public static (string SchemaName, string Name) Split(string model) => TableDdl.Split(model);

    public NativeColumn Map(string model, ColumnDefinition column)
    {
        var m = MapType(LogicalTypes.Canonical(column.Type)) ?? throw new DdlUnsupportedException(new Diagnostic(DiagnosticCatalog.TypeNotMappable, new(model, column.Line, 0),
            $"Column `{column.Name}` of {model} is declared {column.Type}, which has no native {target} type."));
        string? collation = null;
        if (m.CatalogType is "nvarchar" or "varchar" or "character varying" or "char" or "text")
        {
            var logical = column.Collation ?? "default";
            collation = config.StringSemantics.Collations.TryGetValue(logical, out var byEngine) && byEngine.TryGetValue(target, out var name) ? name
                : throw new DdlUnsupportedException(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, new(model, column.Line, 0),
                    $"Column `{column.Name}` of {model} needs the collation `{logical}` for {target}, which is not configured."));
            if (!SafeCollation().IsMatch(collation))
                throw new DdlUnsupportedException(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, new(model, column.Line, 0), $"The collation name `{collation}` for {target} contains characters that are not allowed in a collation name."));
        }
        var shape = new ColumnShape(column.Name, m.CatalogType, m.Length, m.Precision, m.Scale, column.Nullable, collation);
        return new NativeColumn(column.Name, m.Declaration, collation, column.Nullable, shape);
    }

    public IReadOnlyList<NativeColumn> MapAll(ModelDefinition model) => model.Columns.Select(c => Map(model.Name, c)).ToList();

    public static IReadOnlyList<ColumnShape> ExpectedShape(IEnumerable<NativeColumn> columns) => TableDdl.ExpectedShape(columns);

    public string CreateSchema(string schema) => statements.CreateSchema(schema);
    public string CreateTable(string schema, string name, IReadOnlyList<NativeColumn> columns) => statements.CreateTable(schema, name, columns);
    public string AddColumn(string schema, string name, NativeColumn column) => statements.AddColumn(schema, name, column);
    public string DropColumn(string schema, string name, string column) => statements.DropColumn(schema, name, column);
    public string RenameColumn(string schema, string name, string from, string to) => statements.RenameColumn(schema, name, from, to);
    public string AlterColumn(string schema, string name, NativeColumn column) => statements.AlterColumn(schema, name, column);
    public string DropTable(string schema, string name) => statements.DropTable(schema, name);
    public string CreateIndex(string schema, string name, IndexDefinition index) => statements.CreateIndex(schema, name, new IndexSpec(index.Name, index.Unique, index.Columns, index.Include));
    public string DropIndex(string schema, string table, string indexName) => statements.DropIndex(schema, table, indexName);

    /// <param name="bodySql">The model body transpiled to this engine.</param>
    public string CreateOrReplaceView(string schema, string name, IReadOnlyList<NativeColumn> columns, string bodySql) => statements.CreateOrReplaceView(schema, name, columns, bodySql);

    /// <summary>Safe when the new type holds every value of the old one: same family, larger length or precision at the same scale.</summary>
    public static DbDataKit.Schema.Ddl.TypeChange Classify(ColumnShape from, ColumnShape to) => TableDdl.Classify(from, to);

    protected static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class TSqlDdl(string target, ProjectConfig config) : DdlGenerator(target, config, new TSqlTableDdl(target))
{
    private readonly bool fabric = target == "fabric";

    [GeneratedRegex(@"^VARCHAR\((\d+)\)$")] private static partial Regex Varchar();
    [GeneratedRegex(@"^DECIMAL\((\d+), (\d+)\)$")] private static partial Regex Decimal();

    protected override (string, string, int?, int?, int?)? MapType(string t)
    {
        if (Varchar().Match(t) is { Success: true } v)
        {
            var n = int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture);
            // nvarchar holds 4000 characters before MAX; Fabric has no nvarchar and holds 8000 before MAX (Microsoft's data types page)
            var (name, limit) = fabric ? ("varchar", 8000) : ("nvarchar", 4000);
            return n is < 1 ? null : n > limit ? ($"{name}(max)", name, -1, null, null) : ($"{name}({n})", name, n, null, null);
        }
        if (Decimal().Match(t) is { Success: true } d)
        {
            var (p, s) = (int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture));
            return p is < 1 or > 38 || s > p ? null : ($"decimal({p}, {s})", "decimal", null, p, s);
        }
        return t switch
        {
            "BIGINT" => ("bigint", "bigint", null, null, null),
            "INTEGER" => ("int", "int", null, null, null),
            "SMALLINT" or "TINYINT" => ("smallint", "smallint", null, null, null), // T-SQL tinyint is unsigned, DuckDB's is signed
            "DOUBLE" => ("float(53)", "float", null, null, null),
            "FLOAT" => ("real", "real", null, null, null),
            "BOOLEAN" => ("bit", "bit", null, null, null),
            "DATE" => ("date", "date", null, null, null),
            "TIMESTAMP" => ("datetime2(6)", "datetime2", null, null, 6),
            "TIME" => ("time(6)", "time", null, null, 6),
            "TIMESTAMP WITH TIME ZONE" => ("datetimeoffset(6)", "datetimeoffset", null, null, 6),
            "UUID" => ("uniqueidentifier", "uniqueidentifier", null, null, null),
            "BLOB" => ("varbinary(max)", "varbinary", -1, null, null),
            "VARCHAR" => fabric ? ("varchar(max)", "varchar", -1, null, null) : ("nvarchar(max)", "nvarchar", -1, null, null),   // unlimited text
            "DECIMAL" => ("decimal(18, 3)", "decimal", null, 18, 3), // DuckDB's bare DECIMAL
            _ => null,
        };
    }
}

public sealed partial class PostgresDdl(ProjectConfig config) : DdlGenerator("postgres", config, new PostgresTableDdl())
{
    [GeneratedRegex(@"^VARCHAR\((\d+)\)$")] private static partial Regex Varchar();
    [GeneratedRegex(@"^DECIMAL\((\d+), (\d+)\)$")] private static partial Regex Decimal();

    protected override (string, string, int?, int?, int?)? MapType(string t)
    {
        if (Varchar().Match(t) is { Success: true } v)
        {
            var n = int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture);
            return n is < 1 or > 10485760 ? null : ($"varchar({n})", "character varying", n, null, null);
        }
        if (Decimal().Match(t) is { Success: true } d)
        {
            var (p, s) = (int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture));
            return p is < 1 or > 38 || s > p ? null : ($"numeric({p}, {s})", "numeric", null, p, s);
        }
        return t switch
        {
            "BIGINT" => ("bigint", "bigint", null, null, null),
            "INTEGER" => ("integer", "integer", null, null, null),
            "SMALLINT" or "TINYINT" => ("smallint", "smallint", null, null, null),
            "DOUBLE" => ("double precision", "double precision", null, null, null),
            "FLOAT" => ("real", "real", null, null, null),
            "BOOLEAN" => ("boolean", "boolean", null, null, null),
            "DATE" => ("date", "date", null, null, null),
            "TIMESTAMP" => ("timestamp(6)", "timestamp without time zone", null, null, 6),
            "TIME" => ("time(6)", "time without time zone", null, null, 6),
            "TIMESTAMP WITH TIME ZONE" => ("timestamp(6) with time zone", "timestamp with time zone", null, null, 6),
            "UUID" => ("uuid", "uuid", null, null, null),
            "BLOB" => ("bytea", "bytea", null, null, null),
            "VARCHAR" => ("text", "text", null, null, null),   // unlimited text
            "DECIMAL" => ("numeric(18, 3)", "numeric", null, 18, 3),
            _ => null,
        };
    }
}
