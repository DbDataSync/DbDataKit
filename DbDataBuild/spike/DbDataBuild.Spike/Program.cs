using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;
using DbDataBuild.Sql;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Npgsql;

// Spike runner (DESIGN.md milestone 1).
//   transpile <root>                         print DuckDB -> target SQL for every construct
//   diff <root> <mssql host:port> <pg host:port>   execute on DuckDB (oracle) and the targets, compare results
// Synthetic data only. Connects only to the endpoints given on the command line (local ephemeral containers).
var mode = args.Length > 0 ? args[0] : "transpile";
var root = args.Length > 1 && mode is not ("ast" or "analyze" or "t" or "pgvalidate") ? args[1] : ".";
var diags = new List<Diagnostic>();
var doc = StrictYamlReader.Read(File.ReadAllText(Path.Combine(root, "spike", "constructs.yml")), "constructs.yml", diags);
if (doc is not YamlSequence seq) { Console.Error.WriteLine("bad constructs.yml"); return 1; }
var constructs = seq.Items.Cast<YamlMapping>()
    .Select(m => (Id: Str(m, "id"), Sql: Str(m, "sql"), Expect: Str(m, "expect"))).ToList();
var opts = Environment.GetEnvironmentVariable("SPIKE_OPTS") ?? "{}";

