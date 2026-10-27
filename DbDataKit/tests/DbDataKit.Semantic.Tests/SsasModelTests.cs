using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The Tabular model in <c>ssas/model.tmsl.json</c> holds its data inside the model (DATATABLE calculated tables), so it needs no source database. These tests,
/// which run everywhere, keep it from drifting from what the rest of the suite believes: the same rows as the seed data, and every DAX name the catalog uses.
/// </summary>
public partial class SsasModelTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    private static readonly JsonObject Model = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "ssas", "model.tmsl.json")))!["createOrReplace"]!["database"]!["model"]!.AsObject();

    [GeneratedRegex(@"^\s*\{ (.*) \},?$")]
    private static partial Regex DataRow();

    private static JsonObject Table(string name) => Model["tables"]!.AsArray().Select(t => t!.AsObject()).Single(t => t["name"]!.GetValue<string>() == name);

    /// <summary>The rows of a DATATABLE calculated table, each as its values written as text.</summary>
    private static List<string[]> Rows(string table) =>
        Table(table)["partitions"]![0]!["source"]!["expression"]!.AsArray().Select(l => DataRow().Match(l!.GetValue<string>()))
            .Where(m => m.Success).Select(m => m.Groups[1].Value.Split(", ").Select(v => v.Trim('"')).ToArray()).ToList();

    private static List<string[]> Seed(string sql)
    {
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var compiled = new CompiledQuery("seed", new DbDataKit.Semantic.Abstractions.NativeQuery("duckdb", sql), sql, []);
        return executor.Execute(compiled, 1000).Rows.Select(r => r.Values.Select(v => v is decimal d ? d.ToString("0.####") : v!.ToString()!).ToArray()).ToList();
    }

    [Fact]
    public void TheModelHoldsTheSeedDataOfTheSsasSource()
    {
        Assert.Equal(Seed("SELECT region, fiscal_month, CAST(amount AS DOUBLE), month_start FROM fact_budget ORDER BY region DESC, fiscal_month").Select(r => string.Join("|", r)).ToList(),
            Rows("Budget").OrderByDescending(r => r[0]).ThenBy(r => r[1]).Select(r => string.Join("|", r)).ToList());
        Assert.Equal(Seed("SELECT calendar_date, fiscal_month, fiscal_quarter, fiscal_year, prior_year_fiscal_month, prior_year_fiscal_quarter, prior_year_calendar_date FROM dim_date ORDER BY calendar_date").Select(r => string.Join("|", r)).ToList(),
            Rows("Date").OrderBy(r => r[0]).Select(r => string.Join("|", r)).ToList());
        Assert.Equal(Seed("SELECT DISTINCT region FROM dim_customer ORDER BY region").Select(r => r[0]).ToList(), Rows("Customer").Select(r => r[0]).Order().ToList());
    }

    [Fact]
    public void EveryDaxNameTheCatalogUsesForTheSsasSourceIsInTheModel()
    {
        var catalog = Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        var columns = Model["tables"]!.AsArray().SelectMany(t => t!["columns"]!.AsArray().Select(c => $"'{t["name"]}'[{c!["name"]}]")).ToHashSet();
        var measures = Model["tables"]!.AsArray().SelectMany(t => t!["measures"]?.AsArray() ?? []).Select(m => $"[{m!["name"]}]").ToHashSet();
        var dax = catalog.Doc.Attributes.Values.SelectMany(d => d.Bindings.Where(b => catalog.Doc.Sources[b.Key].Kind == "ssas").Select(b => b.Value.Dax)).Where(x => x is not null).ToList();
        Assert.NotEmpty(dax);
        foreach (var column in dax) Assert.Contains(column!, columns);
        var measureDax = catalog.Doc.Measures.Values.Where(m => m.Source == "ssas_finance").Select(m => m.Dax).ToList();
        Assert.NotEmpty(measureDax);
        foreach (var m in measureDax) Assert.Contains(m!, measures);
    }

    [Fact]
    public void TheRelationshipsPointAtColumnsTheModelHas()
    {
        foreach (var r in Model["relationships"]!.AsArray())
            foreach (var (table, column) in new[] { (r!["fromTable"]!.GetValue<string>(), r["fromColumn"]!.GetValue<string>()), (r["toTable"]!.GetValue<string>(), r["toColumn"]!.GetValue<string>()) })
                Assert.Contains(Table(table)["columns"]!.AsArray(), c => c!["name"]!.GetValue<string>() == column);
    }
}
