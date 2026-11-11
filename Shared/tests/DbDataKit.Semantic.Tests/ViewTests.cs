using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic.Presentation;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The presentation fixtures: a hand-written result and descriptor, a view document, and what it must render to, or the errors
/// it must give. The results are written by hand so they hold things the engine can't yet produce (totals on both axes) and so a
/// renderer that adds cells instead of reading the result's totals gives the wrong number.
/// </summary>
public class ViewTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    private static readonly ViewRenderer Renderer = new();

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Path.Combine(Root, "view-fixtures"), "*.json").Order().Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void View(string file)
    {
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "view-fixtures", file)))!.AsObject();
        var expect = fx["expect"]!.AsObject();
        var rendered = Renderer.Render(fx["view"]!.AsObject(), fx["result"]!.AsObject());
        var failures = new List<string>();
        failures.AddRange(FixtureChecker.Subset(expect["status"], rendered["status"], "status"));

        var codes = new JsonArray((rendered["errors"] as JsonArray ?? []).Select(e => (JsonNode?)e!["code"]!.DeepClone()).ToArray());
        if (expect["errors"] is JsonArray errs) failures.AddRange(FixtureChecker.Subset(errs, codes, "errors"));
        else if (expect["errors_include"] is JsonArray some)
        {
            foreach (var code in some)
                if (!codes.Any(c => c!.GetValue<string>() == code!.GetValue<string>())) failures.Add($"errors: expected a {code} error, got {codes.ToJsonString()}");
        }
        else if (codes.Count > 0) failures.Add($"unexpected errors: {rendered["errors"]}");
        foreach (var (index, wanted) in (expect["suggestions_include"] as JsonObject) ?? [])
            if (!(rendered["errors"]![int.Parse(index)]!["suggestions"] as JsonArray ?? []).Any(s => s!.GetValue<string>() == wanted!.GetValue<string>()))
                failures.Add($"errors[{index}]: expected a suggestion of {wanted}");

        if (rendered["views"] is JsonArray views && expect["views"] is JsonArray wantedViews)
        {
            // A grid row's cells are compared by what they read as.
            foreach (var view in views.OfType<JsonObject>().Where(v => v["type"]!.GetValue<string>() == "matrix"))
                foreach (var row in view["rows"]!.AsArray())
                    row!["texts"] = new JsonArray(row["cells"]!.AsArray().Select(c => (JsonNode?)c!["text"]!.DeepClone()).ToArray());
            failures.AddRange(FixtureChecker.Subset(wantedViews, views, "views"));
        }
        if (expect["chart_rows"] is { } n)
        {
            var chart = (rendered["views"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(v => v["type"]!.GetValue<string>() == "chart");
            var data = chart?["vega_lite"]?["data"]?["values"]?.AsArray();
            if (data is null || data.Count != n.GetValue<int>()) failures.Add($"chart data: expected {n} rows, got {data?.Count}");
            else if (data.Any(r => r!.AsObject().ContainsKey("_rollup_level"))) failures.Add("chart data holds the rollup level column");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void AMatrixRendersAResultTheEngineProduced()
    {
        // The engine's own subtotals: region by product with the product rolled up, which is each region's total column.
        var cat = DbDataKit.Semantic.Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new DbDataKit.Semantic.QisEngine(cat, executor, new DbDataKit.Semantic.Validator(cat), 3);
        var request = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "level3", "01_totals_single_source.json")))!["request"]!.AsObject();
        request["query"]!["totals"] = new JsonObject { ["subtotal_by"] = new JsonArray("products.product"), ["grand_total"] = false };
        var result = System.Text.Json.JsonSerializer.SerializeToNode(engine.Run(request), QisJson.Options)!.AsObject();

        JsonObject View(bool totals) => new()
        {
            ["spec_version"] = "0.3", ["kind"] = "view", ["result"] = new JsonObject { ["ref"] = "result.engine" },
            ["views"] = new JsonArray(new JsonObject
            {
                ["type"] = "matrix", ["id"] = "m1", ["rows"] = new JsonArray("customers.region"), ["columns"] = new JsonArray("products.product"),
                ["values"] = new JsonArray(new JsonObject { ["field"] = "orders.net_revenue" }), ["totals"] = new JsonObject { ["columns"] = totals },
            }),
        };
        var rendered = Renderer.Render(View(true), result);
        Assert.Equal("complete", rendered["status"]!.GetValue<string>());
        var matrix = rendered["views"]![0]!;
        Assert.Equal(["A", "B", "Total"], matrix["columns"]!.AsArray().Select(c => c!["labels"]![0]!.GetValue<string>()).ToArray());
        var texts = matrix["rows"]!.AsArray().ToDictionary(r => r!["labels"]![0]!.GetValue<string>(), r => r!["cells"]!.AsArray().Select(c => c!["text"]!.GetValue<string>()).ToArray());
        Assert.Equal(["300", "200", "500"], texts["West"]);
        Assert.Equal(["100", "300", "400"], texts["East"]);

        // Without the subtotals in the result there is no total to show.
        var plain = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "level3", "01_totals_single_source.json")))!["request"]!.AsObject();
        plain["query"]!.AsObject().Remove("totals");
        var detailOnly = System.Text.Json.JsonSerializer.SerializeToNode(engine.Run(plain), QisJson.Options)!.AsObject();
        Assert.Equal("totals_missing", Renderer.Render(View(true), detailOnly)["errors"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void AMatrixWithATotalRowAndAColumnRendersAnEngineCube()
    {
        var cat = DbDataKit.Semantic.Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new DbDataKit.Semantic.QisEngine(cat, executor, new DbDataKit.Semantic.Validator(cat), 3);
        var request = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "level3", "34_cube_totals.json")))!["request"]!.AsObject();
        var result = System.Text.Json.JsonSerializer.SerializeToNode(engine.Run(request), QisJson.Options)!.AsObject();

        var view = JsonNode.Parse("""
            {"spec_version": "0.3", "kind": "view", "result": {"ref": "result.engine"},
             "views": [{"type": "matrix", "id": "m1", "rows": ["customers.region"], "columns": ["products.product"],
                        "values": [{"field": "orders.net_revenue"}, {"field": "orders.order_count", "label": "Orders"}],
                        "totals": {"rows": true, "columns": true}}]}
            """)!.AsObject();
        var rendered = Renderer.Render(view, result);
        Assert.Equal("complete", rendered["status"]!.GetValue<string>());
        var rows = rendered["views"]![0]!["rows"]!.AsArray().ToDictionary(r => string.Join("/", r!["labels"]!.AsArray().Select(l => l!.GetValue<string>())),
            r => r!["cells"]!.AsArray().Select(c => c!["text"]!.GetValue<string>()).ToArray());
        // Columns: A (revenue, orders), B (revenue, orders), Total (revenue, orders). Every number is one the result holds.
        Assert.Equal(["300", "3", "200", "3", "500", "6"], rows["West"]);
        Assert.Equal(["100", "1", "300", "3", "400", "4"], rows["East"]);
        Assert.Equal(["400", "4", "500", "6", "900", "10"], rows["Total"]);
    }
}