if (mode == "covered")
{
    // covered <root> <diff.md>: node types seen only in constructs that MATCHed on every target, with the constructs as evidence.
    var matched = File.ReadAllLines(args[2]).Where(l => l.StartsWith("| ") && !l.StartsWith("| id") && !l.StartsWith("|---"))
        .Select(l => l.Split('|', StringSplitOptions.TrimEntries)).Where(c => c[3] == "MATCH" && c[4] == "MATCH" && c[5] == "MATCH").Select(c => c[1]).ToHashSet();
    var d1 = new List<Diagnostic>();
    var y1 = (YamlSequence)StrictYamlReader.Read(File.ReadAllText(Path.Combine(args[1], "spike", "constructs.yml")), "c", d1)!;
    var evidence = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (var m in y1.Items.Cast<YamlMapping>())
    {
        var id = ((YamlScalar)m.Get("id")!).Value;
        if (!matched.Contains(id)) continue;
        var ast = DbDataBuild.Sql.Ast.AstNode.Parse(Polyglot.Parse(((YamlScalar)m.Get("sql")!).Value, Dialects.Canonical).Data!);
        foreach (var t in ast.Descendants().Where(n => n.Type != "function").Select(n => n.Type).Distinct())
            (evidence.TryGetValue("node:" + t, out var l) ? l : evidence["node:" + t] = []).Add(id);
        foreach (var dt in ast.Descendants().Where(n => n.Type is "cast" or "try_cast")
                     .Select(n => n.TryGet("to", out var to) && to.TryGetProperty("data_type", out var dtp) ? dtp.GetString() : null).Where(x => x != null).Distinct())
            (evidence.TryGetValue("datatype:" + dt, out var l3) ? l3 : evidence["datatype:" + dt] = []).Add(id);
        foreach (var f in ast.Descendants().Where(n => n.Type == "function").Select(n => n.GetString("name")!.ToUpperInvariant()).Distinct())
            (evidence.TryGetValue("function:" + f, out var l2) ? l2 : evidence["function:" + f] = []).Add(id);
    }
    foreach (var (t, ids) in evidence)
    {
        var kind = t[..t.IndexOf(':')]; var name = t[(t.IndexOf(':') + 1)..];
        Console.WriteLine($"- {{ {kind}: {name}, evidence: [{string.Join(", ", ids.Take(3))}] }}");
    }
    return 0;
}
if (mode == "fabric-vs-tsql")
{
    foreach (var c in constructs)
    {
        var (_, ts) = Polyglot.TranspileOne(c.Sql, Dialects.Canonical, "tsql");
        var (_, fb) = Polyglot.TranspileOne(c.Sql, Dialects.Canonical, "fabric");
        if (ts != fb) Console.WriteLine($"## {c.Id}\nduckdb : {c.Sql}\ntsql   : {ts}\nfabric : {fb}\n");
    }
    return 0;
}
if (mode == "t")
{
    foreach (var t in new[] { "sqlserver", "fabric", "postgres" })
    {
        var (o, sql) = Polyglot.TranspileOne(args[1], Dialects.Canonical, Dialects.ForTarget(t), "{\"unsupportedLevel\":\"raise\"}");
        Console.WriteLine($"-- {t}\n{(o.Ok ? sql : "ERROR: " + o.Error)}\n");
    }
    return 0;
}
if (mode == "pgvalidate")
{
    foreach (var stmt in args.Skip(1))
    {
        var v = Polyglot.Validate(stmt, "postgresql");
        Console.WriteLine($"{(v.Valid ? "OK  " : "FAIL")} {stmt.Replace("\n", " ")[..Math.Min(70, stmt.Replace("\n", " ").Length)]}  {(v.Valid ? "" : v.ErrorsJson + v.Error)}");
    }
    return 0;
}
if (mode == "analyze")
{
    // analyze <sql> <schemaJson>: polyglot_analyze_query with a schema
    var opts2 = "{\"dialect\":\"duckdb\",\"schema\":" + args[2] + "}";
    Console.WriteLine(Polyglot.AnalyzeQuery(args[1], opts2) is { Ok: true } ok ? ok.Data : "ERR");
    return 0;
}
if (mode == "matrix-check")
{
    var md = new List<Diagnostic>();
    DbDataBuild.Sql.Matrix.MatrixLoader.LoadFromDirectory(Path.Combine(args[1], "matrix"), md);
    foreach (var d in md.Take(6)) Console.Write(DiagnosticFormatter.Format(d));
    Console.WriteLine($"{md.Count} diagnostics");
    return 0;
}
if (mode == "ast-types")
{
    var root0 = args.Length > 1 ? args[1] : ".";
    var d0 = new List<Diagnostic>();
    var y = (YamlSequence)StrictYamlReader.Read(File.ReadAllText(Path.Combine(root0, "spike", "constructs.yml")), "c", d0)!;
    foreach (var m in y.Items.Cast<YamlMapping>())
    {
        var id = ((YamlScalar)m.Get("id")!).Value; var sql0 = ((YamlScalar)m.Get("sql")!).Value;
        var ast = DbDataBuild.Sql.Ast.AstNode.Parse(Polyglot.Parse(sql0, Dialects.Canonical).Data!);
        Console.WriteLine($"{id,-20} {string.Join(",", ast.Descendants().Select(n => n.Type).Distinct().Where(t => t is not ("column" or "identifier" or "table" or "select" or "from" or "alias")))}");
    }
    return 0;
}
if (mode == "ast") { Console.WriteLine(Polyglot.Parse(args[1], Dialects.Canonical).Data); return 0; }
return mode == "transpile" ? Transpile() : Diff(args[2], args[3]);

static string Str(YamlMapping m, string k) => ((YamlScalar)m.Get(k)!).Value;

int Transpile()
{
    var sb = new StringBuilder($"polyglot-sql {Polyglot.Version()} @ {Polyglot.PinnedCommit[..7]}\n\n");
    foreach (var c in constructs)
    {
        sb.AppendLine($"### {c.Id}  (expect: {c.Expect})\nduckdb: `{c.Sql}`");
        foreach (var t in new[] { "sqlserver", "fabric", "postgres" })
        {
            var (o, sql) = Polyglot.TranspileOne(c.Sql, Dialects.Canonical, Dialects.ForTarget(t), opts);
            sb.AppendLine(o.Ok ? $"  {t,-9}: `{sql}`" : $"  {t,-9}: ERROR ({o.Status}) {o.Error}");
        }
        sb.AppendLine();
    }
    Console.Write(sb);
    return 0;
}

