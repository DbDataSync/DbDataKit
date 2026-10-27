using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The calendar reads (the order and days of each period, the pairs of a hierarchy) for a catalog whose only date table is the Analysis Services model's (plan 19, J23):
/// the SQL Server source's date bindings are taken away, so the engine has to ask the SSAS source, in DAX. The same fixtures give the same rows as with the SQL date table.
/// </summary>
public class CalendarFromDaxTests(EngineFixture engines) : IClassFixture<EngineFixture>
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { EngineFixture.DuckDb, EngineFixture.Ssas })
            foreach (var f in new[]
            {
                "level4/41_prior_period_over_the_ssas_budget.json", "level4/122_prior_period_delta_by_month_on_ssas.json", "level4/123_window_year_to_date_on_ssas.json",
                "level4/128_ssas_between_days.json", "level4/129_ssas_period_to_date.json", "level4/130_ssas_last_complete_months.json",
            })
                data.Add(engine, f);
        return data;
    }

    /// <summary>The sample catalog with no date column bound to the SQL Server source.</summary>
    private static Catalog FinanceDatesOnly()
    {
        // loaded again: Clone() shares the attribute definitions, and taking bindings away from them would change every other test
        var cat = Catalog.Load(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml"));
        // the SQL date table's columns, for each fact table that reaches it (a binding is kept per source and per fact: "sqlserver_sales", "sqlserver_sales.fact_returns")
        foreach (var attribute in cat.Doc.Attributes.Values)
            foreach (var key in attribute.Bindings.Where(b => b.Key.StartsWith("sqlserver_sales", StringComparison.Ordinal) && b.Value.Join?.StartsWith("dim_date", StringComparison.Ordinal) == true).Select(b => b.Key).ToList())
                attribute.Bindings.Remove(key);
        return cat;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFixtureGivesTheSameRowsWhenOnlyTheModelHasADateTable(string engine, string relative)
    {
        var database = engines.Database(engine);
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        ISourceExecutor executor = engine == EngineFixture.Ssas ? new DbDataKit.Semantic.Ssas.SsasExecutor(database, standIn) : standIn;
        ConformanceTests.RunFixture(relative, "native", executor, catalog: FinanceDatesOnly);
    }

    [Fact]
    public void TheCalendarIsReadFromTheModelInDax()
    {
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        var recorder = new Recorder(standIn);
        ConformanceTests.RunFixture("level4/130_ssas_last_complete_months.json", "native", recorder, catalog: FinanceDatesOnly);
        Assert.True(recorder.Queries.Any(q => q.Native.Dialect == "dax" && q.Native.Text.Contains("SUMMARIZECOLUMNS ( 'Date'[Fiscal Month], \"lo\", MIN ( 'Date'[Date] ), \"hi\", MAX ( 'Date'[Date] ) )", StringComparison.Ordinal)), string.Join("\n----\n", recorder.Queries.Select(q => q.Native.Dialect + ": " + q.Native.Text)));
        Assert.DoesNotContain(recorder.Queries, q => q.Native.Dialect == "tsql");
    }

    [Fact]
    public void WithASqlDateTableTheCalendarStillComesFromIt()
    {
        using var standIn = new DuckDbStandInExecutor(ConformanceTests.Seed);
        var recorder = new Recorder(standIn);
        ConformanceTests.RunFixture("level4/130_ssas_last_complete_months.json", "native", recorder);
        Assert.Contains(recorder.Queries, q => q.Native.Dialect == "tsql" && q.Native.Text.Contains("MIN(", StringComparison.Ordinal));
        Assert.DoesNotContain(recorder.Queries, q => q.Native.Text.Contains("MIN (", StringComparison.Ordinal));
    }

    private sealed class Recorder(ISourceExecutor inner) : ISourceExecutor
    {
        public List<CompiledQuery> Queries { get; } = [];
        public bool RunsNativeText => inner.RunsNativeText;
        public QueryRows Execute(CompiledQuery query, int maxRows) { Queries.Add(query); return inner.Execute(query, maxRows); }
        public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) => inner.ExecuteNativeText(source, native, maxRows);
    }
}
