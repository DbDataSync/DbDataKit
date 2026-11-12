using System.Data;
using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>What an administrator's repair of a key map does (plan 25 K10): the four cases that happen in an emergency, a recovery or after a bug.</summary>
public enum KeyMapOperation
{
    /// <summary>A mapping that should exist and does not (a restore lost it): the given integer for the given business key text.</summary>
    Add,
    /// <summary>A mapping with the wrong business key text (a bug wrote it): the integer stays, the text is replaced.</summary>
    SetText,
    /// <summary>A mapping nothing should hold (a bug created it): the row is deleted. Refused while a fact table uses the integer, unless forced.</summary>
    Remove,
    /// <summary>The identity is at or above the largest integer of the map again, so no load can hand out an integer that is already there.</summary>
    Reseed,
}

internal static class KeyMapOperations
{
    public static string Name(this KeyMapOperation o) => o switch { KeyMapOperation.Add => "add", KeyMapOperation.SetText => "set-text", KeyMapOperation.Remove => "remove", _ => "reseed" };

    public static KeyMapOperation? Parse(string? name) => name switch
    {
        "add" => KeyMapOperation.Add,
        "set-text" => KeyMapOperation.SetText,
        "remove" => KeyMapOperation.Remove,
        "reseed" => KeyMapOperation.Reseed,
        _ => null,
    };

    public static readonly IReadOnlyList<string> Names = ["add", "set-text", "remove", "reseed"];
}

/// <summary>The tables of one key's map on an engine: the map itself (`marts.customer_key_map`) and the repair log beside it (`marts.customer_key_map_repair`).</summary>
internal sealed record KeyMapTables(string Engine, string Schema, string Key)
{
    public const string MapSuffix = "_map";
    public const string LogSuffix = "_map_repair";

    private string Quote(string identifier) => SharedQuoting.Quote(Engine, identifier);
    private string Qualified(string table) => Schema.Length == 0 ? Quote(table) : Quote(Schema) + "." + Quote(table);

    public string MapName => Schema.Length == 0 ? Key + MapSuffix : Schema + "." + Key + MapSuffix;
    public string LogName => Schema.Length == 0 ? Key + LogSuffix : Schema + "." + Key + LogSuffix;
    public string Map => Qualified(Key + MapSuffix);
    public string Log => Qualified(Key + LogSuffix);
    public string Col(string column) => Quote(column);
}

/// <summary>One repair as a script: one transaction, the guards, the change and its audit row, as the statements of a load are (the load scripts of K10 are the pattern).</summary>
internal sealed record KeyMapRepairScript(string Text, IReadOnlyList<GateParameter> Parameters)
{
    /// <summary>The business key text SQL Server can hold (a unique index holds 850 characters); the load of a longer one fails.</summary>
    public const int MaxTextSqlServer = 850;

    /// <summary>
    /// The script for an operation on a key's map. Every write is in one transaction together with the row of the repair log, so a repair is never half done and never unrecorded. Each change is preceded by a guard, a SELECT that divides by
    /// zero when the map is not as the repair assumed (the row to change is gone, the integer or the text to add is taken): the whole transaction then rolls back. A guard is a division rather than a `THROW` because a `DO` block of PostgreSQL
    /// cannot take the bound values; it reads the same on both engines. Whatever a concurrent load adds meanwhile does not matter: a load only adds rows, and the table lock held here (SQL Server `TABLOCKX`, PostgreSQL `SHARE ROW EXCLUSIVE`)
    /// keeps it from adding one between the guard and the change.
    /// </summary>
    public static KeyMapRepairScript Build(string engine, KeyMapTables tables, KeyMapOperation operation, long? value, string? text, bool forced, string by, Guid runId, string? reason)
    {
        var parameters = new List<GateParameter>
        {
            new("by", DbType.String, by), new("run", DbType.String, runId.ToString("D")), new("reason", DbType.String, reason), new("forced", DbType.Boolean, forced),
        };
        if (value != null) parameters.Add(new("value", DbType.Int64, value.Value));
        if (text != null) parameters.Add(new("text", DbType.String, text));
        var sql = engine switch { "sqlserver" => (Sql)new SqlServerSql(tables), "postgres" => new PostgresSql(tables), _ => throw new NotSupportedException($"A key map is kept on sqlserver and postgres, not on '{engine}'.") };
        return new KeyMapRepairScript(sql.Script(operation), parameters);
    }

    /// <summary>The statements the two engines differ on; the guards and the log row are the same text.</summary>
    private abstract class Sql(KeyMapTables t)
    {
        protected readonly KeyMapTables T = t;
        protected string Map => T.Map;
        protected string Log => T.Log;
        protected string Value => T.Col("key_value");
        protected string Text => T.Col("key_text");

