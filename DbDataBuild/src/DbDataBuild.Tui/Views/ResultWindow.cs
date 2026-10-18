using DbDataBuild.Core;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Tui.Model;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>What a command returned: the headline, each diagnostic with its fix, what the command printed, and its data. Nothing is dropped.</summary>
public sealed class ResultWindow : Ui.Modal
{
    public bool OpenPlan { get; private set; }

    public ResultWindow(TuiSession session, ResultModel result) : base($"{result.Headline()}", 94, 92)
    {
        var tabs = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };

        var summary = new View { Title = "Output", Width = Dim.Fill(), Height = Dim.Fill() };
        summary.Add(new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true, Text = result.OutputText() });
        tabs.Add(summary);

        if (result.Diagnostics.Count > 0)
        {
            var diag = new View { Title = $"Diagnostics ({result.Diagnostics.Count})", Width = Dim.Fill(), Height = Dim.Fill() };
            var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = 8 };
            list.SetSource(new ObservableCollection<string>(result.Diagnostics.Select(d => d.Summary)));
            var detail = new TextView { X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
            void Show() { if ((list.SelectedItem ?? 0) is var i and >= 0 && i < result.Diagnostics.Count) detail.Text = Describe(result.Diagnostics[i]); }
            list.ValueChanged += (_, _) => Show();
            diag.Add(list, detail);
            tabs.Add(diag);
            Show();
        }

        var data = new View { Title = "Data", Width = Dim.Fill(), Height = Dim.Fill() };
        data.Add(DataView(result));
        tabs.Add(data);

        Add(tabs);
        var close = Ui.Button("Close", () => Close(session.App), isDefault: result.PlanFile == null);
        close.X = 0; close.Y = Pos.AnchorEnd(1);
        Add(close);
        if (result.PlanFile != null)
        {
            var open = Ui.Button("Open the plan", () => { OpenPlan = true; Close(session.App, confirmed: true); }, isDefault: true);
            open.X = Pos.Right(close) + 2; open.Y = Pos.AnchorEnd(1);
            Add(open);
        }
        Ui.EscapeCloses(this, session.App);
    }

    private static string Describe(DiagnosticItem d)
    {
        var sb = new StringBuilder();
        sb.AppendLineLf($"{d.Severity} {d.Code}: {d.Title}");
        if (d.Location.Length > 0) sb.AppendLineLf($"at {d.Location}");
        sb.AppendLineLf().AppendLineLf(d.Found);
        if (d.Supported.Length > 0) sb.AppendLineLf().AppendLineLf("Supported: " + d.Supported);
        if (d.Fix.Length > 0) sb.AppendLineLf().AppendLineLf("Fix: " + d.Fix);
        sb.AppendLineLf().AppendLineLf($"More: dbdatabuild help code {d.Code}");
        return sb.ToString();
    }

    private static View DataView(ResultModel result)
    {
        if (result.Command == "sample" && result.Data["tables"] is JsonArray tables && tables.Count > 0) return new SampleTables(tables);
        return new TextView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true,
            Text = result.Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        };
    }
}
