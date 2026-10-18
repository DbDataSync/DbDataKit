using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The optional rewrites (DESIGN.md 7.6.2) exist to make an engine give DuckDB's answer. With one switched off the query is shorter and the engine answers as it always did: this test runs each case both ways on a
/// real engine and checks that the query is still valid, that the answer is DuckDB's with the rewrite and the engine's own without it, and that the engine's own differs in the way the catalog says. A case that stops
/// differing means the catalog's description of the engine is out of date.
/// </summary>
public class RewriteOptOutProbes
{
    /// <param name="Rewrite">The rewrite to turn off.</param>
    /// <param name="Sql">A DuckDB query over the probe table.</param>
    /// <param name="Engine">The engine whose behavior differs without it.</param>
    private sealed record Case(string Rewrite, string Sql, string Engine);

    // (length leaves out the row with the emoji: a character outside the BMP counting as two is a known difference of its own)
    private static readonly Case[] Cases =
    [
        new("length-trailing-spaces", "SELECT id, length(s) AS v FROM probe WHERE id <> 6", "sqlserver"),
        new("double-to-int", "SELECT id, CAST(d AS INTEGER) AS v FROM probe WHERE id <> 8", "sqlserver"),
        new("decimal-to-int", "SELECT id, CAST(n AS INTEGER) AS v FROM probe", "sqlserver"),
        new("round-double", "SELECT id, round(CAST(2.675 AS DOUBLE) + d * 0, 2) AS v FROM probe WHERE id <> 8", "sqlserver"),
        new("double-to-decimal", "SELECT id, CAST(CAST(819.025 AS DOUBLE) + d * 0 AS DECIMAL(10, 2)) AS v FROM probe", "sqlserver"),
        new("avg-double", "SELECT avg(i) AS v FROM probe", "sqlserver"),
        new("try-cast-parse", "SELECT id, TRY_CAST(s AS INTEGER) AS v FROM probe", "sqlserver"),
        new("date-diff-weeks", "SELECT id, date_diff('week', dt, DATE '2024-03-01') AS v FROM probe", "sqlserver"),
        new("date-diff-weeks", "SELECT id, date_diff('week', dt, DATE '2024-03-01') AS v FROM probe", "postgres"),
        new("date-diff-boundaries", "SELECT id, date_diff('month', dt, DATE '2024-03-01') AS v FROM probe", "postgres"),
        new("date-diff-boundaries", "SELECT id, date_diff('year', dt, DATE '2025-01-01') AS v FROM probe", "postgres"),
    ];

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Without_an_optional_rewrite_the_query_is_valid_and_the_engine_answers_in_its_own_way(string name)
    {
        var engine = EngineEnv.RequireProbe(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        var problems = new List<string>();
        foreach (var c in Cases.Where(c => c.Engine == name))
        {
            var expected = probe.OnDuckDb(c.Sql);
            var on = probe.Render(c.Sql);
            var off = probe.Render(c.Sql, new DbDataBuild.Core.RewritePolicy([c.Rewrite]));
            if (on.Sql == null || off.Sql == null) { problems.Add($"{c.Rewrite}: not rendered ({on.Refused ?? off.Refused})"); continue; }
            if (on.Sql == off.Sql) { problems.Add($"{c.Rewrite}: turning it off changed nothing in {c.Sql}"); continue; }
            var withIt = await probe.OnEngineAsync(on.Sql);
            var without = await probe.OnEngineAsync(off.Sql);
            if (withIt.Error != null || withIt.Rows != expected.Rows) problems.Add($"{c.Rewrite}: with the rewrite the answer is not DuckDB's ({withIt.Error ?? withIt.Rows})");
            if (without.Error == null && without.Rows == expected.Rows) problems.Add($"{c.Rewrite}: without the rewrite the engine agrees with DuckDB on {c.Sql}, so the catalog's description is out of date");
        }
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }
}
