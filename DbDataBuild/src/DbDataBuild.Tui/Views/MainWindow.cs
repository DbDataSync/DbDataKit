using System.Collections.ObjectModel;
using DbDataBuild.Tui.Model;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>The home screen: what can be done, grouped as a person thinks about it, with the effect of each action next to it.</summary>
public sealed class MainWindow : Window
{
    private sealed record Entry(string Label, string Key, string Description);

    private static readonly (string Group, string[] Commands)[] Groups =
    [
        ("Look at the project", ["project compile", "project show metadata", "project sample", "project show loads", "project show graph", "project tests run", "project tests list", "help matrix", "help code"]),
        ("Define", ["project model create", "project model update", "project agent-kit"]),
        ("Deploy and refresh", ["connection inspect", "connection status", "connection deploy", "connection refresh"]),
        ("Record and report", ["connection monitor", "connection compare", "connection init", "connection publish"]),
    ];

    private readonly TuiSession session;
    private readonly List<Entry> entries = [];
    private readonly ListView list;
    private readonly TextView details;
    private readonly Label status;
    private bool skipping;
    private int lastIndex;

    public MainWindow(TuiSession session)
    {
        this.session = session;
        Title = TitleText();
        Width = Dim.Fill();
        Height = Dim.Fill();

        entries.Add(new Entry("Models…", "@models", "Browse the models: columns, loads, indexes, hooks and index advice. Run a model on sample data, render it, or plan it."));
        entries.Add(new Entry("Plans…", "@plans", "Browse the plan files of this project: every step with its risk and script, and the report. Apply a plan from here."));
        foreach (var (group, commands) in Groups)
        {
            entries.Add(new Entry("── " + group, "", ""));
            foreach (var c in commands)
                if (session.Host.Commands.FirstOrDefault(x => x.Name == c) is { } info)
                    entries.Add(new Entry($"  {Mark(info)} {info.Name}", info.Name, Describe(info)));
        }

        var left = new FrameView { Title = "Actions", X = 0, Y = 0, Width = 34, Height = Dim.Fill(2) };
        list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        list.SetSource(new ObservableCollection<string>(entries.Select(e => e.Label)));
        left.Add(list);

        var right = new FrameView { Title = "About this action", X = 34, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        details = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        right.Add(details);

        status = new Ui.PlainLabel { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Text = "Enter: open   Ctrl+Q: quit   ● changes something (asks first)" };
        var hint = new Ui.PlainLabel { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "F1 keys   F2 models   F3 plans   F4 connection   F5 last result" };
        Add(left, right, status, hint);
        session.Status = s => status.Text = s;

        KeyDown += (_, k) =>
        {
            if (k == Key.F1) Ui.Message(session.App, "Keys", Help);
            else if (k == Key.F2) ModelsWindow.Open(session);
            else if (k == Key.F3) PlansWindow.Open(session);
            else if (k == Key.F4) ChooseTarget();
            else if (k == Key.F5) { if (session.LastResult != null) Flow.Show(session, session.LastResult); else Ui.Message(session.App, "No result yet", "Run something first."); }
            else return;
            k.Handled = true;
            SetNeedsDraw();
        };
        list.ValueChanged += (_, _) => ShowDetails();
        list.Accepted += (_, e) => { Open(); e.Handled = true; };
        list.SelectedItem = 0;
        ShowDetails();
    }

    private const string Help =
        "Up/Down        choose an action (Models and Plans browse, the rest are commands)\n" +
        "Enter          open the action: a form with every option of the command\n" +
        "Tab            move between fields and buttons; Esc closes a screen\n" +
        "F2 / F3        models / plans        F4 connection        F5 last result\n" +
        "Ctrl+Q         quit\n\n" +
        "Every action runs the same command you could type; the form shows that line.\n" +
        "A ● action asks before it changes anything. Plans are read before they are applied.";

    private void ChooseTarget()
    {
        var targets = DbDataBuild.Models.ProjectConfigLoader.LoadFromProject(session.ProjectRoot, []).Connections.Keys.Order(StringComparer.Ordinal).ToList();
        var choice = MessageBox.Query(session.App, "Connection", $"Work on which connection?{(session.Target != null ? $" (now {session.Target})" : "")}", [.. targets, "Cancel"]);
        if (choice is >= 0 and var i && i < targets.Count) { session.Target = targets[i]; Title = TitleText(); }
    }

    private string TitleText() => $"dbdatabuild  |  {session.ProjectRoot}{(session.Target != null ? "  |  connection " + session.Target : "")}";

    private static string Mark(CommandInfo c) => c.Impact == Impact.None ? " " : "●";

    private static string Describe(CommandInfo c) =>
        $"{c.Purpose}\n\nEffect: {c.Effect}\n\n" +
        (c.Arguments.Count + c.Options.Count == 0 ? "No options." : "Options:\n" + string.Join("\n", c.Arguments.Select(a => $"  {a.Name}{(a.Required ? " (required)" : "")}  {a.Description}").Concat(c.Options.Select(o => $"  {o.Name}  {o.Description}{(o.Default is { Length: > 0 } d && o.Kind != OptionKind.Flag ? $" [default {d}]" : "")}"))));

    private void ShowDetails()
    {
        var i = list.SelectedItem ?? 0;
        // a group heading is not an action: move on to the next entry in the direction of travel
        if (i >= 0 && i < entries.Count && entries[i].Key == "" && !skipping)
        {
            skipping = true;
            list.SelectedItem = Math.Min(entries.Count - 1, i > lastIndex ? i + 1 : Math.Max(0, i - 1));
            skipping = false;
            i = list.SelectedItem ?? 0;
        }
        lastIndex = i;
        details.Text = i >= 0 && i < entries.Count ? entries[i].Description : "";
    }

    private void Open()
    {
        var i = list.SelectedItem ?? 0;
        if (i < 0 || i >= entries.Count || entries[i].Key == "") return;
        var key = entries[i].Key;
        switch (key)
        {
            case "@models": ModelsWindow.Open(session); break;
            case "@plans": PlansWindow.Open(session); break;
            default:
                if (session.Host.Commands.FirstOrDefault(c => c.Name == key) is { } info) Flow.Command(session, info);
                break;
        }
        SetNeedsDraw();
    }
}
