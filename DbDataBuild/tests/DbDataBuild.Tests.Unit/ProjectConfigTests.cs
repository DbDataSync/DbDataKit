using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class ProjectConfigTests
{
    private const string Full = """
        defaults: {connections: [sqlserver, postgres]}
        connections:
          sqlserver: { version: 16 }
          postgres: { version: "17" }
          fabric: {}
        tracking: { connection: sqlserver, schema: ddb_meta }
        string_semantics:
          case: sensitive
          accent: insensitive
          trailing_space: significant
          collations:
            default:
              duckdb: NOCASE
              sqlserver: Latin1_General_100_CS_AI
            legacy:
              sqlserver: SQL_Latin1_General_CP1_CI_AS
        policy:
          severity:
            approximated: error
            emulated: warning
            unverified: note
            not_covered: error
        """;

    public sealed record Case(string Name, string Yaml, bool SchemaValid, params string[] Codes)
    {
        public override string ToString() => Name;
    }

    public static readonly Case[] Cases =
    [
        new("full example", Full, true),
        new("empty mapping", "{}", true),
        new("only default connections", "defaults: {connections: [fabric]}\n", true),
        new("version as integer and string", "connections:\n  sqlserver: { version: 17 }\n  postgres: { version: \"16\" }\n", true),
        new("unknown key", "surprise: 1\n", false, "DDB-104"),
        new("invalid default connection name", "defaults: {connections: [\"9 bad\"]}\n", false, "DDB-106"),
        new("empty default connections", "defaults: {connections: []}\n", false, "DDB-106"),
        new("duplicate default connections", "defaults: {connections: [fabric, fabric]}\n", false, "DDB-106"),
        new("default connections not a list", "defaults: {connections: sqlserver}\n", false, "DDB-106"),
        new("connection without an engine", "connections:\n  oracle: { version: 19 }\n", false, "DDB-105"),
        new("unknown target setting", "connections:\n  sqlserver: { edition: enterprise }\n", false, "DDB-104"),
        new("version zero", "connections:\n  sqlserver: { version: 0 }\n", false, "DDB-106"),
        new("version not a number", "connections:\n  sqlserver: { version: sixteen }\n", false, "DDB-106"),
        new("version with a fraction", "connections:\n  sqlserver: { version: 16.5 }\n", false, "DDB-106"),
        new("target settings not a mapping", "connections:\n  sqlserver: 16\n", false, "DDB-106"),
        new("tracking schema with a dash", "tracking: { schema: my-schema }\n", false, "DDB-106"),
        new("tracking schema starting with a digit", "tracking: { schema: 1abc }\n", false, "DDB-106"),
        new("tracking: none", "tracking: none\n", true),
        new("tracking on a connection that does not exist", "tracking: { connection: nowhere }\n", true, "DDB-106"),     // a name the schema cannot know is missing
        new("tracking with an unknown key", "tracking: { place: sqlserver }\n", false, "DDB-104"),
        new("a connection's own tracking", "connections:\n  scratch: { engine: postgres, tracking: none }\n  vendor: { engine: sqlserver, tracking: { connection: scratch, schema: audit } }\n", true),
        new("bad case value", "string_semantics: { case: maybe }\n", false, "DDB-106"),
        new("bad trailing_space value", "string_semantics: { trailing_space: yes }\n", false, "DDB-106"),
        new("unknown string_semantics key", "string_semantics: { unicode: nfc }\n", false, "DDB-104"),
        new("unknown collation engine", "string_semantics:\n  collations:\n    default: { oracle: X }\n", false, "DDB-104"),
        new("empty collation name", "string_semantics:\n  collations:\n    default: { duckdb: \"\" }\n", false, "DDB-106"),
        new("collation entry not a mapping", "string_semantics:\n  collations:\n    default: NOCASE\n", false, "DDB-106"),
        new("unsupported severity is not configurable", "policy:\n  severity:\n    unsupported: warning\n", false, "DDB-104"),
        new("bad severity value", "policy:\n  severity:\n    approximated: loud\n", false, "DDB-106"),
        new("unknown policy key", "policy:\n  mode: strict\n", false, "DDB-104"),
        new("hook groups", "hook_groups:\n  standard:\n    - {name: grant, event: post_create, script: hooks/grant.sql}\n    - {name: stats, event: post_load, script: {sqlserver: hooks/s.sql, postgres: hooks/p.sql}, effect: data}\n  empty: []\n", true),
        new("hook group not a list", "hook_groups:\n  standard: nope\n", false, "DDB-106"),
        new("hook group entry without event", "hook_groups:\n  standard:\n    - {name: grant, script: hooks/grant.sql}\n", false, "DDB-105"),
        new("hook groups do not nest", "hook_groups:\n  standard:\n    - {use: other}\n", false, "DDB-106"),
        new("hook group name with a space", "hook_groups:\n  \"my group\":\n    - {name: x, event: post_load, script: hooks/x.sql}\n", false, "DDB-106"),
        new("hook group duplicate hook name", "hook_groups:\n  g:\n    - {name: x, event: post_load, script: hooks/x.sql}\n    - {name: x, event: pre_load, script: hooks/y.sql}\n", true, "DDB-102"),
        new("the lifecycle settings", "plans:\n  deploy: { keep: committed, audit: full, require_clean_tree: true }\n  refresh: { keep: database, audit: minimal }\nrefresh: { check: live, on_fail: warn }\nretention: { statement_logs_days: 7 }\n", true),
        new("a connection's own lifecycle", "connections:\n  dev: { engine: postgres, plans: { deploy: { keep: ephemeral, audit: minimal } }, refresh: { check: none } }\n", true),
        new("an unknown plan keep", "plans:\n  deploy: { keep: forever }\n", false, "DDB-106"),
        new("an unknown audit level", "plans:\n  refresh: { audit: everything }\n", false, "DDB-106"),
        new("an unknown refresh check", "refresh: { check: always }\n", false, "DDB-106"),
        new("an unknown on_fail", "refresh: { on_fail: ignore }\n", false, "DDB-106"),
        new("require_clean_tree is for deploy only", "plans:\n  refresh: { require_clean_tree: true }\n", false, "DDB-104"),
        new("an unknown lane", "plans:\n  repair: { keep: committed }\n", false, "DDB-104"),
        new("the retention is a whole number", "retention: { statement_logs_days: soon }\n", false, "DDB-106"),
        new("the retention is not negative", "retention: { statement_logs_days: -1 }\n", false, "DDB-106"),
    ];

    public static TheoryData<Case> Data
    {
        get { var d = new TheoryData<Case>(); foreach (var c in Cases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(Data))]
    public void Config_schema_and_loader_agree(Case c)
    {
        Assert.Equal(c.SchemaValid, SchemaAccepts(LoadSchema("config"), c.Yaml));
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load(c.Yaml, "dbdatabuild.yml", diags);
        if (c.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(cfg);
        }
        else
        {
            foreach (var code in c.Codes) Assert.Contains(diags, d => d.Code == code);
            Assert.Null(cfg);
        }
    }

    [Fact]
    public void The_lifecycle_defaults_are_the_documented_ones_and_a_connection_lays_its_own_over_the_project()
    {
        var none = ProjectConfigLoader.Load("{}", "dbdatabuild.yml", [])!;
        Assert.Equal(new LifecycleSettings(new(PlanKeep.Committed, AuditLevel.Full), new(PlanKeep.Committed, AuditLevel.Standard), true, RefreshCheck.Objects, OnFail.Block, 30), none.Lifecycle);
        var cfg = ProjectConfigLoader.Load("plans:\n  deploy: { audit: standard }\nrefresh: { check: live }\nretention: { statement_logs_days: 0 }\nconnections:\n  dev: { engine: postgres, plans: { deploy: { keep: ephemeral } }, refresh: { on_fail: warn } }\n", "dbdatabuild.yml", [])!;
        Assert.Equal(new LifecycleSettings(new(PlanKeep.Committed, AuditLevel.Standard), new(PlanKeep.Committed, AuditLevel.Standard), true, RefreshCheck.Live, OnFail.Block, 0), cfg.LifecycleOf("sqlserver"));
        Assert.Equal(new LifecycleSettings(new(PlanKeep.Ephemeral, AuditLevel.Standard), new(PlanKeep.Committed, AuditLevel.Standard), true, RefreshCheck.Live, OnFail.Warn, 0), cfg.LifecycleOf("dev"));
        Assert.Contains("refresh check: live", cfg.Describe());
        Assert.DoesNotContain("refresh check", none.Describe());          // the defaults are not repeated in every header
    }

    [Fact]
    public void Full_example_is_read_completely()
    {
        var cfg = ProjectConfigLoader.Load(Full, "dbdatabuild.yml", [])!;
        Assert.Equal(["sqlserver", "postgres"], cfg.DefaultConnections);
        Assert.Equal(16, cfg.TargetVersions["sqlserver"]);
        Assert.Equal(17, cfg.TargetVersions["postgres"]);
        Assert.False(cfg.TargetVersions.ContainsKey("fabric"));
        Assert.Equal("ddb_meta", cfg.TrackingSchemaName);
        Assert.Equal(CaseSensitivity.Sensitive, cfg.StringSemantics.Case);
        Assert.Equal(AccentSensitivity.Insensitive, cfg.StringSemantics.Accent);
        Assert.Equal(TrailingSpace.Significant, cfg.StringSemantics.TrailingSpace);
        Assert.Equal("Latin1_General_100_CS_AI", cfg.StringSemantics.Collations["default"]["sqlserver"]);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", cfg.StringSemantics.Collations["legacy"]["sqlserver"]);
        Assert.Equal(Severity.Error, cfg.Policy[PolicyKeys.Approximated]);
        Assert.Equal(Severity.Warning, cfg.Policy[PolicyKeys.Emulated]);
        Assert.Equal(Severity.Note, cfg.Policy[PolicyKeys.Unverified]);
        Assert.Equal(Severity.Error, cfg.Policy[PolicyKeys.NotCovered]);
    }

    [Fact]
    public void Absent_keys_take_the_documented_defaults()
    {
        var cfg = ProjectConfigLoader.Load("tracking: { schema: x }\n", "dbdatabuild.yml", [])!;
        var d = ProjectConfig.Default;
        Assert.Equal(["sqlserver"], cfg.DefaultConnections);
        Assert.Empty(cfg.TargetVersions);
        Assert.Equal(CaseSensitivity.Insensitive, d.StringSemantics.Case);
        Assert.Equal(AccentSensitivity.Sensitive, d.StringSemantics.Accent);
        Assert.Equal(TrailingSpace.Ignored, d.StringSemantics.TrailingSpace);
        Assert.Equal("NOCASE", d.StringSemantics.Collations["default"]["duckdb"]);
        Assert.Equal("dbdatabuild", d.TrackingSchemaName);
        Assert.Equal(Severity.Warning, d.Policy[PolicyKeys.Approximated]);
        Assert.Equal(Severity.Note, d.Policy[PolicyKeys.Emulated]);
    }

    [Fact]
    public void Empty_file_means_all_defaults_and_is_not_an_error()
    {
        var diags = new List<Diagnostic>();
        Assert.Equal(ProjectConfig.Default, ProjectConfigLoader.Load("", "dbdatabuild.yml", diags));
        Assert.Empty(diags);
    }

    [Fact]
    public void Problems_are_reported_together_with_positions()
    {
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load("defaults: {connections: [oracle]}\ntracking: { schema: a-b }\nsurprise: 1\n", "dbdatabuild.yml", diags);
        Assert.Null(cfg);
        Assert.Equal(["DDB-104", "DDB-106"], diags.Select(d => d.Code).Distinct().Order());
        Assert.Equal(3, diags.Count);
        Assert.All(diags, d => Assert.True(d.Location.Line >= 1));
    }

    [Fact]
    public void Missing_file_yields_defaults_and_one_warning()
    {
        var dir = TestSupport.NewProjectDir();
        var diags = new List<Diagnostic>();
        Assert.Equal(ProjectConfig.Default, ProjectConfigLoader.LoadFromProject(dir, diags));
        var d = Assert.Single(diags);
        Assert.Equal("DDB-109", d.Code);
        Assert.Equal(Severity.Warning, d.Severity);
    }

    [Fact]
    public void Broken_file_reports_errors_and_falls_back_to_defaults()
    {
        var dir = TestSupport.NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [oracle]}\n");
        var diags = new List<Diagnostic>();
        Assert.Equal(ProjectConfig.Default, ProjectConfigLoader.LoadFromProject(dir, diags));
        Assert.Contains(diags, d => d.Code == "DDB-106" && d.Location.File == "dbdatabuild.yml");
    }

    [Fact]
    public void Malformed_config_never_throws()
    {
        string[] inputs = [":", "[", "- a", "a: &x 1\nb: *x", "defaults: {connections: {a: b}}", "connections: 5", "connections: [a]", "string_semantics: x",
            "string_semantics: {collations: [a]}", "policy: [a]", "policy: {severity: 3}", "---\n---\n", "tracking: [a]"];
        foreach (var input in inputs)
            Assert.Null(Record.Exception(() => ProjectConfigLoader.Load(input, "dbdatabuild.yml", [])));
    }

    [Fact]
    public void Describe_prints_the_effective_settings()
    {
        var text = ProjectConfigLoader.Load(Full, "dbdatabuild.yml", [])!.Describe();
        Assert.Contains("default connections: sqlserver, postgres", text);
        Assert.Contains("case=sensitive, accent=insensitive, trailing_space=significant", text);
        Assert.Contains("postgres 17, sqlserver 16", text);
        Assert.Contains("target versions: not set", ProjectConfig.Default.Describe());
    }
}
