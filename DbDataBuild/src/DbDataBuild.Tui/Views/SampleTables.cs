using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json.Nodes;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>The tables of a `sample` run: choose one on the left, see its columns and rows as a grid, and any error or warning above it.</summary>
public sealed class SampleTables : View
{
    public SampleTables(JsonArray tables)
    {
        Width = Dim.Fill(); Height = Dim.Fill();
        var names = tables.Select(t => $"{(string?)t!["name"]}  ({(string?)t["kind"]}, {(long?)t["row_count"]})").ToList();
        var list = new ListView { X = 0, Y = 0, Width = 34, Height = Dim.Fill() };
        list.SetSource(new ObservableCollection<string>(names));
        var note = new Ui.PlainLabel { X = 35, Y = 0, Width = Dim.Fill(), Height = 2, Text = "" };
        var grid = new TableView { X = 35, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
        Add(list, note, grid);

        void Show()
        {
            var i = list.SelectedItem ?? 0;
            if (i < 0 || i >= tables.Count) return;
            var t = tables[i]!;
            var table = new DataTable();
            foreach (var c in t["columns"]!.AsArray()) table.Columns.Add(((string?)c!["name"] ?? "?") + "\n" + ((string?)c["type"] ?? ""), typeof(string));
            foreach (var r in t["rows"]!.AsArray()) table.Rows.Add(r!.AsArray().Select(v => (object)((string?)v ?? "NULL")).ToArray());
            grid.Table = new DataTableSource(table);
            var warnings = t["warnings"]!.AsArray().Select(w => (string?)w).ToList();
            note.Text = (string?)t["error"] is { } err ? "FAILED: " + err : $"{(string?)t["origin"] ?? "model result"}   {(long?)t["row_count"]} row(s)" + (warnings.Count > 0 ? "\nwarning: " + string.Join("; ", warnings) : "");
        }
        list.ValueChanged += (_, _) => Show();
        Show();
    }
}