        protected abstract string Open { get; }
        protected abstract string Close { get; }
        protected abstract string EnsureLog { get; }
        protected abstract string Lock { get; }
        protected abstract string Hint { get; }
        protected abstract IEnumerable<string> InsertWithValue { get; }
        protected abstract IEnumerable<string> Reseed();

        /// <summary>Divides by zero (and so rolls the transaction back) unless the count is what is asked: <paramref name="present"/> true needs one row, false needs none.</summary>
        protected static string Guard(string count, bool present) => present ? $"SELECT 1 / ({count});" : $"SELECT 1 / (1 - ({count}));";

        protected string Count(string where, bool hint = false) => $"SELECT COUNT(*) FROM {Map}{(hint ? Hint : "")} WHERE {where}";

        protected string LogRow(string operation, string key, string before, string after, string source = "") =>
            $"INSERT INTO {Log} ({T.Col("repaired_by")}, {T.Col("run_id")}, {T.Col("operation")}, {T.Col("key_value")}, {T.Col("text_before")}, {T.Col("text_after")}, {T.Col("identity_before")}, {T.Col("identity_after")}, {T.Col("forced")}, {T.Col("reason")}) " +
            $"{(source.Length == 0 ? "VALUES (" : "SELECT ")}@by, @run, '{operation}', {key}, {before}, {after}, {NullNumber}, {NullNumber}, @forced, @reason{(source.Length == 0 ? ");" : $" FROM {Map} WHERE {source};")}";

        protected abstract string NullText { get; }
        protected abstract string NullNumber { get; }

        public string Script(KeyMapOperation operation)
        {
            var s = new StringBuilder();
            foreach (var line in Statements(operation).Where(l => l.Length > 0)) s.AppendLineLf(line);
            return s.ToString();
        }

        private IEnumerable<string> Statements(KeyMapOperation operation)
        {
            yield return Open;
            yield return EnsureLog;
            switch (operation)
            {
                case KeyMapOperation.Add:
                    yield return Lock;
                    yield return Guard(Count($"{Value} = @value", hint: true), present: false);
                    yield return Guard(Count($"{Text} = @text"), present: false);
                    foreach (var line in InsertWithValue) yield return line;
                    yield return Guard(Count($"{Value} = @value AND {Text} = @text"), present: true);
                    yield return LogRow("add", "@value", NullText, "@text");
                    break;
                case KeyMapOperation.SetText:
                    yield return Lock;
                    yield return Guard(Count($"{Value} = @value", hint: true), present: true);
                    yield return Guard(Count($"{Text} = @text"), present: false);
                    yield return LogRow("set-text", Value, Text, "@text", source: $"{Value} = @value");
                    yield return $"UPDATE {Map} SET {Text} = @text WHERE {Value} = @value;";
                    yield return Guard(Count($"{Value} = @value AND {Text} = @text"), present: true);
                    break;
                case KeyMapOperation.Remove:
                    yield return Lock;
                    yield return Guard(Count($"{Value} = @value", hint: true), present: true);
                    yield return LogRow("remove", Value, Text, NullText, source: $"{Value} = @value");
                    yield return $"DELETE FROM {Map} WHERE {Value} = @value;";
                    break;
                default:
                    yield return Lock;
                    foreach (var line in Reseed()) yield return line;
                    break;
            }
            yield return Close;
        }
    }

    private sealed class SqlServerSql(KeyMapTables t) : Sql(t)
    {
        protected override string Open => "SET XACT_ABORT ON;\nBEGIN TRANSACTION;";
        protected override string Close => "COMMIT TRANSACTION;";
        protected override string NullText => "NULL";
        protected override string NullNumber => "NULL";
        protected override string Hint => " WITH (TABLOCKX)";
        // the lock is taken by the first statement that reads the map with the hint; a repair that reads it first takes it there
        protected override string Lock => "";

