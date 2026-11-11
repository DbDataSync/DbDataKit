using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// The data exports of the module (plan 32 DBR17): <c>export?format=csv|xlsx&amp;widget=&amp;layout=&amp;p.x=</c> resolve the layout as the caller and write the rows the caller was shown; no browser is configured here at all, because a data export needs none.
/// The CSV and xlsx are read back (a small RFC 4180 reader, ClosedXML), two callers with different access get their own rows, a formula in a label is neutralised, a failed widget is its error block, and every refusal is in words and audited.
/// The files are compared by content only: line breaks are the explicit CRLF of RFC 4180, nothing here depends on a path or the platform's newline.
/// </summary>
public sealed class ViewsDataExportTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private async Task<ViewsHarness> StartAsync(long? maxExportRows = null)
    {
        var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, maxExportRows: maxExportRows);
        h.PublishDataBoard();
        // the table about the category alone cannot be answered: the host refuses it, so the widget is an error entry and the others render
        h.Host.Before = (_, request) => request.Text.Contains("category", StringComparison.Ordinal) && !request.Text.Contains("region", StringComparison.Ordinal)
            ? throw new ServingDeniedException("Source 'warehouse' is not available.") : Task.CompletedTask;
        return h;
    }

    private static string Decode(byte[] bytes)
    {
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "a CSV starts with the UTF-8 byte-order mark");
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    /// <summary>RFC 4180: fields separated by commas, records by CRLF, a quoted field may hold commas, quotes (doubled) and line breaks.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { record.Add(field.ToString()); field.Clear(); records.Add(record); record = []; i++; }
            else field.Append(ch);
        }
        Assert.False(quoted, "a quoted field is closed");
        Assert.Equal(0, field.Length + record.Count);   // the last record ends with CRLF
        return records;
    }

    private async Task<List<List<string>>> CsvAsync(ViewsHarness h, string query, string token)
    {
        var (status, bytes, response) = await h.GetBytesAsync("/api/v1/dashboards/rows/export?" + query, token);
        Assert.True(status == 200, Encoding.UTF8.GetString(bytes));
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        return ParseCsv(Decode(bytes));
    }

    private static IReadOnlyList<AuditRecord> Audited(ViewsHarness h) => [.. h.Audit.Read(new AuditQuery(Verb: "view.export", Limit: 100)).Reverse()];

    [Fact]
    public async Task A_table_is_exported_to_csv_as_the_caller_sees_it_with_a_neutralised_label_and_audited()
    {
        await using var h = await StartAsync();

        var (status, bytes, response) = await h.GetBytesAsync("/api/v1/dashboards/rows/export?format=csv&widget=by_region", "all");
        Assert.Equal(200, status);
        Assert.Equal("rows-by_region.csv", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var all = ParseCsv(Decode(bytes));

        // the header: a label that is a formula is text with an apostrophe before it (the field has quotes and a comma, so it is quoted); a label with a comma is quoted
        Assert.Equal(["'=HYPERLINK(\"http://example.invalid\",\"Region\")", "Revenue, net"], all[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://example.invalid\"\",\"\"Region\"\")\",\"Revenue, net\"\r\n", Decode(bytes));
        Assert.All(all, r => Assert.Equal(2, r.Count));

        // the numbers are the engine's values, not the formatted text, and the regions are the caller's
        var rows = all.Skip(1).ToList();
        Assert.Equal(["East", "West"], rows.Select(r => r[0]).Order(StringComparer.Ordinal));
        Assert.Equal(1600, rows.Sum(r => double.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.DoesNotContain(rows, r => r[1].Contains(',', StringComparison.Ordinal));

        var west = await CsvAsync(h, "format=csv&widget=by_region", "west");
        Assert.Equal(["West"], west.Skip(1).Select(r => r[0]));
        Assert.Equal(900, double.Parse(west[1][1], System.Globalization.CultureInfo.InvariantCulture));

        // the rows equal what the same caller's render of the widget holds
        var render = await h.RenderAsync("west", new JsonObject { ["only"] = new JsonArray("by_region") }, name: "rows");
        var cells = render.Object["widgets"]!["by_region"]!["rows"]!.AsArray().Select(r => r!["cells"]!.AsArray().Select(c => c!["v"]!.ToString()).ToList()).ToList();
        Assert.Equal(cells.Count, west.Count - 1);
        Assert.Equal(cells[0][0], west[1][0]);

        var row = Audited(h)[0].Entry;
        Assert.Equal(("ok", "u-all", "rows@1"), (row.Outcome, row.ActorId, row.TargetId));
        var detail = JsonNode.Parse(row.Detail!)!;
        Assert.Equal(("csv", "by_region", "desk"), (detail["format"]!.GetValue<string>(), detail["widget"]!.GetValue<string>(), detail["layout"]!.GetValue<string>()));
        Assert.Equal(2, detail["rows"]!.GetValue<int>());
        Assert.Equal(bytes.Length, detail["bytes"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_matrix_and_a_chart_are_exported_with_their_headers_and_a_parameter_from_the_address_is_not_audited_as_a_value()
    {
        await using var h = await StartAsync();
        var matrix = await CsvAsync(h, "format=csv&widget=grid", "all");
        Assert.Equal("customers.region", matrix[0][0]);                     // the first row-label column is named by the rows encoding
        Assert.True(matrix[0].Count > 2, "one column for each member of the matrix's columns");
        Assert.Contains("Monitors / orders.net_revenue", matrix[0]);   // a column path: the column member, then the measure
        Assert.All(matrix, r => Assert.Equal(matrix[0].Count, r.Count));

        var chart = await CsvAsync(h, "format=csv&widget=trend", "west");
        Assert.Equal(["customers_region", "orders_net_revenue"], chart[0]);   // the chart's data fields
        Assert.Equal(["West"], chart.Skip(1).Select(r => r[0]));

        var detail = JsonNode.Parse(Audited(h)[0].Entry.Detail!)!;
        Assert.Equal("grid", detail["widget"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_dashboard_is_exported_to_xlsx_as_a_sheet_for_each_table_matrix_and_chart_and_a_failed_widget_is_its_error_block()
    {
        await using var h = await StartAsync();
        var (status, bytes, response) = await h.GetBytesAsync("/api/v1/dashboards/rows/export?format=xlsx", "west");
        Assert.Equal(200, status);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("rows-desk.xlsx", response.Content.Headers.ContentDisposition!.FileName);
        Assert.StartsWith("PK", Encoding.ASCII.GetString(bytes, 0, 2));

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["Metrics", "by_region", "by_category", "grid", "trend"], workbook.Worksheets.Select(w => w.Name));   // the metrics first, then the layout's order

        var metrics = workbook.Worksheet("Metrics");
        Assert.Equal(["Widget", "Label", "Value", "Compare", "Change"], Enumerable.Range(1, 5).Select(c => metrics.Cell(1, c).GetString()));
        Assert.Equal(("revenue", "Revenue"), (metrics.Cell(2, 1).GetString(), metrics.Cell(2, 2).GetString()));
        Assert.Equal(XLDataType.Number, metrics.Cell(2, 3).DataType);
        Assert.Equal(900, metrics.Cell(2, 3).GetDouble());                    // the West caller's figure, as the render shows it
        Assert.True(metrics.Cell(2, 4).IsEmpty() && metrics.Cell(3, 1).IsEmpty());   // no comparison, one metric

        var table = workbook.Worksheet("by_region");
        Assert.Equal("=HYPERLINK(\"http://example.invalid\",\"Region\")", table.Cell(1, 1).GetString());
        Assert.False(table.Cell(1, 1).HasFormula);                            // a text cell, never a formula
        Assert.Equal(XLDataType.Text, table.Cell(1, 1).DataType);
        Assert.Equal("West", table.Cell(2, 1).GetString());
        Assert.Equal(XLDataType.Number, table.Cell(2, 2).DataType);
        Assert.Equal(900, table.Cell(2, 2).GetDouble());
        Assert.True(table.Cell(2, 2).IsEmpty() == false && table.Cell(3, 1).IsEmpty());   // one row for the West caller

        var broken = workbook.Worksheet("by_category");
        Assert.Equal(["code", "message", "path"], Enumerable.Range(1, 3).Select(c => broken.Cell(1, c).GetString()));
        Assert.Equal("denied", broken.Cell(2, 1).GetString());
        Assert.Contains("not available", broken.Cell(2, 2).GetString());

        Assert.Equal("West", workbook.Worksheet("trend").Cell(2, 1).GetString());

        var one = await h.GetBytesAsync("/api/v1/dashboards/rows/export?format=xlsx&widget=grid", "all");
        using var single = new XLWorkbook(new MemoryStream(one.Bytes));
        Assert.Equal(["grid"], single.Worksheets.Select(w => w.Name));
        Assert.Equal("rows-grid.xlsx", one.Response.Content.Headers.ContentDisposition!.FileName);

        var detail = JsonNode.Parse(Audited(h)[0].Entry.Detail!)!;
        Assert.Equal(("xlsx", 5, 1), (detail["format"]!.GetValue<string>(), detail["sheets"]!.GetValue<int>(), detail["errors"]!.GetValue<int>()));
    }

    [Fact]
    public async Task A_failed_widget_is_refused_for_csv_with_its_code_and_words_and_a_parameter_narrows_the_rows()
    {
        await using var h = await StartAsync();
        var failed = await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=by_category", "all");
        Assert.Equal((422, "widget_failed"), (failed.Status, failed.Object["code"]!.GetValue<string>()));
        Assert.Equal("denied", failed.Object["widget"]!["code"]!.GetValue<string>());
        Assert.Contains("not available", failed.Object["message"]!.GetValue<string>());
        Assert.Equal("refused", Audited(h)[0].Entry.Outcome);

        // an unknown parameter is refused in the resolver's words, as for a PDF
        var bad = await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=by_region&p.nope=1", "all");
        Assert.Equal(400, bad.Status);
        Assert.Contains("nope", bad.Object["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_that_is_not_one_is_refused_in_words_and_audited_and_a_data_export_needs_no_browser()
    {
        await using var h = await StartAsync();
        string Code(Reply r) => r.Object["code"]!.GetValue<string>();

        var noWidget = await h.GetAsync("/api/v1/dashboards/rows/export?format=csv", "all");
        Assert.Equal((400, "invalid"), (noWidget.Status, Code(noWidget)));
        Assert.Contains("widget=<id>", noWidget.Object["message"]!.GetValue<string>());
        var unknown = await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=missing", "all");
        Assert.Equal((404, "widget_not_found"), (unknown.Status, Code(unknown)));
        var elsewhere = await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=by_region&layout=kpi", "all");
        Assert.Equal((400, "widget_not_placed"), (elsewhere.Status, Code(elsewhere)));
        Assert.Contains("'kpi'", elsewhere.Object["message"]!.GetValue<string>());
        var metric = await h.GetAsync("/api/v1/dashboards/rows/export?format=xlsx&widget=revenue", "all");
        Assert.Equal((400, "widget_not_tabular"), (metric.Status, Code(metric)));
        var none = await h.GetAsync("/api/v1/dashboards/rows/export?format=xlsx&layout=notes", "all");
        Assert.Equal((400, "nothing_tabular"), (none.Status, Code(none)));
        var format = await h.GetAsync("/api/v1/dashboards/rows/export?format=docx", "all");
        Assert.Equal((400, "invalid"), (format.Status, Code(format)));
        Assert.Contains("`csv`", format.Object["message"]!.GetValue<string>());
        var pdfWidget = await h.GetAsync("/api/v1/dashboards/rows/export?format=pdf&widget=grid", "all");
        Assert.Equal((400, "invalid"), (pdfWidget.Status, Code(pdfWidget)));
        // a picture needs the browser, and a host without one says so (the CSV above worked without it)
        var svg = await h.GetAsync("/api/v1/dashboards/rows/export?format=svg&widget=trend", "all");
        Assert.Equal((501, "export_unavailable"), (svg.Status, Code(svg)));
        Assert.Equal(404, (await h.GetAsync("/api/v1/dashboards/missing/export?format=csv&widget=x", "all")).Status);
        Assert.Equal(401, (await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=grid", null)).Status);
        // an app whose manifest lacks the model gets nothing: no request reaches the core
        var before = h.Host.Received.Count;
        Assert.Equal(403, (await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=grid", "app-other")).Status);
        Assert.Equal(before, h.Host.Received.Count);

        Assert.All(Audited(h), r => Assert.Equal("refused", r.Entry.Outcome));
        Assert.Equal(10, Audited(h).Count);          // everything but the call with no token
    }

    [Fact]
    public async Task An_export_over_the_row_limit_is_refused_with_its_numbers()
    {
        await using var h = await StartAsync(maxExportRows: 1);
        var big = await h.GetAsync("/api/v1/dashboards/rows/export?format=csv&widget=by_region", "all");
        Assert.Equal((413, "export_too_large"), (big.Status, big.Object["code"]!.GetValue<string>()));
        Assert.Contains("2 rows", big.Object["message"]!.GetValue<string>());
        Assert.Contains("limited to 1", big.Object["message"]!.GetValue<string>());
        Assert.Equal(200, (await h.GetBytesAsync("/api/v1/dashboards/rows/export?format=csv&widget=by_region", "west")).Status);   // one row fits
        Assert.Equal(413, (await h.GetAsync("/api/v1/dashboards/rows/export?format=xlsx", "all")).Status);
        Assert.Equal("export_too_large", JsonNode.Parse(Audited(h)[0].Entry.Detail!)!["code"]!.GetValue<string>());
    }
}