int Diff(string mssql, string pg)
{
    var seed = File.ReadAllText(Path.Combine(root, "spike", "seed.duckdb.sql"));
    using var duck = new DuckDBConnection("DataSource=:memory:");
    duck.Open();
    Exec(duck, seed);

    var sa = Environment.GetEnvironmentVariable("SPIKE_MSSQL_PASSWORD") ?? "Ddb!Spike_9xQ2";
    var (msHost, msPort) = Split(mssql);
    var ms = new SqlConnection($"Server={msHost},{msPort};User Id=sa;Password={sa};TrustServerCertificate=true;Encrypt=false");
    Retry(() => ms.Open());
    Exec(ms, "IF DB_ID('ddbspike') IS NOT NULL DROP DATABASE ddbspike; CREATE DATABASE ddbspike;");
    ms.ChangeDatabase("ddbspike");
    var ver = Scalar(ms, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(30)) + ' / ' + CAST(SERVERPROPERTY('Collation') AS varchar(60))");

    var (pgHost, pgPort) = Split(pg);
    var pgc = new NpgsqlConnection($"Host={pgHost};Port={pgPort};Username=postgres;Password={Environment.GetEnvironmentVariable("SPIKE_PG_PASSWORD") ?? "ddbspike"};Database=postgres");
    Retry(() => pgc.Open());
    Exec(pgc, "DROP TABLE IF EXISTS t; DROP TABLE IF EXISTS u;");
    var pgVer = Scalar(pgc, "SHOW server_version") + " / " + Scalar(pgc, "SELECT datcollate FROM pg_database WHERE datname = current_database()");

    Exec(ms, "CREATE TABLE t (id INT, a INT, b INT, s NVARCHAR(50), d DATE, ts DATETIME2(3), d1 DATE, d2 DATE, x INT); CREATE TABLE u (a INT, b INT);");
    Exec(pgc, "CREATE TABLE t (id INT, a INT, b INT, s VARCHAR(50), d DATE, ts TIMESTAMP(3), d1 DATE, d2 DATE, x INT); CREATE TABLE u (a INT, b INT);");
    foreach (var table in new[] { "t", "u" }) { Copy(duck, ms, table); Copy(duck, pgc, table); }

    var sb = new StringBuilder();
    sb.AppendLine($"polyglot-sql {Polyglot.Version()} @ {Polyglot.PinnedCommit[..7]}");
    sb.AppendLine($"SQL Server {ver}");
    sb.AppendLine($"PostgreSQL {pgVer}");
    sb.AppendLine("fabric* = Fabric-transpiled text executed on SQL Server as a proxy (no Fabric engine here).\n");
    sb.AppendLine("| id | expect | sqlserver | fabric* | postgres |\n|---|---|---|---|---|");
    var detail = new StringBuilder();

    foreach (var c in constructs)
    {
        var oracle = Run(duck, c.Sql);
        var cells = new List<string>();
        foreach (var (target, conn) in new (string, DbConnection)[] { ("sqlserver", ms), ("fabric", ms), ("postgres", pgc) })
        {
            var (o, sql) = Polyglot.TranspileOne(c.Sql, Dialects.Canonical, Dialects.ForTarget(target), opts);
            string verdict, note = "";
            if (!o.Ok) { verdict = "TRANSPILE_ERR"; note = o.Error ?? ""; }
            else
            {
                var syntax = target == "postgres" ? null : TSqlSyntaxError(sql!);
                var r = Run(conn, sql!);
                if (r.Error != null && oracle.Error != null) { verdict = "BOTH_ERR"; note = $"target: {r.Error} | duckdb: {oracle.Error}"; }
                else if (r.Error != null) { verdict = syntax == null ? "EXEC_ERR" : "SYNTAX_ERR"; note = r.Error; }
                else if (oracle.Error != null) { verdict = "ORACLE_ERR"; note = oracle.Error; }
                else
                {
                    var diff = FirstDiff(oracle, r);
                    verdict = diff == null ? "MATCH" : "MISMATCH";
                    note = diff ?? "";
                    if (verdict == "MATCH" && syntax != null) note = "scriptdom: " + syntax;
                }
                if (target != "postgres" && syntax != null && verdict != "SYNTAX_ERR") note = (note + " [scriptdom: " + syntax + "]").Trim();
            }
            cells.Add(verdict);
            if (verdict != "MATCH") detail.AppendLine($"- **{c.Id}** / {target}: {verdict}: {note}\n  - sql: `{sql}`");
        }
        sb.AppendLine($"| {c.Id} | {c.Expect} | {string.Join(" | ", cells)} |");
    }
    sb.AppendLine("\n## Non-matching details\n").Append(detail);
    Console.Write(sb);
    return 0;
}

