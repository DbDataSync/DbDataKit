using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Extraction;
using DbDataKit.Semantic.Presentation;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The specs working together: a spreadsheet the user brings is extracted to a table, mapped onto the catalog, queried against two sources
/// (SQL Server sales, SSAS budget) by the user's own territories with totals, and shown as a matrix. Nothing in the chain computes a number
/// twice: the totals come from the sources, and the view only places them.
/// </summary>
public class EndToEndTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");

    [Fact]
    public void ActualsAndBudgetByAnUploadedTerritoryList()
    {
        // 1. The user's spreadsheet: territories and the region codes their company uses.
        var workbook = ExtractionTests.Workbook(JsonNode.Parse("""
            {"sheets": [{"name": "Territories", "cells": {
                "A1": "Territory", "B1": "Region Code", "A2": "Pacific", "B2": "WST-01", "A3": "Atlantic", "B3": "EST-01"}}]}
            """)!.AsObject());
        var spec = JsonNode.Parse("""
            {"spec_version": "0.3", "id": "territories",
             "tables": [{"name": "territories", "sheet": "Territories", "header": {"rows": [1]},
                         "data": {"start": "A2", "end": {"until_blank_in": "A"}, "columns": "A:B"},
                         "checks": [{"type": "row_count", "min": 1}, {"type": "unique", "columns": ["territory"]}]}]}
            """)!.AsObject();
        var extracted = new Extractor().Extract(spec, workbook, new ExtractionContext(FileName: "territories.xlsx"));
        Assert.Equal("complete", extracted["status"]!.GetValue<string>());

        // 2. Register it, map its region codes to catalog regions (a reviewed map), and bind its columns.
        var cat = Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new QisEngine(cat, executor, new Validator(cat), 3);
        JsonObject Run(string json) { var r = engine.Run(JsonNode.Parse(json)!.AsObject()); Assert.Equal("complete", r.Status); return JsonSerializer.SerializeToNode(r, QisJson.Options)!.AsObject(); }
        Assert.Equal("complete", engine.Run(extracted["tables"]![0]!.DeepClone().AsObject()).Status);
        Run("""
            {"spec_version": "0.3", "kind": "value_map", "id": "code_region", "table": "adhoc.territories@v1", "field": "region_code",
             "target": "customers.region", "mode": "catalog", "scope": "governed",
             "pairs": [{"from": "WST-01", "to": "West"}, {"from": "EST-01", "to": "East"}]}
            """);
        Run("""
            {"spec_version": "0.3", "kind": "bindings", "table": "adhoc.territories@v1", "confirmed_by": "user",
             "bindings": [{"field": "territory", "role": "attribute", "as": "territories.territory"},
                          {"field": "region_code", "role": "attribute", "binds_to": "customers.region", "via_map": "code_region"}],
             "grain": ["territories.territory", "customers.region"]}
            """);

        // 3. Actuals and budget by territory, with a total.
        var result = Run("""
            {"spec_version": "0.3", "kind": "query",
             "query": {"measures": ["orders.net_revenue", "budget.budget_amount"], "limit": 100, "attributes": ["territories.territory"],
                       "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
                       "totals": {"subtotal_by": ["territories.territory"], "grand_total": true}}}
            """);

        // 4. A matrix of it: the territory down, the two measures across, the total row from the result.
        var view = JsonNode.Parse("""
            {"spec_version": "0.3", "kind": "view", "result": {"ref": "result.e2e"},
             "views": [{"type": "matrix", "id": "m1", "rows": ["territories.territory"],
                        "values": [{"field": "orders.net_revenue", "label": "Actual"}, {"field": "budget.budget_amount", "label": "Budget"}],
                        "totals": {"rows": true}}]}
            """)!.AsObject();
        var rendered = new ViewRenderer().Render(view, result);
        Assert.Equal("complete", rendered["status"]!.GetValue<string>());
        var grid = rendered["views"]![0]!["rows"]!.AsArray().Select(r =>
            string.Join("|", r!["labels"]!.AsArray().Select(l => l!.GetValue<string>()).Concat(r["cells"]!.AsArray().Select(c => c!["text"]!.GetValue<string>())))).ToList();
        Assert.Equal(["Atlantic|400|$500", "Pacific|500|$600", "Total|900|$1,100"], grid);
    }
}
