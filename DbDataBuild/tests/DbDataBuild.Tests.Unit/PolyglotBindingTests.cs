using System.Text.Json;
using DbDataBuild.Sql;

namespace DbDataBuild.Tests.Unit;

public class PolyglotBindingTests
{
    public PolyglotBindingTests() =>
        Assert.True(Polyglot.IsAvailable(), "Native polyglot library not found. Run scripts/build-polyglot.sh (or set DBDATABUILD_POLYGLOT_PATH).");

    [Fact]
    public void Pinned_commit_matches_build_script()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build-polyglot.sh"));
        Assert.Contains($"PIN=\"{Polyglot.PinnedCommit}\"", script);
    }

    [Fact]
    public void Library_loads_and_reports_version_and_all_target_dialects()
    {
        Assert.False(string.IsNullOrWhiteSpace(Polyglot.Version()));
        var names = Polyglot.DialectNames();
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
            Assert.Contains(Dialects.ForTarget(target), names);
        Assert.Contains(Dialects.Canonical, names);
    }

    [Fact]
    public void Transpiles_duckdb_to_each_target()
    {
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            var (o, sql) = Polyglot.TranspileOne("SELECT a FROM t ORDER BY a LIMIT 10", Dialects.Canonical, Dialects.ForTarget(target));
            Assert.True(o.Ok, o.Error);
            Assert.False(string.IsNullOrWhiteSpace(sql));
        }
    }

    [Fact]
    public void Parse_errors_are_outcomes_not_exceptions_and_non_ascii_round_trips()
    {
        var bad = Polyglot.Parse("SELEC FROM FROM (", Dialects.Canonical);
        Assert.False(bad.Ok);
        Assert.False(string.IsNullOrEmpty(bad.Error));

        var (o, sql) = Polyglot.TranspileOne("SELECT 'héllo ☃' AS x", Dialects.Canonical, "postgres");
        Assert.True(o.Ok, o.Error);
        Assert.Contains("héllo ☃", sql);
    }

    [Fact]
    public void Validate_reports_structured_errors()
    {
        var ok = Polyglot.Validate("SELECT 1", Dialects.Canonical);
        Assert.True(ok.Valid);
        var bad = Polyglot.Validate("SELECT FROM WHERE", Dialects.Canonical);
        Assert.False(bad.Valid);
        using var doc = JsonDocument.Parse(bad.ErrorsJson);
        Assert.True(doc.RootElement.GetArrayLength() > 0);
    }

    [Fact]
    public void Repeated_calls_do_not_leak_or_crash()
    {
        for (var i = 0; i < 2000; i++)
            Assert.True(Polyglot.Transpile($"SELECT {i} AS x", Dialects.Canonical, "tsql").Ok);
    }

    [Fact]
    public void Concurrent_calls_are_safe()
    {
        Parallel.For(0, 200, i =>
        {
            var (o, sql) = Polyglot.TranspileOne($"SELECT a + {i} FROM t", Dialects.Canonical, "tsql");
            Assert.True(o.Ok);
            Assert.Contains(i.ToString(), sql);
        });
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DbDataBuild.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
