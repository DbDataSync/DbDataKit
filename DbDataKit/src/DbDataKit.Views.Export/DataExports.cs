using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;

namespace DbDataKit.Views.Export;

/// <summary>One row of a <see cref="DataSheet"/>: its cells (<c>null</c>, text, a <c>double</c> or a <c>bool</c>) and whether it is a total row of the result.</summary>
public sealed record DataRow(IReadOnlyList<object?> Cells, bool Total = false);

/// <summary>What a table, a matrix or a chart of a layout holds as rows: one sheet of an xlsx, or one CSV file (plan 32 DBR17). <paramref name="Kind"/> is the widget's type, or <c>error</c> for a widget that failed (its error block as rows).</summary>
public sealed record DataSheet(string Id, string Kind, IReadOnlyList<string> Header, IReadOnlyList<DataRow> Rows);

/// <summary>
/// The data exports of the views module (plan 32 section 4.5 and DBR17): CSV and xlsx of the rows a widget was <b>rendered</b> with, so the file is what the caller was shown. Pure functions over the rendered widget's JSON
/// (<c>rendered-widgets</c>): no browser, no network, nothing the caller could not already read. Numbers are written as the value the engine gave (<c>v</c>), not as the text a renderer formats; a cell with no value takes its text (a
/// total row's "Total"). CSV is RFC 4180 (comma, <c>CRLF</c>, a field quoted when it holds a comma, a quote or a line break) in UTF-8 <b>with a byte-order mark</b> (the reader that needs one is Excel, and every other reader skips it),
/// and a <b>text</b> cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return gets a leading apostrophe, so a spreadsheet opening the file never runs it as a formula (a number is never changed).
/// In the xlsx a text cell is a text cell and never a formula, so nothing is added there.
/// </summary>
public static class DataExports
{
    public const string CsvContentType = "text/csv; charset=utf-8";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The most rows an xlsx sheet holds (the header takes one).</summary>
    public const int XlsxMaxRows = 1_048_575;

    /// <summary>The rows of a rendered widget, or null for a widget that holds none (a metric, a control, text, an image). <paramref name="axisNames"/> names a matrix's row-label columns (the fields of its <c>rows</c> encoding).</summary>
    public static DataSheet? Sheet(string id, JsonObject widget, IReadOnlyList<string>? axisNames = null)
    {
        switch (widget["type"]?.GetValue<string>())
        {
            case "table": return Table(id, widget);
            case "matrix": return Matrix(id, widget, axisNames ?? []);
            case "chart": return Chart(id, widget);
            case "error":
                var row = new object?[] { widget["code"]?.GetValue<string>(), widget["message"]?.GetValue<string>(), widget["path"]?.GetValue<string>() };
                return new DataSheet(id, "error", ["code", "message", "path"], [new DataRow(row)]);
            default: return null;
        }
    }

    private static DataSheet Table(string id, JsonObject widget)
    {
        var header = (widget["columns"] as JsonArray ?? []).Select(c => c?["label"]?.GetValue<string>() ?? c?["field"]?.GetValue<string>() ?? "").ToList();
        var rows = new List<DataRow>();
        foreach (var row in widget["rows"] as JsonArray ?? [])
            rows.Add(new DataRow([.. (row?["cells"] as JsonArray ?? []).Select(Cell)], row?["total"]?.GetValue<bool>() == true));
        return new DataSheet(id, "table", header, rows);
    }

