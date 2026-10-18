using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Targets.Rules;

namespace DbDataBuild.Tests.Unit;

/// <summary>Turning the rewrites that keep an engine equal to DuckDB off (DESIGN.md 7.6.2): the settings, how they combine, what is refused, and what it does to rendered queries. DDB-229 is covered here.</summary>
public class RewriteTests
{
    private static (RewriteSettings? Settings, List<Diagnostic> Diags) Read(string yaml)
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
        return (config?.Rewrites, diags);
    }

    [Fact]
    public void Every_rewrite_is_named_once_and_says_what_it_does_and_what_the_engine_does_without_it()
    {
        Assert.Equal(RewriteCatalog.All.Count, RewriteCatalog.Names.Distinct().Count());
        Assert.All(RewriteCatalog.All, r =>
        {
            Assert.NotEmpty(r.Targets);
            Assert.All(r.RequiredOn, t => Assert.Contains(t, r.Targets));
            Assert.False(string.IsNullOrWhiteSpace(r.Exact));
            Assert.False(string.IsNullOrWhiteSpace(r.Native));
        });
    }

    [Fact]
    public void Every_rewrite_the_target_rules_apply_is_in_the_catalog()
    {
        foreach (var target in new[] { "sqlserver", "postgres", "fabric", "oracle", "spark", "bigquery" })
            foreach (var version in new int?[] { null, 16, 17 })
                foreach (var name in TargetRules.RulesOf(target, version))
                    Assert.Contains(name, RewriteCatalog.Names);
    }

    [Fact]
    public void The_section_is_read_from_the_configuration()
    {
        var (s, diags) = Read("rewrites:\n  fidelity: native\n  disable: [length-trailing-spaces]\n  enable: [round-double]\n");
        Assert.Empty(diags);
        Assert.Equal("native", s!.Fidelity);
        Assert.Equal(["length-trailing-spaces"], s.Disable);
        Assert.Equal(["round-double"], s.Enable);
    }

    [Theory]
    [InlineData("rewrites: nope\n", "must be a mapping")]
    [InlineData("rewrites:\n  fidelity: loose\n", "exact")]
    [InlineData("rewrites:\n  disable: [not-a-rewrite]\n", "is not a rewrite")]
    [InlineData("rewrites:\n  disable: [round-double]\n  enable: [round-double]\n", "both `disable` and `enable`")]
    [InlineData("rewrites:\n  bogus: 1\n", "Unknown key")]
    public void A_bad_section_is_a_diagnostic_not_an_exception(string yaml, string expected)
    {
        var (_, diags) = Read(yaml);
        Assert.Contains(diags, d => (d.Found + d.Supported).Contains(expected));
    }

    private static RewriteSettings S(string? fidelity = null, string[]? disable = null, string[]? enable = null) => new(fidelity, disable ?? [], enable ?? [], 1);

    [Fact]
    public void Native_turns_off_every_rewrite_the_targets_can_do_without_and_keeps_the_ones_they_need()
    {
        var sqlServer = RewriteCatalog.Resolve(S("native"), null, ["sqlserver"], "m", null);
        Assert.Contains("length-trailing-spaces", sqlServer.Disabled);
        Assert.Contains("double-to-int", sqlServer.Disabled);
        Assert.DoesNotContain("concat-plus", sqlServer.Disabled);              // SQL Server cannot run || inside strpos
        Assert.DoesNotContain("pad-to-length", sqlServer.Disabled);
        Assert.DoesNotContain("split-part", sqlServer.Disabled);               // nothing to do on SQL Server at all
        Assert.DoesNotContain("round-double", RewriteCatalog.Resolve(S("native"), null, ["postgres"], "m", null).Disabled);   // PostgreSQL has no round(double, n)
        Assert.Contains("round-double", sqlServer.Disabled);                  // and on SQL Server it is only fidelity
        Assert.DoesNotContain("round-double", RewriteCatalog.Resolve(S("native"), null, ["sqlserver", "postgres"], "m", null).Disabled);   // one model, both connections: the stricter wins
    }

    [Fact]
    public void A_model_overrides_the_project_and_enable_brings_one_back()
    {
        var project = S("native");
        var model = S(enable: ["length-trailing-spaces"]);
        var r = RewriteCatalog.Resolve(project, model, ["sqlserver"], "m", null);
        Assert.DoesNotContain("length-trailing-spaces", r.Disabled);
        Assert.Contains("double-to-int", r.Disabled);
        Assert.True(RewriteCatalog.Resolve(project, S("exact"), ["sqlserver"], "m", null).IsDefault);       // a model can say `exact` for itself
        Assert.Equal(["date-diff-weeks"], RewriteCatalog.Resolve(S(disable: ["date-diff-weeks"]), null, ["sqlserver"], "m", null).Disabled);
    }

    [Fact]
    public void A_rewrite_a_target_needs_cannot_be_named_in_disable()
    {
        var diags = new List<Diagnostic>();
        var r = RewriteCatalog.Resolve(S(disable: ["pad-to-length", "length-trailing-spaces"]), null, ["sqlserver"], "marts.m", diags);
        var d = Assert.Single(diags);
        Assert.Equal("DDB-229", d.Code);
        Assert.Contains("pad-to-length", d.Found);
        Assert.Equal(["length-trailing-spaces"], r.Disabled);                  // the one that can go still goes
        Assert.Empty(RewriteCatalog.Resolve(S(disable: ["pad-to-length"]), null, ["postgres"], "m", new List<Diagnostic>()).Disabled);   // PostgreSQL has lpad: nothing to disable, nothing to refuse
    }

    [Fact]
    public void A_target_rule_that_is_off_is_not_applied()
    {
        const string sql = "SELECT length(s) AS n, date_diff('week', a, b) AS w FROM t";
        Assert.Equal([TargetRules.DateDiffWeeks, TargetRules.LengthKeepsTrailingSpaces], TargetRules.Apply(sql, "sqlserver").Rules);
        var r = TargetRules.Apply(sql, "sqlserver", new RewritePolicy([TargetRules.LengthKeepsTrailingSpaces]));
        Assert.Equal([TargetRules.DateDiffWeeks], r.Rules);
        Assert.Equal(sql, TargetRules.Apply(sql, "sqlserver", new RewritePolicy([TargetRules.LengthKeepsTrailingSpaces, TargetRules.DateDiffWeeks])).Sql);   // nothing left to do: the text is returned as it was
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void A_project_that_asks_for_native_behavior_renders_shorter_queries_and_says_so()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-rewrite-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, Run("project", "create", "retail", dir).Exit);
            Assert.Equal(0, Run("project", "compile", "--project", dir).Exit);
            var exact = File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/marts.fct_shipment_packages/load.default.sql"));
            var lowered = File.ReadAllText(Path.Combine(dir, "rendered/lowered/marts.agg_daily_sales/lowered.sql"));
            Assert.Contains("-- type rules:", lowered);
            Assert.DoesNotContain("rewrites off", exact);

            File.AppendAllText(Path.Combine(dir, "dbdatabuild.yml"), "\nrewrites:\n  fidelity: native\n");
            var stale = Run("project", "compile", "--check", "--project", dir);
            Assert.NotEqual(0, stale.Exit);                                          // the rendered files changed: the setting is part of what they say
            Assert.Equal(0, Run("project", "compile", "--project", dir).Exit);
            var native = File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/marts.fct_shipment_packages/load.default.sql"));
            var nativeLowered = File.ReadAllText(Path.Combine(dir, "rendered/lowered/marts.agg_daily_sales/lowered.sql"));
            Assert.Contains("-- rewrites off:", native);
            Assert.Contains("-- rewrites off:", nativeLowered);
            Assert.Contains("sum-widen", lowered);
            Assert.DoesNotContain(nativeLowered.Split('\n'), l => l.StartsWith("-- type rules:", StringComparison.Ordinal) && l.Contains("sum-widen"));      // it is listed on the line that says what is off, not as a rule that fired
            Assert.DoesNotContain("CAST(quantity AS BIGINT)", nativeLowered);
            var (validateExit, validateOut, _) = Run("project", "compile", "--project", dir);
            Assert.True(validateExit == 0, validateOut);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Naming_a_required_rewrite_in_a_project_stops_validation_with_the_diagnostic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-rewrite-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, Run("project", "create", "starter", dir).Exit);
            File.AppendAllText(Path.Combine(dir, "dbdatabuild.yml"), "\nrewrites:\n  disable: [concat-plus]\n");
            var (exit, output, err) = Run("project", "compile", "--project", dir);
            Assert.NotEqual(0, exit);
            Assert.Contains("DDB-229", output + err);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
