using System.Text;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The native text the engine plans, recorded for every conformance fixture (plan 19, J04): per fixture, each source query's native text
/// (T-SQL or DAX), its parameters and its DuckDB twin, in the order they run. Moving the emitters onto the shared dialects must not change
/// a character of it; a deliberate change is made by re-recording (<c>UPDATE_GOLDEN=1</c>) and reading the diff. One file per adoption level in <c>golden/emitter</c>.
/// </summary>
public class EmitterGoldenTests
{
    public static TheoryData<string> Levels() => ["level0", "level1", "level2", "level3", "level4", "level5"];

    [Theory]
    [MemberData(nameof(Levels))]
    public void NativeTextIsUnchanged(string level)
    {
        var fixtures = Directory.GetFiles(Path.Combine(ConformanceTests.Root, "fixtures", level), "*.json").Order().ToList();
        var text = new StringBuilder();
        foreach (var file in fixtures)
        {
            var relative = Path.GetRelativePath(Path.Combine(ConformanceTests.Root, "fixtures"), file).Replace('\\', '/');
            using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
            var recorder = new Recorder(standIn);
            ConformanceTests.RunFixture(relative, "native", recorder);
            text.Append("=== ").Append(relative).Append('\n');
            foreach (var q in recorder.Queries)
            {
                text.Append("--- ").Append(q.Source).Append(' ').Append(q.Native.Dialect).Append('\n').Append(q.Native.Text).Append('\n');
                if (q.ExecText is not null && q.ExecText != q.Native.Text) text.Append("--- exec\n").Append(q.ExecText).Append('\n');
                text.Append("--- parameters ").Append(string.Join(", ", q.DuckDbParameters.Select(p => p is null ? "null" : $"{p.GetType().Name}:{Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture)}"))).Append('\n');
                text.Append("--- duckdb\n").Append(q.DuckDbSql).Append('\n');
            }
        }
        Golden.Assert($"emitter/{level}.txt", text.ToString());
    }

    /// <summary>
    /// The same for PostgreSQL (plan 19, J05): the text the engine emits for a PostgreSQL source and the values of its <c>$n</c> markers, one file per level in
    /// <c>golden/emitter-postgres</c>. It is the text that runs (the row limit one above the request's, so truncation shows).
    /// </summary>
    [Theory]
    [MemberData(nameof(Levels))]
    public void PostgresTextIsRecorded(string level)
    {
        var fixtures = Directory.GetFiles(Path.Combine(ConformanceTests.Root, "fixtures", level), "*.json").Order().ToList();
        var text = new StringBuilder();
        foreach (var file in fixtures)
        {
            var relative = Path.GetRelativePath(Path.Combine(ConformanceTests.Root, "fixtures"), file).Replace('\\', '/');
            using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
            var recorder = new Recorder(standIn);
            ConformanceTests.RunFixture(relative, "native", recorder);
            text.Append("=== ").Append(relative).Append('\n');
            foreach (var q in recorder.Queries.Where(q => q.Postgres is not null))
            {
                text.Append("--- ").Append(q.Source).Append('\n').Append(q.Postgres!.Text).Append('\n');
                text.Append("--- parameters ").Append(string.Join(", ", q.Postgres.Parameters.Select(p => p is null ? "null" : $"{p.GetType().Name}:{Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture)}"))).Append('\n');
            }
        }
        Golden.Assert($"emitter-postgres/{level}.txt", text.ToString());
    }

    /// <summary>A source declared as PostgreSQL's gets PostgreSQL's text as its native text, and nothing in it is T-SQL or the DuckDB twin's.</summary>
    [Fact]
    public void PostgresSourceNativeTextIsPostgres()
    {
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        var recorder = new Recorder(standIn);
        ConformanceTests.RunFixture("level1/62_quarter_buckets.json", "native", recorder, catalog: EngineConformanceTests.PostgresCatalog);
        var queries = recorder.Queries.Where(q => q.Native.Dialect == "postgres").ToList();
        Assert.NotEmpty(queries);
        Assert.All(queries, query =>
        {
            Assert.DoesNotContain("strftime", query.Native.Text);
            Assert.DoesNotContain("OFFSET 0 ROWS", query.Native.Text);
            Assert.Equal(query.Postgres!.Text, query.ExecText);
        });
        Assert.Contains(queries, query => query.Native.Text.Contains("to_char("));
    }

    private sealed class Recorder(ISourceExecutor inner) : ISourceExecutor
    {
        public List<CompiledQuery> Queries { get; } = [];
        public bool RunsNativeText => inner.RunsNativeText;
        public QueryRows Execute(CompiledQuery query, int maxRows) { Queries.Add(query); return inner.Execute(query, maxRows); }
        public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) => inner.ExecuteNativeText(source, native, maxRows);
    }
}

/// <summary>Golden-file comparison. Files live in <c>golden/</c> beside the tests and are copied to the output; a missing file fails; <c>UPDATE_GOLDEN=1</c> writes the actual text to the source folder (then review the diff).</summary>
internal static class Golden
{
    public static void Assert(string name, string actual)
    {
        var normalized = actual.Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = new DirectoryInfo(AppContext.BaseDirectory);
            while (source is not null && !source.GetFiles("DbDataKit.Semantic.Tests.csproj").Any()) source = source.Parent;
            var path = Path.Combine(source!.FullName, "golden", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalized);
            return;
        }
        var golden = Path.Combine(AppContext.BaseDirectory, "golden", name);
        Xunit.Assert.True(File.Exists(golden), $"Golden file missing: {name}. Run with UPDATE_GOLDEN=1, then review it.");
        Xunit.Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n"), normalized);
    }
}
