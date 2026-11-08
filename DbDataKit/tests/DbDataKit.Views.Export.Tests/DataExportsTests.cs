using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Export.Tests;

/// <summary>
/// CSV and xlsx of rendered widgets (plan 32 DBR17), with no browser and no server: the rows of a rendered table, matrix and chart (the shapes of <c>rendered-widgets</c>) to the bytes, and back. Line breaks are the explicit CRLF of
/// RFC 4180 and nothing depends on a path, so the tests mean the same on Windows and Linux.
/// </summary>
public sealed class DataExportsTests
{
    private static JsonObject Table() => (JsonObject)JsonNode.Parse("""
        {"type":"table","columns":[{"field":"p.name","label":"Name","align":"left"},{"field":"s.amount","label":"Amount","align":"right"},{"field":"s.note","label":"Note","align":"left"}],
         "rows":[
          {"cells":[{"v":"Plain","text":"Plain"},{"v":1234.5,"text":"1,234.5"},{"v":"has, comma","text":"has, comma"}]},
          {"cells":[{"v":"say \"hi\"","text":"say \"hi\""},{"v":-5,"text":"-5"},{"v":"two\nlines","text":"two\nlines"}]},
          {"cells":[{"v":"=1+1","text":"=1+1"},{"v":null,"text":""},{"v":"+cmd|' /C calc'!A0","text":"x"}]},
          {"cells":[{"v":"-2+3","text":"-2+3"},{"v":3,"text":"3"},{"v":"@SUM(A1)","text":"@SUM(A1)"}]},
          {"cells":[{"v":"\tTabbed","text":"\tTabbed"},{"v":1e21,"text":"1e21"},{"v":"a=b-c+d@e","text":"a=b-c+d@e"}]},
          {"total":true,"cells":[{"v":null,"text":"Total"},{"v":1235,"text":"1,235"},{"v":null,"text":""}]}
         ]}
        """)!;

