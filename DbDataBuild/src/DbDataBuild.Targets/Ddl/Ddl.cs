using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Targets.Ddl;

/// <summary>A declared column mapped to one engine: the text to write in DDL, and the shape the engine's catalog will report for it.</summary>
/// <param name="Declaration">The type text, for example `nvarchar(20)` or `numeric(14, 2)`. Never contains user text beyond validated numbers.</param>
/// <param name="Collation">The engine collation to emit and expect, or null for non-text columns.</param>
public sealed record NativeColumn(string Name, string Declaration, string? Collation, bool Nullable, ColumnShape Expected);

/// <summary>Thrown when a declared type or collation has no faithful native form. Carries the diagnostic the planner reports.</summary>
public sealed class DdlUnsupportedException(Diagnostic diagnostic) : Exception(diagnostic.Found)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>How a type change is classified for risk (DESIGN.md 10.4).</summary>
public enum TypeChange { None, Widening, Other }

/// <summary>Per-engine DDL statements and the logical-to-native type table (DESIGN.md 8: `IDdlGenerator`).</summary>
public abstract partial class DdlGenerator(string target, ProjectConfig config)
{
    public string Target => target;

    protected abstract string Quote(string identifier);
    protected abstract (string Declaration, string CatalogType, int? Length, int? Precision, int? Scale)? MapType(string normalized);
    protected abstract string CollateClause(string collation);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex SafeCollation();

    public string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    /// <summary>An identifier quoted for this engine (a column name in an INSERT list).</summary>
    public string QuoteIdentifier(string identifier) => Quote(identifier);

    /// <summary>Drops a table that may not exist (SQL Server 2016 and later, PostgreSQL, Fabric).</summary>
    public string DropTableIfExists(string schema, string name) => $"DROP TABLE IF EXISTS {Qualified(schema, name)};";

    /// <summary>Splits `marts.fct_orders` into schema and name.</summary>
    public static (string SchemaName, string Name) Split(string model)
    {
        var i = model.LastIndexOf('.');
        return i < 0 ? ("dbo", model) : (model[..i], model[(i + 1)..]);
    }

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

    public static IReadOnlyList<ColumnShape> ExpectedShape(IEnumerable<NativeColumn> columns) => columns.Select(c => c.Expected).ToList();

    protected string ColumnText(NativeColumn c) =>
        $"{Quote(c.Name)} {c.Declaration}{(c.Collation == null ? "" : " " + CollateClause(c.Collation))} {(c.Nullable ? "NULL" : "NOT NULL")}";

    public abstract string CreateSchema(string schema);

    public string CreateTable(string schema, string name, IReadOnlyList<NativeColumn> columns) =>
        $"CREATE TABLE {Qualified(schema, name)} (\n{string.Join(",\n", columns.Select(c => "  " + ColumnText(c)))}\n);";

    public abstract string AddColumn(string schema, string name, NativeColumn column);
    public string DropColumn(string schema, string name, string column) => $"ALTER TABLE {Qualified(schema, name)} DROP COLUMN {Quote(column)};";
    public abstract string RenameColumn(string schema, string name, string from, string to);
    public abstract string AlterColumn(string schema, string name, NativeColumn column);
    public abstract string DropTable(string schema, string name);

    public string CreateIndex(string schema, string name, IndexDefinition index) =>
        $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {Quote(index.Name)} ON {Qualified(schema, name)} ({string.Join(", ", index.Columns.Select(Quote))})" +
        $"{(index.Include.Count > 0 ? $" INCLUDE ({string.Join(", ", index.Include.Select(Quote))})" : "")};";

    public abstract string DropIndex(string schema, string table, string indexName);

    /// <param name="bodySql">The model body transpiled to this engine.</param>
    public abstract string CreateOrReplaceView(string schema, string name, IReadOnlyList<NativeColumn> columns, string bodySql);

