using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;
using DbDataBuild.Sql.Ast;
using DbDataBuild.Sql.Matrix;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;

namespace DbDataBuild.Tests.Unit;

public class MatrixTests
{
    private static readonly SupportMatrix Matrix = Load();

    private static SupportMatrix Load()
    {
        var diags = new List<Diagnostic>();
        var m = MatrixLoader.LoadEmbedded(diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        return m;
    }

    private static HashSet<string> SpikeCaseIds()
    {
        var d = new List<Diagnostic>();
        var seq = (YamlSequence)StrictYamlReader.Read(File.ReadAllText(Path.Combine(RepoRoot(), "spike", "constructs.yml")), "constructs.yml", d)!;
        Assert.Empty(d);
        return seq.Items.Cast<YamlMapping>().Select(m => ((YamlScalar)m.Get("id")!).Value).ToHashSet();
    }

    [Fact]
    public void Embedded_matrix_matches_the_files_on_disk()
    {
        var diags = new List<Diagnostic>();
        var disk = MatrixLoader.LoadFromDirectory(Path.Combine(RepoRoot(), "matrix"), diags);
        Assert.Empty(diags);
        Assert.Equal(disk.Rows.Select(r => r.Id), Matrix.Rows.Select(r => r.Id));
        Assert.Equal(disk.Covered.Count, Matrix.Covered.Count);
    }

    [Fact]
    public void Every_row_has_every_target_and_a_detector()
    {
        Assert.NotEmpty(Matrix.Rows);
        foreach (var row in Matrix.Rows)
        {
            Assert.Equal(SupportMatrix.Targets.Order(), row.Targets.Keys.Order());
            Assert.NotEmpty(row.Detect);
        }
    }

    [Fact]
    public void Test_references_point_at_real_spike_cases_until_the_conformance_suite_exists()
    {
        var cases = SpikeCaseIds();
        foreach (var row in Matrix.Rows)
            foreach (var (target, entry) in row.Targets)
            {
                if (entry.Test == null) continue;
                Assert.StartsWith("spike/", entry.Test);
                Assert.True(cases.Contains(entry.Test["spike/".Length..]), $"{row.Id}/{target}: test `{entry.Test}` is not in spike/constructs.yml");
            }
    }

    [Fact]
    public void Coverage_evidence_points_at_real_spike_cases()
    {
        var cases = SpikeCaseIds();
        foreach (var c in Matrix.Covered)
            foreach (var e in c.Evidence)
                Assert.True(cases.Contains(e), $"{c.Kind} {c.Name}: evidence `{e}` is not in spike/constructs.yml");
    }

    [Fact]
    public void Every_detector_is_used_by_a_row()
    {
        var used = Matrix.Rows.SelectMany(r => r.Detect).Where(d => d.Kind == DetectKind.Detector).Select(d => d.Name).ToHashSet();
        Assert.Equal(Detectors.All.Keys.Order(), used.Order());
    }

    [Fact]
    public void Statuses_that_make_a_claim_name_a_test()
    {
        foreach (var row in Matrix.Rows)
            foreach (var (target, e) in row.Targets)
                if (e.Status is not (SupportStatus.Native or SupportStatus.Unverified))
                    Assert.False(string.IsNullOrEmpty(e.Test), $"{row.Id}/{target}");
    }

    [Fact]
    public void Expression_tag_list_matches_the_pinned_commit_and_walker_knows_it()
    {
        var header = File.ReadLines(Path.Combine(RepoRoot(), "src", "DbDataBuild.Sql", "Ast", "expression-types.txt")).Skip(1).First();
        Assert.Equal("# pin: " + DbDataBuild.Sql.Polyglot.PinnedCommit, header);
        Assert.Contains("like", AstNode.KnownTypes);
        Assert.Contains("i_like", AstNode.KnownTypes);
        Assert.True(AstNode.KnownTypes.Count > 900);
    }

    // ---- loader negative tests ----

    private static List<Diagnostic> LoadBad(string constructs, string covered = "- { node: column, evidence: [x] }\n")
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "constructs.yml"), constructs);
        File.WriteAllText(Path.Combine(dir, "covered.yml"), covered);
        File.WriteAllText(Path.Combine(dir, "strategies.yml"), "[]\n");
        var diags = new List<Diagnostic>();
        MatrixLoader.LoadFromDirectory(dir, diags);
        return diags;
    }

    private const string GoodRow = """
        - id: x.y
          detect: node:div
          duckdb: native
          tsql: { any: { status: native } }
          postgres: { status: native }
        """;

    [Fact]
    public void A_good_row_loads_clean() => Assert.Empty(LoadBad(GoodRow));

    [Theory]
    [InlineData("status: bogus", "DDB-106")]
    [InlineData("status: translated", "DDB-105")]                       // needs a test
    [InlineData("status: native, color: red", "DDB-104")]
    [InlineData("status: native, min_version: abc", "DDB-106")]
    public void Bad_entries_are_diagnosed(string entry, string code)
    {
        var yaml = GoodRow.Replace("postgres: { status: native }", $"postgres: {{ {entry} }}");
        Assert.Contains(LoadBad(yaml), d => d.Code == code);
    }

    [Fact]
    public void Missing_target_unknown_detector_bad_tag_and_duplicate_ids_are_diagnosed()
    {
        Assert.Contains(LoadBad(GoodRow.Replace("postgres: { status: native }", "")), d => d.Code == "DDB-105" && d.Found.Contains("postgres"));
        Assert.Contains(LoadBad(GoodRow.Replace("node:div", "detector:nope")), d => d.Code == "DDB-106");
        Assert.Contains(LoadBad(GoodRow.Replace("node:div", "node:not_a_tag")), d => d.Code == "DDB-106");
        Assert.Contains(LoadBad(GoodRow.Replace("node:div", "bogus")), d => d.Code == "DDB-106");
        Assert.Contains(LoadBad(GoodRow + "\n" + GoodRow), d => d.Code == "DDB-102");
        Assert.Contains(LoadBad(GoodRow.Replace("duckdb: native", "duckdb: translated")), d => d.Code == "DDB-106");
    }

    [Fact]
    public void Tsql_shorthand_is_overridden_by_a_specific_target()
    {
        var diags = new List<Diagnostic>();
        var dir = Path.Combine(Path.GetTempPath(), "ddb-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "constructs.yml"), GoodRow.Replace("duckdb: native", "duckdb: native\n  fabric: { status: unverified }"));
        File.WriteAllText(Path.Combine(dir, "covered.yml"), "- { node: column, evidence: [x] }\n");
        File.WriteAllText(Path.Combine(dir, "strategies.yml"), "[]\n");
        var m = MatrixLoader.LoadFromDirectory(dir, diags);
        Assert.Empty(diags);
        Assert.Equal(SupportStatus.Native, m.Rows[0].Targets["sqlserver"].Status);
        Assert.Equal(SupportStatus.Unverified, m.Rows[0].Targets["fabric"].Status);
    }

    [Fact]
    public void Covered_entries_need_evidence_and_known_tags()
    {
        Assert.Contains(LoadBad(GoodRow, "- { node: column }\n"), d => d.Code == "DDB-105");
        Assert.Contains(LoadBad(GoodRow, "- { node: not_a_tag, evidence: [x] }\n"), d => d.Code == "DDB-106");
        Assert.Contains(LoadBad(GoodRow, "- { whatever: 1 }\n"), d => d.Code is "DDB-104" or "DDB-105");
    }

    // ---- strategy rows ----

    [Fact]
    public void Strategy_rows_cover_exactly_the_closed_library_with_every_target()
    {
        Assert.Equal(DbDataBuild.Models.LoadStrategies.All.Select(x => "strategy." + x).Order(), Matrix.Strategies.Select(r => r.Id).Order());
        foreach (var row in Matrix.Strategies)
        {
            Assert.Equal(SupportMatrix.Targets.Order(), row.Targets.Keys.Order());
            Assert.Empty(row.Detect);
        }
    }

    [Fact]
    public void Strategy_test_references_name_conformance_cases_that_exist()
    {
        var dir = Path.Combine(RepoRoot(), "tests", "DbDataBuild.Tests.Conformance");
        Assert.True(Directory.Exists(dir), "tests/DbDataBuild.Tests.Conformance is missing");
        var source = string.Join("\n", Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var row in Matrix.Strategies)
            foreach (var (target, e) in row.Targets.Where(t => t.Value.Test != null))
            {
                Assert.StartsWith("conformance/", e.Test);
                var id = e.Test!["conformance/".Length..];
                Assert.Contains($"\"{id}\"", source);
                Assert.Equal("strategy." + id, row.Id);
            }
    }

    [Fact]
    public void Fabric_strategy_entries_are_unverified_until_a_fabric_engine_runs_them()
    {
        Assert.All(Matrix.Strategies, r => Assert.Equal(SupportStatus.Unverified, r.Targets["fabric"].Status));
    }

    [Fact]
    public void Postgres_merge_needs_version_15()
    {
        Assert.Equal(15, Matrix.Strategies.Single(r => r.Id == "strategy.merge_by_key").Targets["postgres"].MinVersion);
    }
}
