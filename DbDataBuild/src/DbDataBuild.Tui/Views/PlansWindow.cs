using System.Collections.ObjectModel;
using DbDataBuild.Tui.Model;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>The plan files in the project, newest first. Choose one to read it.</summary>
public sealed class PlansWindow : Ui.Modal
{
    public static void Open(TuiSession s)
    {
        var files = PlanBrowser.Find(s.ProjectRoot, s.Target);
        if (files.Count == 0) { Ui.Message(s.App, "No plans yet", $"There are no plan files under {Path.Combine(s.ProjectRoot, "plans")}{(s.Target != null ? " for target " + s.Target : "")}.\nRun `connection deploy` first."); return; }
        s.App.Run(new PlansWindow(s, files));
    }

    private PlansWindow(TuiSession s, IReadOnlyList<string> files) : base($"Plans  ({files.Count})", 80, 70)
    {
        var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(2) };
        list.SetSource(new ObservableCollection<string>(files.Select(f => Path.GetRelativePath(s.ProjectRoot, f))));
        list.Accepted += (_, e) => { Open(s, files, list); e.Handled = true; };
        var open = Ui.Button("Open", () => Open(s, files, list), isDefault: true);
        open.X = 0; open.Y = Pos.AnchorEnd(1);
        var close = Ui.Button("Close", () => Close(s.App));
        close.X = Pos.Right(open) + 2; close.Y = Pos.AnchorEnd(1);
        Add(list, open, close);
        Ui.EscapeCloses(this, s.App);
    }

    private static void Open(TuiSession s, IReadOnlyList<string> files, ListView list)
    {
        if ((list.SelectedItem ?? -1) is var i && i >= 0 && i < files.Count) PlanWindow.Open(s, files[i]);
    }
}