    private static DataSheet Matrix(string id, JsonObject widget, IReadOnlyList<string> axisNames)
    {
        var rowsIn = (widget["rows"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var columns = (widget["columns"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var labelCount = Math.Max(rowsIn.Select(r => (r["labels"] as JsonArray)?.Count ?? 0).DefaultIfEmpty(0).Max(), axisNames.Count);
        var header = new List<string>();
        for (var i = 0; i < labelCount; i++) header.Add(i < axisNames.Count ? axisNames[i] : $"Row {i + 1}");
        header.AddRange(columns.Select(c => string.Join(" / ", (c["labels"] as JsonArray ?? []).Select(l => l?.ToString() ?? ""))));
        var rows = new List<DataRow>();
        foreach (var row in rowsIn)
        {
            var cells = new List<object?>();
            var labels = (row["labels"] as JsonArray ?? []).Select(l => l?.ToString()).ToList();
            for (var i = 0; i < labelCount; i++) cells.Add(i < labels.Count ? labels[i] : null);
            cells.AddRange((row["cells"] as JsonArray ?? []).Select(Cell));
            rows.Add(new DataRow(cells, row["total"]?.GetValue<bool>() == true));
        }
        return new DataSheet(id, "matrix", header, rows);
    }

    private static DataSheet Chart(string id, JsonObject widget)
    {
        var values = (widget["vega_lite"]?["data"]?["values"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var header = new List<string>();
        foreach (var value in values)
            foreach (var key in value.Select(p => p.Key))
                if (!header.Contains(key, StringComparer.Ordinal)) header.Add(key);
        return new DataSheet(id, "chart", header, [.. values.Select(v => new DataRow([.. header.Select(h => Plain(v[h]))]))]);
    }

    /// <summary>A table or matrix cell <c>{v, text}</c>: the value when it has one, else its text (empty is no cell).</summary>
    private static object? Cell(JsonNode? cell)
    {
        if (Plain(cell?["v"]) is { } v) return v;
        return cell?["text"] is JsonValue t && t.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
    }

    private static object? Plain(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        null => null,
        _ => node.ToJsonString(),
    };

    // ---- CSV ----

    public static byte[] Csv(DataSheet sheet)
    {
        var text = new StringBuilder();
        text.Append('﻿');
        Line(text, sheet.Header.Cast<object?>());
        foreach (var row in sheet.Rows) Line(text, row.Cells);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    private static void Line(StringBuilder text, IEnumerable<object?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) text.Append(',');
            first = false;
            text.Append(Field(cell));
        }
        text.Append("\r\n");
    }

    private static string Field(object? cell)
    {
        var value = cell switch
        {
            null => "",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            string s => Guard(s),
            _ => Guard(cell.ToString() ?? ""),
        };
        return value.IndexOfAny(['"', ',', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>Text a spreadsheet would run as a formula gets an apostrophe before it (the OWASP rule: <c>=</c> <c>+</c> <c>-</c> <c>@</c>, tab, carriage return).</summary>
    public static string Guard(string text) => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    // ---- xlsx ----

    /// <summary>A workbook with a sheet for each of <paramref name="sheets"/> (named by the widget's id, made safe and unique), the header row bold and frozen, totals bold.</summary>
    public static byte[] Xlsx(IReadOnlyList<DataSheet> sheets)
    {
        using var workbook = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            var sheetName = SheetName(sheet.Id, used);
            var ws = workbook.Worksheets.Add(sheetName);
            for (var c = 0; c < sheet.Header.Count; c++) Put(ws.Cell(1, c + 1), sheet.Header[c]);
            ws.Row(1).Style.Font.Bold = true;
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r].Cells;
                for (var c = 0; c < cells.Count; c++) Put(ws.Cell(r + 2, c + 1), cells[c]);
                if (sheet.Rows[r].Total) ws.Row(r + 2).Style.Font.Bold = true;
            }
            ws.SheetView.FreezeRows(1);
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Text goes in as text (never read as a number, a date or a formula); a number as a number.</summary>
    private static void Put(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: return;
            case double d: cell.Value = d; break;
            case bool b: cell.Value = b; break;
            default:
                var text = value.ToString() ?? "";
                XLCellValue v = text.Length > 32767 ? text[..32767] : text;
                cell.Value = v;
                break;
        }
    }

    /// <summary>An xlsx sheet name: at most 31 characters, none of <c>[]:*?/\</c>, not starting or ending with an apostrophe, not empty, and different from every earlier one <b>ignoring case</b> (Excel compares them so).</summary>
    public static string SheetName(string id, ISet<string> used)
    {
        var name = new string([.. id.Select(ch => "[]:*?/\\".Contains(ch, StringComparison.Ordinal) ? '_' : ch)]).Trim('\'');
        if (name.Length == 0) name = "Sheet";
        if (name.Length > 31) name = name[..31];
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            candidate = name[..Math.Min(name.Length, 31 - suffix.Length)] + suffix;
        }
        return candidate;
    }
}
