using System.Globalization;
using DbDataBuild.Models;
using DbDataBuild.Targets.DuckDb;
using DuckDB.NET.Data;

namespace DbDataBuild.Sample;

/// <summary>A table the model's query reads, with the columns the project declares for it.</summary>
public sealed record UpstreamTable(string Name, IReadOnlyList<ColumnDefinition> Columns);

public enum CaseOutcome { Pass, Fail, Error, NotASelect }

/// <summary>The outcome of one case. For a failure, <c>Rows</c> are the differing rows (with a `_diff` column) or the rows an `assert` returned, and <c>Count</c> how many there were.</summary>
public sealed record CaseResult(CaseOutcome Outcome, string? Message, int Line, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, int Count);

/// <summary>
/// Runs one case of a model test (DESIGN.md 9.8) in an in-memory DuckDB with external access off: the tables the query reads are created from their declared columns and filled with the
/// `given` rows (each value cast by DuckDB to its column's declared type), the model's query runs as written into a table named `result`, and `expect` and `assert` are checked.
/// `expect` is cast to the declared output types and compared both ways with EXCEPT ALL, so duplicates count and row order does not matter unless the case says `ordered`. Nothing is
/// connected to and nothing is written.
/// </summary>
public static class ModelTestRunner
{
    public static CaseResult Run(string modelName, IReadOnlyList<ColumnDefinition> declared, string sql, IReadOnlyList<UpstreamTable> upstream, ModelTestCase test, int keep, DbDataBuild.Core.DuckPrelude? macros = null, string? collation = null)
    {
        CaseResult Error(string message, int line = 0) => new(CaseOutcome.Error, message, line > 0 ? line : test.Line, [], [], 0);

        // the case's input is checked before anything runs
        foreach (var g in test.Given)
        {
            var table = upstream.FirstOrDefault(u => string.Equals(u.Name, g.Table, StringComparison.OrdinalIgnoreCase));
            if (table == null)
                return Error($"`given` has `{g.Table}`, which the query of {modelName} does not read (it reads: {(upstream.Count == 0 ? "no tables" : string.Join(", ", upstream.Select(u => u.Name)))}).", g.Line);
            foreach (var row in g.Rows)
                if (Check(table.Columns, row, $"`given` row of `{g.Table}`", requireNotNull: true) is { } problem) return Error(problem, row.Line);
        }
        if (test.Expect != null)
            foreach (var row in test.Expect)
                if (Check(declared, row, $"`expect` row of {modelName}", requireNotNull: false) is { } problem) return Error(problem, row.Line);
        if (upstream.Any(u => string.Equals(u.Name, "result", StringComparison.OrdinalIgnoreCase) || string.Equals(u.Name, "main.result", StringComparison.OrdinalIgnoreCase)))
            return Error("a table named `result` is read by the query; `result` is the name of what the query returns in a test.");

        using var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        try
        {
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" }) Exec(db, setting);
            foreach (var statement in (macros?.Schemas ?? []).Concat(macros?.Types ?? [])) Exec(db, statement);

            foreach (var u in upstream)
            {
                var (schema, table) = Split(u.Name);
                Exec(db, $"CREATE SCHEMA IF NOT EXISTS {Q(schema)}");
                Exec(db, $"CREATE TABLE {Q(schema)}.{Q(table)} ({string.Join(", ", u.Columns.Select(c => $"{Q(c.Name)} {c.Type.Trim()}{(c.Nullable ? "" : " NOT NULL")}"))})");
                if (test.Given.FirstOrDefault(g => string.Equals(g.Table, u.Name, StringComparison.OrdinalIgnoreCase)) is { } given)
                    Insert(db, $"{Q(schema)}.{Q(table)}", u.Columns, given.Rows);
            }

            foreach (var statement in macros?.Macros ?? []) Exec(db, statement);
            // the query runs the way the model's connection compares strings; what it made is compared exactly (plain columns), as a test must
            if (collation != null) Exec(db, $"SET default_collation = '{collation}'");
            try { Exec(db, $"CREATE TABLE result AS {sql.Trim().TrimEnd(';').TrimEnd()}"); }
            finally { if (collation != null) Exec(db, "RESET default_collation"); }
            var actualNames = new List<string>();
            using (var d = db.CreateCommand())
            {
                d.CommandText = "DESCRIBE result";
                using var r = d.ExecuteReader();
                while (r.Read()) actualNames.Add(r.GetString(0));
            }
            var missing = declared.Where(c => !actualNames.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name).ToList();
            if (missing.Count > 0) return Error($"the query does not return the declared column(s) {string.Join(", ", missing)}");

            if (test.Expect != null)
            {
                // what the query returned, as the declared columns and types, against what the case expects
                Exec(db, $"CREATE TABLE __actual AS SELECT {string.Join(", ", declared.Select(c => $"CAST({Q(c.Name)} AS {c.Type.Trim()}) AS {Q(c.Name)}"))} FROM result");
                Exec(db, $"CREATE TABLE __expected ({string.Join(", ", declared.Select(c => $"{Q(c.Name)} {c.Type.Trim()}"))})");
                Insert(db, "__expected", declared, test.Expect);
                var diff = DuckSelect.Run(db,
                    $"SELECT 'missing' AS _diff, * FROM (SELECT * FROM __expected EXCEPT ALL SELECT * FROM __actual) UNION ALL SELECT 'unexpected', * FROM (SELECT * FROM __actual EXCEPT ALL SELECT * FROM __expected)", keep);
                if (diff.Outcome != RuleOutcome.Ran) return Error(diff.Message ?? "the comparison could not run");
                if (diff.Count > 0)
                {
                    var missingCount = Convert.ToInt32(Scalar(db, "SELECT count(*) FROM (SELECT * FROM __expected EXCEPT ALL SELECT * FROM __actual)"), CultureInfo.InvariantCulture);
                    return new CaseResult(CaseOutcome.Fail, $"the rows differ: {missingCount} expected row(s) missing, {diff.Count - missingCount} unexpected row(s) returned", test.Line, diff.Columns, diff.Rows, diff.Count);
                }
                if (test.Ordered)
                {
                    var order = DuckSelect.Run(db,
                        $"SELECT e.rn AS position, {string.Join(", ", declared.Select(c => $"e.{Q(c.Name)} AS {Q("expected_" + c.Name)}"))}, {string.Join(", ", declared.Select(c => $"a.{Q(c.Name)} AS {Q("returned_" + c.Name)}"))} " +
                        $"FROM (SELECT row_number() OVER () AS rn, * FROM __expected) e JOIN (SELECT row_number() OVER () AS rn, * FROM __actual) a ON a.rn = e.rn " +
                        $"WHERE {string.Join(" OR ", declared.Select(c => $"e.{Q(c.Name)} IS DISTINCT FROM a.{Q(c.Name)}"))} ORDER BY e.rn", keep);
                    if (order.Outcome != RuleOutcome.Ran) return Error(order.Message ?? "the order check could not run");
                    if (order.Count > 0) return new CaseResult(CaseOutcome.Fail, $"the rows are the expected ones but not in the expected order ({order.Count} position(s) differ)", test.Line, order.Columns, order.Rows, order.Count);
                }
            }

            if (test.Assert != null)
            {
                var run = DuckSelect.Run(db, test.Assert, keep);
                if (run.Outcome == RuleOutcome.NotASelect) return new CaseResult(CaseOutcome.NotASelect, run.Message, test.Line, [], [], 0);
                if (run.Outcome == RuleOutcome.CouldNotRun) return Error($"`assert` could not run: {run.Message}");
                if (run.Count > 0) return new CaseResult(CaseOutcome.Fail, $"`assert` returned {run.Count} violating row(s)", test.Line, run.Columns, run.Rows, run.Count);
            }
            return new CaseResult(CaseOutcome.Pass, null, test.Line, [], [], 0);
        }
        catch (DuckDBException ex)
        {
            return Error(FirstLine(ex.Message));
        }
    }

