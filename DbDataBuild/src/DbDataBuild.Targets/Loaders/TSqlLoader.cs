using DbDataBuild.Models;

namespace DbDataBuild.Targets.Loaders;

/// <summary>T-SQL statement assembly, shared by SQL Server and Fabric (Fabric entries are unverified; see matrix/strategies.yml).</summary>
public class TSqlLoader : LoaderBase
{
    protected override string Quote(string identifier) => SharedQuoting.Quote("sqlserver", identifier);
    protected override string Stage => "#ddb_stage";
    protected override IReadOnlyList<string> Prelude => ["SET XACT_ABORT ON;", "DROP TABLE IF EXISTS #ddb_stage;"];
    protected override IReadOnlyList<string> OpenTransaction => ["BEGIN TRANSACTION;"];
    protected override IReadOnlyList<string> CloseTransaction => ["COMMIT TRANSACTION;"];
    protected override string? DropStage => "DROP TABLE #ddb_stage;";

    protected override string StageStatement(string prefix, string selectList, string? where) =>
        $"{prefix.TrimEnd()}\nSELECT {selectList} INTO #ddb_stage FROM {BodyName}{where};";

    protected override string DeleteMatchingKeys(string table, IReadOnlyList<string> keys) =>
        $"DELETE FROM {table} WHERE EXISTS (SELECT 1 FROM #ddb_stage AS src WHERE {string.Join(" AND ", keys.Select(k => $"src.{Quote(k)} = {table}.{Quote(k)}"))});";

    protected override string Merge(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)
    {
        var update = columns.Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var matched = update.Count == 0 ? "" : $"\nWHEN MATCHED THEN UPDATE SET {string.Join(", ", update.Select(c => $"tgt.{Quote(c)} = src.{Quote(c)}"))}";
        return $"MERGE INTO {table} AS tgt USING #ddb_stage AS src ON ({string.Join(" AND ", keys.Select(k => $"tgt.{Quote(k)} = src.{Quote(k)}"))}){matched}\n" +
               $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(c => "src." + Quote(c)))});";
    }

    protected override string SubtractDuration(string expression, LoadDuration d, string columnType) =>
        $"DATEADD({d.Unit.ToString().ToUpperInvariant()}, -{d.Amount}, {expression})";

    // The text is compared as bytes (a binary collation), so two business keys that differ only in case or accent are two keys, as they are for a hash; the lookup of a query puts the same collation on its side.
    // IGNORE_DUP_KEY makes a second load adding the same text at the same moment end quietly with one row. 850 characters is what a unique index can hold.
    protected override string KeyMapEnsure(string table) =>
        $"IF OBJECT_ID(N'{table.Replace("'", "''")}', N'U') IS NULL\nCREATE TABLE {table} ({Quote("key_value")} BIGINT IDENTITY(1, 1) NOT NULL PRIMARY KEY, {Quote("key_text")} NVARCHAR(850) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE WITH (IGNORE_DUP_KEY = ON));";

    protected override string KeyMapInsert(string table, string column, string textPrefix)
    {
        var text = $"{BodyName}.{Quote(column)} COLLATE Latin1_General_100_BIN2";
        return $"{textPrefix.TrimEnd()}\nINSERT INTO {table} ({Quote("key_text")}) SELECT DISTINCT {text} FROM {BodyName} WHERE {BodyName}.{Quote(column)} IS NOT NULL " +
               $"AND NOT EXISTS (SELECT 1 FROM {table} AS m WHERE m.{Quote("key_text")} = {text});";
    }

    protected override string TypedLiteral(string logicalType, string literal)
    {
        var lit = Literal(literal);
        var t = logicalType.Trim().ToUpperInvariant();
        if (t == "DATE") return $"CAST('{lit}' AS DATE)";
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return $"CAST('{lit.Replace('T', ' ')}' AS DATETIME2(6))";
        return lit;   // integer types
    }
}
