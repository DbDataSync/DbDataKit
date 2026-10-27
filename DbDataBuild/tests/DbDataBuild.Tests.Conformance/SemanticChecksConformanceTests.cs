using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The data checks generated from a semantic model (plan 19 J09), on the real engines, through the commands a person would type: the `calendar` template is created, seeded, deployed, and
/// `connection check` runs the checks its hierarchy and relationship bring. On the data the project built they all pass; then each is broken by hand (a day in two quarters, a day the sales need
/// deleted, a day twice) and each is found, by name, with its count, and with the key only when the person asked for values. Quick group: a few seconds on each engine.
/// </summary>
public class SemanticChecksConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private const string PostgresConfig = "defaults: {connections: [postgres]}\ntracking: { connection: postgres }\nparameters:\n  fiscal_start_month: 7\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\npolicy:\n  severity:\n    approximated: note\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_generated_checks_pass_on_what_the_project_built_and_find_each_fault_made_by_hand(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var scope = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-semantic-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
            (int Exit, string Out, string Err) Cli(params string[] args)
            {
                var o = new StringWriter();
                var e = new StringWriter();
                var exit = CliApp.Run([.. args, .. (args[0] == "project" && args[1] == "create" ? [] : new[] { "--project", dir })], o, e, environment: Env);
                return (exit, o.ToString(), e.ToString());
            }
            void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

            Ok(Cli("project", "create", "calendar", dir), "create");
            if (name == "postgres") File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), PostgresConfig);
            Ok(Cli("connection", "seed", "--apply"), "seed");
            Ok(Cli("project", "compile"), "compile");
            Ok(Cli("connection", "init", "--apply"), "init");
            var plan = Cli("connection", "deploy", "--write-plan", "--accept-inferred");
            Ok(plan, "plan");
            Ok(Cli("connection", "deploy", "--apply-plan", Path.Combine(dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value)), "apply");
            Assert.Equal(["1096"], await engine.RowsAsync("SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM marts.dim_date"));
            Assert.NotEqual(["0"], await engine.RowsAsync("SELECT CAST(COUNT(*) AS VARCHAR(10)) FROM marts.fct_sales"));

            // what the project built satisfies every check the semantic model brings
            var clean = Cli("connection", "check", "--format", "json");
            Ok(clean, "check on what the project built");
            using (var doc = JsonDocument.Parse(clean.Out))
            {
                var data = doc.RootElement.GetProperty("data");
                Assert.Equal(4, data.GetProperty("counts").GetProperty("total").GetInt32());
                Assert.Equal(4, data.GetProperty("counts").GetProperty("passed").GetInt32());
                Assert.All(data.GetProperty("checks").EnumerateArray(), c => Assert.Equal("pass", c.GetProperty("status").GetString()));
            }

            // three faults made by hand: a day whose quarter is another year's, a day the sales need deleted, and a day twice
            var orphanDay = (await engine.RowsAsync(name == "sqlserver" ? "SELECT CONVERT(VARCHAR(10), MIN(sold_on), 23) FROM marts.fct_sales" : "SELECT CAST(MIN(sold_on) AS VARCHAR(10)) FROM marts.fct_sales"))[0];
            await engine.ExecAsync("UPDATE marts.dim_date SET fiscal_quarter = 'FY2999-Q9' WHERE calendar_date = '2025-03-15'");
            await engine.ExecAsync($"DELETE FROM marts.dim_date WHERE calendar_date = '{orphanDay}'");
            await engine.ExecAsync("INSERT INTO marts.dim_date SELECT * FROM marts.dim_date WHERE calendar_date = '2025-06-01'");

            var broken = Cli("connection", "check", "--format", "json");
            Assert.Equal(CliApp.ExitFindings, broken.Exit);
            using (var doc = JsonDocument.Parse(broken.Out))
            {
                var data = doc.RootElement.GetProperty("data");
                Assert.Equal(3, data.GetProperty("counts").GetProperty("failed").GetInt32());
                Assert.Equal(1, data.GetProperty("counts").GetProperty("passed").GetInt32());
                var byName = data.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);
                var prefix = $"semantic.calendar_sales.";
                Assert.Equal("fail", byName[$"{prefix}hierarchy.date.by_fiscal_year.date.fiscal_month@{name}"].GetProperty("status").GetString());
                Assert.Equal(1, byName[$"{prefix}hierarchy.date.by_fiscal_year.date.fiscal_month@{name}"].GetProperty("violations").GetInt64());      // FY2025-P09 is in two quarters
                Assert.Equal("pass", byName[$"{prefix}hierarchy.date.by_fiscal_year.date.fiscal_quarter@{name}"].GetProperty("status").GetString());       // FY2999-Q9 is in one year
                Assert.Equal(1, byName[$"{prefix}relationship.sales_to_date.orphans@{name}"].GetProperty("violations").GetInt64());                        // one day the sales need
                Assert.Equal(1, byName[$"{prefix}relationship.sales_to_date.grain@{name}"].GetProperty("violations").GetInt64());                          // one day twice
                Assert.All(byName.Values, c => Assert.False(c.TryGetProperty("examples", out _)));                                                       // counts only: no value was read
            }
            Assert.DoesNotContain("FY2025-P09", broken.Out);
            Assert.DoesNotContain(orphanDay, broken.Out);

            // each finding is a diagnostic at the line of the semantic file that holds the claim
            var line = File.ReadAllLines(Path.Combine(dir, "semantic", "calendar.yml")).ToList().FindIndex(l => l.StartsWith("  date.by_fiscal_year:", StringComparison.Ordinal)) + 1;
            var text = Cli("connection", "check");
            Assert.Equal(CliApp.ExitFindings, text.Exit);
            Assert.Contains($"error DDB-601  semantic/calendar.yml:{line}", text.Err);
            Assert.Contains("hierarchy.date.by_fiscal_year.date.fiscal_month", text.Err);
            Assert.Contains("FAIL  semantic.calendar_sales.relationship.sales_to_date.orphans@" + name + "  (1 violation(s))", text.Out);

            // values only on request, and then the keys that break each check
            var shown = Cli("connection", "check", "--show-values", "--limit", "5", "--format", "json");
            using (var doc = JsonDocument.Parse(shown.Out))
            {
                var byName = doc.RootElement.GetProperty("data").GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);
                var month = byName[$"semantic.calendar_sales.hierarchy.date.by_fiscal_year.date.fiscal_month@{name}"];
                Assert.Equal(["child", "parents"], month.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());
                Assert.Equal(["FY2025-P09", "2"], month.GetProperty("examples")[0].EnumerateArray().Select(c => c.GetString()!).ToArray());
                Assert.Equal(orphanDay, byName[$"semantic.calendar_sales.relationship.sales_to_date.orphans@{name}"].GetProperty("examples")[0][0].GetString());
                Assert.Equal("2025-06-01", byName[$"semantic.calendar_sales.relationship.sales_to_date.grain@{name}"].GetProperty("examples")[0][0].GetString());
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