    /// <summary>A row names only columns the table has, and (for given rows) gives every NOT NULL column a value.</summary>
    private static string? Check(IReadOnlyList<ColumnDefinition> columns, TestRowData row, string where, bool requireNotNull)
    {
        foreach (var (name, _) in row.Values)
            if (!columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                return $"{where} has the column `{name}`, which is not declared (declared: {string.Join(", ", columns.Select(c => c.Name))}).";
        if (requireNotNull)
            foreach (var c in columns.Where(c => !c.Nullable))
                if (!row.Values.TryGetValue(c.Name, out var v) || v.Text == null)
                    return $"{where} gives no value for `{c.Name}`, which is NOT NULL.";
        return null;
    }

    private static void Insert(DuckDBConnection db, string table, IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<TestRowData> rows)
    {
        if (rows.Count == 0) return;
        var names = string.Join(", ", columns.Select(c => Q(c.Name)));
        string Cell(ColumnDefinition c, TestRowData r) =>
            r.Values.TryGetValue(c.Name, out var v) && v.Text != null ? $"CAST({SqlString(v.Text)} AS {c.Type.Trim()})" : $"CAST(NULL AS {c.Type.Trim()})";
        for (var i = 0; i < rows.Count; i += 200)
            Exec(db, $"INSERT INTO {table} ({names}) VALUES {string.Join(", ", rows.Skip(i).Take(200).Select(r => "(" + string.Join(", ", columns.Select(c => Cell(c, r))) + ")"))}");
    }

    private static object? Scalar(DuckDBConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static (string SchemaName, string Table) Split(string name)
    {
        var i = name.LastIndexOf('.');
        return i < 0 ? ("main", name) : (name[..i], name[(i + 1)..]);
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static string SqlString(string s) => "'" + s.Replace("'", "''") + "'";
    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private static void Exec(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