    private static string Decode(byte[] csv)
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv[..3]);          // the UTF-8 byte-order mark, once
        Assert.NotEqual(0xEF, csv[3]);
        return Encoding.UTF8.GetString(csv, 3, csv.Length - 3);
    }

    [Fact]
    public void A_table_is_csv_by_rfc_4180_with_the_values_the_engine_gave_and_the_total_row_where_it_is()
    {
        var csv = Decode(DataExports.Csv(DataExports.Sheet("t", Table())!));
        var expected = string.Join("\r\n",
            "Name,Amount,Note",
            "Plain,1234.5,\"has, comma\"",
            "\"say \"\"hi\"\"\",-5,\"two\nlines\"",
            "'=1+1,,'+cmd|' /C calc'!A0",
            "'-2+3,3,'@SUM(A1)",
            "'\tTabbed,1E+21,a=b-c+d@e",
            "Total,1235,") + "\r\n";
        Assert.Equal(expected, csv);
    }

    [Fact]
    public void A_text_that_a_spreadsheet_would_run_gets_an_apostrophe_and_a_number_is_never_changed()
    {
        foreach (var text in new[] { "=A1", "+1", "-1", "@x", "\tx", "\rx" }) Assert.Equal("'" + text, DataExports.Guard(text));
        foreach (var text in new[] { "", "a=1", " =1", "1+1", "x-y", "'=1" }) Assert.Equal(text, DataExports.Guard(text));
        var sheet = new DataSheet("n", "table", ["=evil"], [new DataRow([-12.5]), new DataRow([-0.0001]), new DataRow(["-12.5"])]);
        var lines = Decode(DataExports.Csv(sheet)).Split("\r\n");
        Assert.Equal(["'=evil", "-12.5", "-0.0001", "'-12.5", ""], lines);
    }

    [Fact]
    public void The_xlsx_holds_the_same_rows_with_numbers_as_numbers_and_text_as_text_never_a_formula()
    {
        using var workbook = new XLWorkbook(new MemoryStream(DataExports.Xlsx([DataExports.Sheet("t", Table())!])));
        var ws = Assert.Single(workbook.Worksheets);
        Assert.Equal("t", ws.Name);
        Assert.Equal(["Name", "Amount", "Note"], Enumerable.Range(1, 3).Select(c => ws.Cell(1, c).GetString()));
        Assert.True(ws.Row(1).Style.Font.Bold);
        Assert.Equal(1, ws.SheetView.SplitRow);                                  // the header row is frozen
        Assert.Equal(1234.5, ws.Cell(2, 2).GetDouble());
        Assert.Equal(XLDataType.Number, ws.Cell(2, 2).DataType);
        Assert.Equal("has, comma", ws.Cell(2, 3).GetString());
        Assert.Equal("two\nlines", ws.Cell(3, 3).GetString().Replace("\r\n", "\n"));   // a line break is whatever the file keeps; compared without the platform's
        Assert.Equal("=1+1", ws.Cell(4, 1).GetString());
        Assert.Equal(XLDataType.Text, ws.Cell(4, 1).DataType);
        Assert.False(ws.Cell(4, 1).HasFormula);
        Assert.Equal("+cmd|' /C calc'!A0", ws.Cell(4, 3).GetString());
        Assert.False(ws.Cell(4, 3).HasFormula);
        Assert.True(ws.Cell(4, 2).IsEmpty());
        Assert.Equal("-2+3", ws.Cell(5, 1).GetString());
        Assert.Equal(XLDataType.Text, ws.Cell(5, 1).DataType);
        Assert.Equal("Total", ws.Cell(7, 1).GetString());
        Assert.True(ws.Row(7).Style.Font.Bold);                                  // the total row
        Assert.False(ws.Row(2).Style.Font.Bold);
        Assert.Equal(7, ws.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void A_matrix_has_its_row_label_columns_named_and_one_column_for_each_column_path_and_a_chart_its_data_fields()
    {
        var matrix = (JsonObject)JsonNode.Parse("""
            {"id":"m","type":"matrix","columns":[{"labels":["East","Apparel"]},{"labels":["East","Outdoor"]},{"labels":["Total"],"total":true}],
             "rows":[{"labels":["Gold","A"],"cells":[{"v":1,"text":"1"},{"v":null,"text":""},{"v":1,"text":"1"}]},{"labels":["Total"],"total":true,"cells":[{"v":3,"text":"3"},{"v":4,"text":"4"},{"v":7,"text":"7"}]}]}
            """)!;
        var sheet = DataExports.Sheet("m", matrix, ["customers.region", "products.category"])!;
        Assert.Equal(["customers.region", "products.category", "East / Apparel", "East / Outdoor", "Total"], sheet.Header);
        Assert.Equal("matrix", sheet.Kind);
        Assert.Equal("customers.region,products.category,East / Apparel,East / Outdoor,Total\r\nGold,A,1,,1\r\nTotal,,3,4,7\r\n", Decode(DataExports.Csv(sheet)));
        Assert.True(sheet.Rows[1].Total);
        // fewer names than label columns: the rest are numbered
        Assert.Equal(["Row 1", "Row 2"], DataExports.Sheet("m", matrix)!.Header.Take(2));

        var chart = (JsonObject)JsonNode.Parse("""{"id":"c","type":"chart","vega_lite":{"mark":"bar","data":{"values":[{"region":"East","revenue":10,"open":true},{"region":"West","revenue":20.5,"extra":"x"}]}}}""")!;
        var rows = DataExports.Sheet("c", chart)!;
        Assert.Equal(["region", "revenue", "open", "extra"], rows.Header);
        Assert.Equal("region,revenue,open,extra\r\nEast,10,true,\r\nWest,20.5,,x\r\n", Decode(DataExports.Csv(rows)));
    }

    [Fact]
    public void A_failed_widget_is_its_error_block_and_a_widget_with_no_rows_is_none()
    {
        var error = (JsonObject)JsonNode.Parse("""{"type":"error","code":"unknown_member","path":"widgets.bad.encodings.y","message":"no such member"}""")!;
        var sheet = DataExports.Sheet("bad", error)!;
        Assert.Equal("error", sheet.Kind);
        Assert.Equal("code,message,path\r\nunknown_member,no such member,widgets.bad.encodings.y\r\n", Decode(DataExports.Csv(sheet)));
        foreach (var type in new[] { "metric", "control", "text", "image" })
            Assert.Null(DataExports.Sheet("w", new JsonObject { ["type"] = type }));
    }

    [Fact]
    public void Sheet_names_are_safe_and_different_ignoring_case_and_a_workbook_keeps_each_sheet()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("revenue", DataExports.SheetName("revenue", used));
        Assert.Equal("Revenue_2", DataExports.SheetName("Revenue", used));              // Excel compares sheet names without case
        Assert.Equal("a_b_c_d_e_f", DataExports.SheetName("a[b]c:d*e?f", used));
        Assert.Equal("Sheet", DataExports.SheetName("'", used));
        var long31 = DataExports.SheetName(new string('x', 40), used);
        Assert.Equal(31, long31.Length);
        var again = DataExports.SheetName(new string('x', 40), used);
        Assert.Equal(31, again.Length);
        Assert.NotEqual(long31, again);

        var sheets = new[] { "Totals", "totals", new string('y', 50) }.Select(id => new DataSheet(id, "table", ["a"], [new DataRow([1.0])])).ToList();
        using var workbook = new XLWorkbook(new MemoryStream(DataExports.Xlsx(sheets)));
        Assert.Equal(3, workbook.Worksheets.Count);
        Assert.Equal(3, workbook.Worksheets.Select(w => w.Name.ToUpperInvariant()).Distinct().Count());
        Assert.All(workbook.Worksheets, w => Assert.Equal(1, w.Cell(2, 1).GetDouble()));
    }

    [Fact]
    public void Non_ascii_text_and_a_long_text_survive_both_formats()
    {
        var sheet = new DataSheet("u", "table", ["Straße"], [new DataRow(["Zürich – 東京 😀"]), new DataRow([new string('z', 40000)])]);
        var csv = Decode(DataExports.Csv(sheet));
        Assert.StartsWith("Straße\r\nZürich – 東京 😀\r\n", csv);
        using var workbook = new XLWorkbook(new MemoryStream(DataExports.Xlsx([sheet])));
        var ws = workbook.Worksheet(1);
        Assert.Equal("Zürich – 東京 😀", ws.Cell(2, 1).GetString());
        Assert.Equal(32767, ws.Cell(3, 1).GetString().Length);                        // a cell holds at most 32,767 characters
    }
}