        protected override string EnsureLog =>
            $"IF OBJECT_ID(N'{Log.Replace("'", "''")}', N'U') IS NULL\nCREATE TABLE {Log} ({T.Col("repair_id")} BIGINT IDENTITY(1, 1) NOT NULL PRIMARY KEY, {T.Col("repaired_at")} DATETIMEOFFSET(3) NOT NULL DEFAULT SYSDATETIMEOFFSET(), " +
            $"{T.Col("repaired_by")} NVARCHAR(400) NOT NULL, {T.Col("db_login")} NVARCHAR(400) NOT NULL DEFAULT SUSER_SNAME(), {T.Col("run_id")} NVARCHAR(64) NOT NULL, {T.Col("operation")} NVARCHAR(20) NOT NULL, {T.Col("key_value")} BIGINT NULL, " +
            $"{T.Col("text_before")} NVARCHAR(850) COLLATE Latin1_General_100_BIN2 NULL, {T.Col("text_after")} NVARCHAR(850) COLLATE Latin1_General_100_BIN2 NULL, {T.Col("identity_before")} BIGINT NULL, {T.Col("identity_after")} BIGINT NULL, " +
            $"{T.Col("forced")} BIT NOT NULL, {T.Col("reason")} NVARCHAR(1000) NULL);";

        protected override IEnumerable<string> InsertWithValue =>
        [
            $"SET IDENTITY_INSERT {Map} ON;",
            $"INSERT INTO {Map} WITH (TABLOCKX) ({Value}, {Text}) VALUES (@value, @text);",
            $"SET IDENTITY_INSERT {Map} OFF;",
        ];

        // DBCC CHECKIDENT sets the last identity value handed out; only ever raised, so a value that was given is never given again (the identity of an empty map is left alone for the same reason)
        protected override IEnumerable<string> Reseed() =>
        [
            $"DECLARE @max BIGINT = (SELECT MAX({Value}) FROM {Map} WITH (TABLOCKX));",
            $"DECLARE @last BIGINT = CAST(IDENT_CURRENT(N'{Map.Replace("'", "''")}') AS BIGINT);",
            "IF @max IS NOT NULL AND @last < @max",
            "BEGIN",
            $"    DBCC CHECKIDENT (N'{Map.Replace("'", "''")}', RESEED, @max) WITH NO_INFOMSGS;",
            $"    INSERT INTO {Log} ({T.Col("repaired_by")}, {T.Col("run_id")}, {T.Col("operation")}, {T.Col("key_value")}, {T.Col("text_before")}, {T.Col("text_after")}, {T.Col("identity_before")}, {T.Col("identity_after")}, {T.Col("forced")}, {T.Col("reason")}) VALUES (@by, @run, 'reseed', NULL, NULL, NULL, @last, @max, @forced, @reason);",
            "END",
        ];
    }

    private sealed class PostgresSql(KeyMapTables t) : Sql(t)
    {
        protected override string Open => "BEGIN;";
        protected override string Close => "COMMIT;";
        protected override string NullText => "CAST(NULL AS text)";
        protected override string NullNumber => "CAST(NULL AS bigint)";
        protected override string Hint => "";
        protected override string Lock => $"LOCK TABLE {Map} IN SHARE ROW EXCLUSIVE MODE;";

        protected override string EnsureLog =>
            $"CREATE TABLE IF NOT EXISTS {Log} ({T.Col("repair_id")} bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, {T.Col("repaired_at")} timestamptz NOT NULL DEFAULT now(), {T.Col("repaired_by")} text NOT NULL, " +
            $"{T.Col("db_login")} text NOT NULL DEFAULT current_user, {T.Col("run_id")} text NOT NULL, {T.Col("operation")} text NOT NULL, {T.Col("key_value")} bigint, {T.Col("text_before")} text, {T.Col("text_after")} text, " +
            $"{T.Col("identity_before")} bigint, {T.Col("identity_after")} bigint, {T.Col("forced")} boolean NOT NULL, {T.Col("reason")} text);";

        // an identity column refuses a value of the caller's unless the statement says it overrides it; the sequence does not follow, so it is raised after
        protected override IEnumerable<string> InsertWithValue =>
        [
            $"INSERT INTO {Map} ({Value}, {Text}) OVERRIDING SYSTEM VALUE VALUES (@value, @text);",
            RaiseSequence(withLog: false),
        ];

        protected override IEnumerable<string> Reseed() => [RaiseSequence(withLog: true)];

        /// <summary>
        /// Raises the identity's sequence to the largest integer of the map when it is below it (never lowers it). The sequence is found by the column, so no name is guessed. `pg_sequence_last_value` is NULL for a sequence nothing has drawn from.
        /// With the log, the row is written first, while the sequence still has its old value.
        /// </summary>
        private string RaiseSequence(bool withLog)
        {
            var seq = $"(SELECT pg_get_serial_sequence('{Map.Replace("'", "''")}', 'key_value')::regclass AS seq) s CROSS JOIN (SELECT COALESCE(MAX({Value}), 0) AS max_value FROM {Map}) m";
            const string below = "m.max_value > COALESCE(pg_sequence_last_value(s.seq), 0)";
            if (!withLog) return $"SELECT setval(s.seq, m.max_value, true) FROM {seq} WHERE {below};";
            return $"INSERT INTO {Log} ({T.Col("repaired_by")}, {T.Col("run_id")}, {T.Col("operation")}, {T.Col("key_value")}, {T.Col("text_before")}, {T.Col("text_after")}, {T.Col("identity_before")}, {T.Col("identity_after")}, {T.Col("forced")}, {T.Col("reason")}) " +
                   $"SELECT @by, @run, 'reseed', CAST(NULL AS bigint), CAST(NULL AS text), CAST(NULL AS text), COALESCE(pg_sequence_last_value(s.seq), 0), m.max_value, @forced, @reason FROM {seq} WHERE {below};\n" +
                   $"SELECT setval(s.seq, m.max_value, true) FROM {seq} WHERE {below};";
        }
    }
}
