using DbDataBuild.Cli;
using DbDataBuild.Semantic;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The data checks generated from a semantic model (plan 19 J09): a check for each pair of adjacent hierarchy levels and two for each relationship, each one SELECT in the SQL of its connection's engine
/// that returns the violations. The text is tested here; what the checks find on real engines is the conformance suite's (`SemanticChecksConformanceTests`).
/// </summary>
public class SemanticChecksTests : IDisposable
{
    private readonly List<string> dirs = [];

    public void Dispose() { foreach (var d in dirs.Where(Directory.Exists)) Directory.Delete(d, recursive: true); }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    private string Project(string template)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-checks-" + Guid.NewGuid().ToString("N"));
        dirs.Add(dir);
        Assert.Equal(0, Run("project", "create", template, dir).Exit);
        return dir;
    }

    private static GeneratedSemanticChecks ChecksOf(string dir)
    {
        var ctx = ProjectContext.Load(dir);
        Assert.DoesNotContain(ctx.Diagnostics, d => d.Severity == DbDataBuild.Core.Severity.Error);
        return SemanticChecks.Generate(ctx.Semantic.Models.Single(), ctx.Config);
    }

    [Fact]
    public void The_calendar_template_has_a_check_for_each_level_pair_and_two_for_its_relationship()
    {
        var checks = ChecksOf(Project("calendar")).Checks;
        Assert.Equal(
        [
            "semantic.calendar_sales.hierarchy.date.by_fiscal_year.date.fiscal_month@sqlserver",
            "semantic.calendar_sales.hierarchy.date.by_fiscal_year.date.fiscal_quarter@sqlserver",
            "semantic.calendar_sales.relationship.sales_to_date.grain@sqlserver",
            "semantic.calendar_sales.relationship.sales_to_date.orphans@sqlserver",
        ], checks.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        var quarter = checks.Single(c => c.Name.EndsWith("fiscal_quarter@sqlserver", StringComparison.Ordinal));
        Assert.Equal("hierarchy", quarter.Kind);
        Assert.Equal("semantic/calendar.yml", quarter.File);
        Assert.Equal(File.ReadAllLines(Path.Combine(ChecksOfDir, "semantic", "calendar.yml")).ToList().FindIndex(l => l.StartsWith("  date.by_fiscal_year:", StringComparison.Ordinal)) + 1, quarter.Line);
        Assert.Equal(
            "SELECT [fiscal_quarter] AS [child], COUNT(DISTINCT [fiscal_year]) + MAX(CASE WHEN [fiscal_year] IS NULL THEN 1 ELSE 0 END) AS [parents] FROM [marts].[dim_date] WHERE [fiscal_quarter] IS NOT NULL GROUP BY [fiscal_quarter] HAVING COUNT(DISTINCT [fiscal_year]) + MAX(CASE WHEN [fiscal_year] IS NULL THEN 1 ELSE 0 END) > 1",
            quarter.Sql);
        Assert.Equal(
            "SELECT f.[sold_on] AS [sold_on], COUNT(*) AS [row_count] FROM [marts].[fct_sales] f LEFT JOIN [marts].[dim_date] t ON f.[sold_on] = t.[calendar_date] WHERE t.[calendar_date] IS NULL AND f.[sold_on] IS NOT NULL GROUP BY f.[sold_on]",
            checks.Single(c => c.Kind == "relationship_orphans").Sql);
        Assert.Equal("SELECT [calendar_date], COUNT(*) AS [row_count] FROM [marts].[dim_date] GROUP BY [calendar_date] HAVING COUNT(*) > 1", checks.Single(c => c.Kind == "relationship_grain").Sql);
    }

    private string ChecksOfDir => dirs[^1];

    [Fact]
    public void On_PostgreSQL_the_names_are_quoted_the_PostgreSQL_way()
    {
        var dir = Project("calendar");
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [postgres]}\ntracking: { connection: postgres }\nparameters:\n  fiscal_start_month: 7\n");
        var checks = ChecksOf(dir).Checks;
        Assert.All(checks, c => Assert.Equal("postgres", c.Connection));
        Assert.All(checks, c => Assert.EndsWith("@postgres", c.Name));
        Assert.Contains("FROM \"marts\".\"dim_date\"", checks[0].Sql);
        Assert.DoesNotContain("[", string.Concat(checks.Select(c => c.Sql)));
    }

    [Fact]
    public void A_level_pair_in_two_models_is_listed_as_not_checked_with_the_reason()
    {
        var dir = Project("calendar");
        var file = Path.Combine(dir, "semantic", "calendar.yml");
        // a hierarchy whose child is a column of the fact: two models, which the check does not join
        File.WriteAllText(file, File.ReadAllText(file).Replace("levels: [date.fiscal_year, date.fiscal_quarter, date.fiscal_month]", "levels: [date.fiscal_year, date.fiscal_month, sales.sold]")
            .Replace("dimensions:\n", "dimensions:\n  sales:\n    attributes:\n      sold:\n        type: date\n        bindings:\n          default: {table: marts.fct_sales, column: sold_on}\n"));
        var generated = ChecksOf(dir);
        Assert.Contains(generated.Skipped, s => s.Description.Contains("`date.fiscal_month` > `sales.sold`") && s.Reason.Contains("two models"));
        Assert.DoesNotContain(generated.Checks, c => c.Name.EndsWith("sales.sold@sqlserver", StringComparison.Ordinal));
    }

    [Fact]
    public void The_checks_are_listed_by_project_tests_list_and_not_run_by_tests_run()
    {
        var dir = Project("calendar");
        var (exit, output, err) = Run("project", "tests", "list", "--project", dir, "--kind", "semantic", "--format", "json");
        Assert.True(exit == 0, output + err);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var tests = doc.RootElement.GetProperty("data").GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(4, tests.Count);
        Assert.All(tests, t => Assert.Equal("semantic", t.GetProperty("kind").GetString()));
        Assert.Equal(CliApp.ExitUsage, Run("project", "tests", "list", "--project", dir, "--kind", "nope").Exit);

        var (runExit, runOut, _) = Run("project", "tests", "run", "--project", dir);
        Assert.Equal(0, runExit);
        Assert.DoesNotContain("semantic.", runOut);
    }

    [Fact]
    public void Check_without_a_read_login_is_refused_naming_the_variable_and_one_without_semantic_models_says_so()
    {
        var dir = Project("calendar");
        var (exit, _, err) = Run("connection", "check", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-501", err);
        Assert.Contains("DBDATA_BUILD_SQLSERVER_READ", err);

        Directory.Delete(Path.Combine(dir, "semantic"), recursive: true);
        var (noneExit, noneOut, _) = Run("connection", "check", "--project", dir);
        Assert.Equal(0, noneExit);
        Assert.Contains("No checks: the project has no semantic models", noneOut);
    }

    // What the generated SQL finds, run in DuckDB (the same text, DuckDB's quoting) over small tables with one fault each.

    private static List<string[]> Rows(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string[]>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
        return rows.OrderBy(x => string.Join("|", x), StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void The_hierarchy_sql_finds_a_child_with_two_parents_and_counts_a_missing_parent_as_one()
    {
        var dialect = DbDataBuild.Models.SharedQuoting.For("duckdb")!;
        using var c = new DuckDBConnection("DataSource=:memory:");
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE SCHEMA marts; CREATE TABLE marts.d (y VARCHAR, q VARCHAR); INSERT INTO marts.d VALUES ('Y1','Q1'),('Y1','Q1'),('Y1','Q2'),('Y2','Q2'),('Y2','Q3'),(NULL,'Q3'),('Y3','Q4'),(NULL,'Q5'),(NULL,'Q5'),('Y4',NULL)";
            cmd.ExecuteNonQuery();
        }
        var sql = SemanticChecks.HierarchySql(dialect, "marts.d", "y", "q");
        // Q2 is under Y1 and Y2; Q3 under Y2 and a missing year; Q1, Q4 and Q5 each have one parent (a missing one counts as one); a missing child is not a member
        Assert.Equal([["Q2", "2"], ["Q3", "2"]], Rows(c, sql));
    }

    [Fact]
    public void The_relationship_sql_finds_orphan_keys_and_repeated_keys_and_ignores_a_missing_key()
    {
        var dialect = DbDataBuild.Models.SharedQuoting.For("duckdb")!;
        using var c = new DuckDBConnection("DataSource=:memory:");
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE SCHEMA marts; CREATE TABLE marts.f (a INTEGER, b INTEGER); CREATE TABLE marts.d (a INTEGER, b INTEGER); " +
                "INSERT INTO marts.f VALUES (1,1),(1,1),(2,1),(9,9),(9,9),(9,9),(NULL,5),(3,NULL); INSERT INTO marts.d VALUES (1,1),(2,1),(2,1),(3,3)";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal([["9", "9", "3"]], Rows(c, SemanticChecks.OrphansSql(dialect, "marts.f", ["a", "b"], "marts.d", ["a", "b"])));
        Assert.Equal([["2", "1", "2"]], Rows(c, SemanticChecks.GrainSql(dialect, "marts.d", ["a", "b"])));
    }
}
