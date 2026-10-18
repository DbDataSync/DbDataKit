using DbDataBuild.Models;

namespace DbDataBuild.Targets.Loaders;

/// <summary>PostgreSQL statement assembly. MERGE needs PostgreSQL 15 or later (see matrix/strategies.yml).</summary>
public sealed class PostgresLoader : LoaderBase
{
    protected override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    protected override string Stage => "ddb_stage";
    protected override IReadOnlyList<string> Prelude => [];
    protected override IReadOnlyList<string> OpenTransaction => ["BEGIN;"];
    protected override IReadOnlyList<string> CloseTransaction => ["COMMIT;"];

    // Dropped explicitly rather than with ON COMMIT DROP: equivalent (DDL is transactional, so a failed run leaves nothing behind) and
    // the polyglot parser used for offline validation does not accept ON COMMIT DROP.
    protected override string? DropStage => "DROP TABLE ddb_stage;";

    protected override string StageStatement(string prefix, string selectList, string? where) =>
        $"CREATE TEMP TABLE ddb_stage AS\n{prefix.TrimEnd()}\nSELECT {selectList} FROM {BodyName}{where};";

    protected override string DeleteMatchingKeys(string table, IReadOnlyList<string> keys) =>
        $"DELETE FROM {table} AS tgt WHERE EXISTS (SELECT 1 FROM ddb_stage AS src WHERE {string.Join(" AND ", keys.Select(k => $"src.{Quote(k)} = tgt.{Quote(k)}"))});";

    protected override string Merge(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)
    {
        var update = columns.Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var matched = update.Count == 0 ? "" : $"\nWHEN MATCHED THEN UPDATE SET {string.Join(", ", update.Select(c => $"{Quote(c)} = src.{Quote(c)}"))}";
        return $"MERGE INTO {table} AS tgt USING ddb_stage AS src ON ({string.Join(" AND ", keys.Select(k => $"tgt.{Quote(k)} = src.{Quote(k)}"))}){matched}\n" +
               $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(c => "src." + Quote(c)))});";
    }

    protected override string SubtractDuration(string expression, LoadDuration d, string columnType)
    {
        var interval = $"{expression} - INTERVAL '{d.Amount} {d.Unit.ToString().ToLowerInvariant()}{(d.Amount == 1 ? "" : "s")}'";
        // date minus interval is a timestamp in PostgreSQL; cast back so the watermark keeps the column's type
        return columnType.Trim().Equals("DATE", StringComparison.OrdinalIgnoreCase) ? $"CAST({interval} AS DATE)" : interval;
    }

    protected override string TypedLiteral(string logicalType, string literal)
    {
        var lit = Literal(literal);
        var t = logicalType.Trim().ToUpperInvariant();
        if (t == "DATE") return $"CAST('{lit}' AS DATE)";
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return $"CAST('{lit.Replace('T', ' ')}' AS TIMESTAMP)";
        return lit;
    }
}