// ---- helpers ----
static (string, string) Split(string hp) { var i = hp.LastIndexOf(':'); return (hp[..i], hp[(i + 1)..]); }

static void Retry(Action a)
{
    for (var i = 0; ; i++)
        try { a(); return; }
        catch when (i < 60) { Thread.Sleep(1000); }
}

static void Exec(DbConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static string Scalar(DbConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
}

static void Copy(DbConnection from, DbConnection to, string table)
{
    using var read = from.CreateCommand();
    read.CommandText = $"SELECT * FROM {table}";
    using var rd = read.ExecuteReader();
    var cols = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToList();
    var insert = $"INSERT INTO {table} ({string.Join(",", cols)}) VALUES ({string.Join(",", cols.Select((_, i) => "@p" + i))})";
    while (rd.Read())
    {
        using var cmd = to.CreateCommand();
        cmd.CommandText = insert;
        for (var i = 0; i < cols.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            var v = rd.GetValue(i);
            p.Value = v switch { null => DBNull.Value, DateOnly d => d.ToDateTime(TimeOnly.MinValue), _ => v };
            if (v is DateOnly) p.DbType = System.Data.DbType.Date;
            cmd.Parameters.Add(p);
        }
        cmd.ExecuteNonQuery();
    }
}

static string? TSqlSyntaxError(string sql)
{
    var parser = new TSql160Parser(true);
    parser.Parse(new StringReader(sql), out var errors);
    return errors.Count == 0 ? null : errors[0].Message;
}

static RunResult Run(DbConnection c, string sql)
{
    try
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        using var rd = cmd.ExecuteReader();
        var rows = new List<string>();
        while (rd.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i =>
                Norm(rd.IsDBNull(i) ? null : rd.GetValue(i), string.Equals(rd.GetDataTypeName(i), "date", StringComparison.OrdinalIgnoreCase)))));
        rows.Sort(StringComparer.Ordinal);
        return new(rows, null);
    }
    catch (Exception ex)
    {
        // Synthetic data only, so messages are safe to print here. The product must scrub these (DESIGN.md 14.2).
        var msg = ex.Message.Replace('\n', ' ').Replace('\r', ' ');
        return new([], msg.Length > 200 ? msg[..200] : msg);
    }
}

static string Norm(object? v, bool dateOnly = false) => v switch
{
    null => "∅",
    DateTime dt when dateOnly => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    bool b => b ? "1" : "0",
    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    DateTimeOffset o => o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    decimal m => NumStr(m),
    double d when double.IsNaN(d) || double.IsInfinity(d) => d.ToString(CultureInfo.InvariantCulture),
    double d => NumStr((decimal)Math.Round(d, 9)),
    float f => NumStr((decimal)Math.Round(f, 6)),
    sbyte or byte or short or ushort or int or uint or long or ulong => NumStr(Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
    byte[] bytes => Convert.ToHexString(bytes),
    string str => str,
    System.Collections.IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(e => Norm(e))) + "]",
    _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
};

static string NumStr(decimal m) => Math.Round(m, 9).ToString("0.#########", CultureInfo.InvariantCulture);

static string? FirstDiff(RunResult oracle, RunResult other)
{
    if (oracle.Rows.Count != other.Rows.Count) return $"row count duckdb={oracle.Rows.Count} target={other.Rows.Count}";
    for (var i = 0; i < oracle.Rows.Count; i++)
        if (oracle.Rows[i] != other.Rows[i]) return $"duckdb=[{oracle.Rows[i]}] target=[{other.Rows[i]}] (sorted row {i})";
    return null;
}

internal sealed record RunResult(List<string> Rows, string? Error);
