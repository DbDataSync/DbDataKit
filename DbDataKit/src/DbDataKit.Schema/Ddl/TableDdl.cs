using DbDataKit.Dialect;

namespace DbDataKit.Schema.Ddl;

/// <summary>A column in the engine's own words: the type text to write in DDL, the collation to emit and the shape the engine's catalog will report for it.</summary>
/// <param name="Declaration">The type text, for example `nvarchar(20)` or `numeric(14, 2)`. Never contains user text beyond validated numbers.</param>
/// <param name="Collation">The engine collation to emit and expect, or null for non-text columns.</param>
/// <param name="Expected">What a catalog reports for the column once it exists, so a plan can say which shape hash the object must have afterwards.</param>
public sealed record NativeColumn(string Name, string Declaration, string? Collation, bool Nullable, ColumnShape Expected);

/// <summary>An index as the DDL needs it: a name, whether it is unique, its key columns in order and the columns it includes.</summary>
public sealed record IndexSpec(string Name, bool Unique, IReadOnlyList<string> Columns, IReadOnlyList<string> Include);

/// <summary>How a type change is classified for risk.</summary>
public enum TypeChange { None, Widening, Other }

/// <summary>
/// The statements that create and change a table on one engine, built from columns already written in the engine's own types (<see cref="NativeColumn"/>). Which declared type becomes which native type is the caller's
/// (it depends on the product's type names and settings); what each engine's statement looks like is here, once. Identifiers are quoted by the engine's shared dialect.
/// </summary>
public abstract class TableDdl(string engine)
{
    public string Engine => engine;

    private readonly ISqlDialect _dialect = DialectCatalog.Builtin.TryGet(engine, out var found) && found is not null ? found : throw new ArgumentException($"No shared dialect for '{engine}'.", nameof(engine));

    public string Quote(string identifier) => _dialect.Quote(identifier);

    protected abstract string CollateClause(string collation);

    public string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    /// <summary>Drops a table that may not exist (SQL Server 2016 and later, PostgreSQL, Fabric).</summary>
    public string DropTableIfExists(string schema, string name) => $"DROP TABLE IF EXISTS {Qualified(schema, name)};";

    /// <summary>Splits `marts.fct_orders` into schema and name.</summary>
    public static (string SchemaName, string Name) Split(string model)
    {
        var i = model.LastIndexOf('.');
        return i < 0 ? ("dbo", model) : (model[..i], model[(i + 1)..]);
    }

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

    public string CreateIndex(string schema, string name, IndexSpec index) =>
        $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {Quote(index.Name)} ON {Qualified(schema, name)} ({string.Join(", ", index.Columns.Select(Quote))})" +
        $"{(index.Include.Count > 0 ? $" INCLUDE ({string.Join(", ", index.Include.Select(Quote))})" : "")};";

    /// <summary>A primary key, added after the table exists (the same statement on SQL Server, Fabric and PostgreSQL). The columns must already be NOT NULL.</summary>
    public string AddPrimaryKey(string schema, string name, string constraint, IReadOnlyList<string> columns) =>
        $"ALTER TABLE {Qualified(schema, name)} ADD CONSTRAINT {Quote(constraint)} PRIMARY KEY ({string.Join(", ", columns.Select(Quote))});";

    /// <summary>A unique constraint over the columns (a key other rows may point at).</summary>
    public string AddUnique(string schema, string name, string constraint, IReadOnlyList<string> columns) =>
        $"ALTER TABLE {Qualified(schema, name)} ADD CONSTRAINT {Quote(constraint)} UNIQUE ({string.Join(", ", columns.Select(Quote))});";

    /// <summary>A foreign key from the columns of one table to the key (or unique columns) of another. No cascade: a row that is pointed at is not deleted by deleting something else.</summary>
    public string AddForeignKey(string schema, string name, string constraint, IReadOnlyList<string> columns, string referencedSchema, string referencedTable, IReadOnlyList<string> referencedColumns) =>
        $"ALTER TABLE {Qualified(schema, name)} ADD CONSTRAINT {Quote(constraint)} FOREIGN KEY ({string.Join(", ", columns.Select(Quote))}) REFERENCES {Qualified(referencedSchema, referencedTable)} ({string.Join(", ", referencedColumns.Select(Quote))});";

    /// <summary>A unique index over only the rows that satisfy a condition (a filtered index on SQL Server, a partial one on PostgreSQL): the same text for both.</summary>
    public string CreateUniqueIndexWhere(string schema, string name, string index, IReadOnlyList<string> columns, string condition) =>
        $"CREATE UNIQUE INDEX {Quote(index)} ON {Qualified(schema, name)} ({string.Join(", ", columns.Select(Quote))}) WHERE {condition};";

    public abstract string DropIndex(string schema, string table, string indexName);

    /// <param name="bodySql">The query written for this engine.</param>
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
}

/// <summary>SQL Server and Fabric: Transact-SQL.</summary>
public sealed class TSqlTableDdl(string engine) : TableDdl(engine)
{
    protected override string CollateClause(string collation) => "COLLATE " + collation;

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

/// <summary>PostgreSQL.</summary>
public sealed class PostgresTableDdl() : TableDdl("postgres")
{
    protected override string CollateClause(string collation) => "COLLATE " + Quote(collation);

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