    /// <summary>Safe when the new type holds every value of the old one: same family, larger length or precision at the same scale.</summary>
    public static TypeChange Classify(ColumnShape from, ColumnShape to)
    {
        if (from.Type == to.Type && from.Length == to.Length && from.Precision == to.Precision && from.Scale == to.Scale) return TypeChange.None;
        static bool Wider(int? a, int? b) => a == -1 ? b == -1 : b == -1 || (a != null && b != null && b > a);
        if (from.Type == to.Type)
        {
            if (from.Length != to.Length && from.Precision == to.Precision && from.Scale == to.Scale && Wider(from.Length, to.Length)) return TypeChange.Widening;
            if (from.Length == to.Length && from.Scale == to.Scale && from.Precision != null && to.Precision > from.Precision) return TypeChange.Widening;
        }
        if (from.Type == "character varying" && to.Type == "text" && from.Collation == to.Collation) return TypeChange.Widening;   // varchar(n) to unlimited text holds every value
        var rank = new Dictionary<string, int> { ["smallint"] = 1, ["int"] = 2, ["integer"] = 2, ["bigint"] = 3 };
        if (rank.TryGetValue(from.Type, out var fr) && rank.TryGetValue(to.Type, out var tr) && tr > fr) return TypeChange.Widening;
        return TypeChange.Other;
    }

    protected static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class TSqlDdl(string target, ProjectConfig config) : DdlGenerator(target, config)
{
    private readonly bool fabric = target == "fabric";
    protected override string Quote(string id) => "[" + id.Replace("]", "]]") + "]";
    protected override string CollateClause(string collation) => "COLLATE " + collation;

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

    public override string CreateSchema(string schema) => $"IF SCHEMA_ID({Lit(schema)}) IS NULL EXEC({Lit("CREATE SCHEMA " + Quote(schema))});";
    private static string Lit(string s) => "N'" + s.Replace("'", "''") + "'";

    public override string AddColumn(string schema, string name, NativeColumn c) => $"ALTER TABLE {Qualified(schema, name)} ADD {ColumnText(c)};";
    public override string RenameColumn(string schema, string name, string from, string to) =>
        $"EXEC sp_rename {Lit($"{Quote(schema)}.{Quote(name)}.{Quote(from)}")}, {Lit(to)}, N'COLUMN';";
    public override string AlterColumn(string schema, string name, NativeColumn c) => $"ALTER TABLE {Qualified(schema, name)} ALTER COLUMN {ColumnText(c)};";
    public override string DropTable(string schema, string name) => $"DROP TABLE {Qualified(schema, name)};";
    public override string DropIndex(string schema, string table, string indexName) => $"DROP INDEX {Quote(indexName)} ON {Qualified(schema, table)};";

    public override string CreateOrReplaceView(string schema, string name, IReadOnlyList<NativeColumn> columns, string bodySql) =>
        $"CREATE OR ALTER VIEW {Qualified(schema, name)} ({string.Join(", ", columns.Select(c => Quote(c.Name)))}) AS\n{bodySql.Trim().TrimEnd(';')};";
}

public sealed partial class PostgresDdl(ProjectConfig config) : DdlGenerator("postgres", config)
{
    protected override string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
    protected override string CollateClause(string collation) => "COLLATE " + Quote(collation);

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

    public override string CreateSchema(string schema) => $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)};";
    public override string AddColumn(string schema, string name, NativeColumn c) => $"ALTER TABLE {Qualified(schema, name)} ADD COLUMN {ColumnText(c)};";
    public override string RenameColumn(string schema, string name, string from, string to) =>
        $"ALTER TABLE {Qualified(schema, name)} RENAME COLUMN {Quote(from)} TO {Quote(to)};";

    public override string AlterColumn(string schema, string name, NativeColumn c)
    {
        var q = Quote(c.Name);
        var type = $"{c.Declaration}{(c.Collation == null ? "" : " " + CollateClause(c.Collation))}";
        return $"ALTER TABLE {Qualified(schema, name)} ALTER COLUMN {q} TYPE {type}, ALTER COLUMN {q} {(c.Nullable ? "DROP" : "SET")} NOT NULL;";
    }

    public override string DropTable(string schema, string name) => $"DROP TABLE {Qualified(schema, name)};";
    public override string DropIndex(string schema, string table, string indexName) => $"DROP INDEX {Qualified(schema, indexName)};"; // PostgreSQL index names are schema-scoped

    // PostgreSQL's CREATE OR REPLACE VIEW cannot change or reorder existing columns, so a changed view is dropped and created in one statement batch (one implicit transaction)
    public override string CreateOrReplaceView(string schema, string name, IReadOnlyList<NativeColumn> columns, string bodySql) =>
        $"DROP VIEW IF EXISTS {Qualified(schema, name)};\nCREATE VIEW {Qualified(schema, name)} ({string.Join(", ", columns.Select(c => Quote(c.Name)))}) AS\n{bodySql.Trim().TrimEnd(';')};";
}
