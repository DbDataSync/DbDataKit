using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Extraction;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// The extraction fixtures: a workbook (written as cells, merges and dates, built here with ClosedXML), an extraction spec, and what it
/// must give: tables with rows and descriptors, or the errors that stop it. Drift and mistakes must fail, never extract something wrong.
/// </summary>
public class ExtractionTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    private static readonly Extractor Extractor = new();

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Path.Combine(Root, "extraction-fixtures"), "*.json").Order().Select(f => new object[] { Path.GetFileName(f) });

    /// <summary>A workbook from its description: cells by A1 reference (text, number or boolean), the cells that are dates, and merged ranges.</summary>
    internal static MemoryStream Workbook(JsonObject description)
    {
        using var book = new XLWorkbook();
        foreach (var s in description["sheets"]!.AsArray())
        {
            var sheet = book.AddWorksheet(s!["name"]!.GetValue<string>());
            var dates = (s["dates"]?.AsArray().Select(d => d!.GetValue<string>()) ?? []).ToHashSet();
            foreach (var (reference, value) in s["cells"]!.AsObject())
            {
                var cell = sheet.Cell(reference);
                switch (value!.GetValueKind())
                {
                    case System.Text.Json.JsonValueKind.String when dates.Contains(reference):
                        cell.Value = DateTime.Parse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
                        cell.Style.DateFormat.Format = "yyyy-mm-dd";
                        break;
                    case System.Text.Json.JsonValueKind.String: cell.Value = value.GetValue<string>(); break;
                    case System.Text.Json.JsonValueKind.Number: cell.Value = value.GetValue<double>(); break;
                    case System.Text.Json.JsonValueKind.True: cell.Value = true; break;
                    case System.Text.Json.JsonValueKind.False: cell.Value = false; break;
                }
            }
            foreach (var merge in s["merges"]?.AsArray() ?? []) sheet.Range(merge!.GetValue<string>()).Merge();
        }
        var stream = new MemoryStream();
        book.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Extraction(string file)
    {
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "extraction-fixtures", file)))!.AsObject();
        var expect = fx["expect"]!.AsObject();
        var context = new ExtractionContext(FileName: "Forecast_West_FY26.xlsx", PreviousRowCount: fx["context"]?["previous_row_count"]?.GetValue<int>());
        using var workbook = Workbook(fx["workbook"]!.AsObject());
        var result = Extractor.Extract(fx["spec"]!.AsObject(), workbook, context);

        var failures = new List<string>();
        failures.AddRange(FixtureChecker.Subset(expect["status"], result["status"], "status"));
        var errors = result["errors"] as JsonArray ?? [];
        var codes = new JsonArray(errors.Select(e => (JsonNode?)e!["code"]!.DeepClone()).ToArray());
        if (expect["errors"] is JsonArray exact) failures.AddRange(FixtureChecker.Subset(exact, codes, "errors"));
        else if (expect["errors_include"] is JsonArray some)
        {
            foreach (var code in some)
                if (!codes.Any(c => c!.GetValue<string>() == code!.GetValue<string>())) failures.Add($"errors: expected {code}, got {codes.ToJsonString()}");
        }
        else if (errors.Count > 0) failures.Add($"unexpected errors: {errors.ToJsonString()}");
        if (expect["message_includes"] is { } text && !errors.Any(e => e!["message"]!.GetValue<string>().Contains(text.GetValue<string>())))
            failures.Add($"no error message mentions '{text}': {errors.ToJsonString()}");

        if (expect["tables"] is JsonArray tables)
        {
            failures.AddRange(FixtureChecker.Subset(tables, result["tables"], "tables"));
            // Every table is a valid table document, so it can be registered as it is.
            foreach (var t in result["tables"] as JsonArray ?? [])
                failures.AddRange(new Validator(Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml")))
                    .SchemaErrors(t!, "table", "extraction").Select(e => $"table document: {e.Path} {e.Message}"));
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ASavedSpecMatchesFilesByLibraryAndNamePattern()
    {
        var spec = JsonNode.Parse("""{"match": {"library": "Finance/Forecasts", "name_pattern": "Forecast_*_FY*.xlsx"}}""")!.AsObject();
        Assert.True(Extractor.Matches(spec, "Finance/Forecasts", "Forecast_West_FY26.xlsx"));
        Assert.True(Extractor.Matches(spec, "finance/forecasts/", "forecast_east_fy27.xlsx"));
        Assert.False(Extractor.Matches(spec, "Finance/Forecasts", "Budget_West_FY26.xlsx"));
        Assert.False(Extractor.Matches(spec, "Sales/Forecasts", "Forecast_West_FY26.xlsx"));
    }

    [Fact]
    public void InspectMapsNonEmptyCellsMergesAndSamplesBigSheets()
    {
        var small = JsonNode.Parse("""{"sheets": [{"name": "S", "cells": {"B2": "Region", "C2": 4.5, "B3": "West"}, "merges": ["C1:D1"]}]}""")!.AsObject();
        using var workbook = Workbook(small);
        var map = Extractor.Inspect(workbook)["sheets"]![0]!;
        Assert.Equal(["B2", "C2", "B3"], map["cells"]!.AsArray().Select(c => c!["ref"]!.GetValue<string>()).ToArray());
        Assert.Equal(["C1:D1"], map["merged"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray());
        Assert.False(map["sampled"]!.GetValue<bool>());

        // 200 rows: the top 40, the bottom 10 and the rows a merged region touches (row 100 here).
        var cells = new JsonObject();
        for (int r = 1; r <= 200; r++) cells[$"A{r}"] = $"row {r}";
        var big = new JsonObject { ["sheets"] = new JsonArray(new JsonObject { ["name"] = "Big", ["cells"] = cells, ["merges"] = new JsonArray("A100:B100") }) };
        using var bigBook = Workbook(big);
        var rows = Extractor.Inspect(bigBook)["sheets"]![0]!["cells"]!.AsArray().Select(c => int.Parse(c!["ref"]!.GetValue<string>()[1..])).ToList();
        Assert.Equal(Enumerable.Range(1, 40).Append(100).Concat(Enumerable.Range(191, 10)), rows);
    }

    [Fact]
    public void AnExtractedTableRegistersAsAnAdHocTable()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "extraction-fixtures", "01_report_layout.json")))!.AsObject();
        using var workbook = Workbook(fx["workbook"]!.AsObject());
        var result = Extractor.Extract(fx["spec"]!.AsObject(), workbook, new ExtractionContext(FileName: "f.xlsx"));
        var table = result["tables"]![0]!.AsObject();

        var cat = Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new QisEngine(cat, executor, new Validator(cat), 4);
        var registered = engine.Run(table.DeepClone().AsObject());
        Assert.Equal("complete", registered.Status);
        Assert.Equal("adhoc.forecast@v1", registered.ResultSets![0].Id);
        Assert.Equal(12, registered.ResultSets[0].Descriptor["x_row_count"]!.GetValue<int>());
    }
}
