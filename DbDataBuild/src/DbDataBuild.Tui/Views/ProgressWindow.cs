using DbDataBuild.Core;
using System.Collections.Concurrent;
using DbDataBuild.Tui.Model;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>
/// Shown while a command that takes more than a moment runs: how long it has been going, and what it has reported (for `apply`, each step as it starts and finishes). It closes
/// itself when the command ends. `apply` and `run` get a button that asks them to stop after the step they are on; the request is honoured between steps only, so nothing is left
/// half done, and the plan can be continued with `apply --resume`.
/// </summary>
public sealed class ProgressWindow : Ui.Modal
{
    private readonly TextView log;
    private readonly Ui.PlainLabel elapsed;

    public ProgressWindow(TuiSession session, IReadOnlyList<string> args, ConcurrentQueue<string> lines, Task<CommandResult> task, DateTime started, bool canStop, Action requestStop)
        : base($"Running {args[0]}", 80, 70)
    {
        elapsed = new Ui.PlainLabel { X = 0, Y = 0, Width = Dim.Fill(1), Text = "" };
        log = new TextView { X = 0, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(3), ReadOnly = true, WordWrap = true };
        Add(elapsed, log);
        var note = new Ui.PlainLabel { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = canStop ? "Stop finishes the step that is running; it never abandons a statement." : "This cannot be stopped part way; it only reads." };
        Add(note);
        if (canStop)
        {
            Button? stop = null;
            stop = Ui.Button("Stop after this step", () =>
            {
                requestStop();
                stop!.Text = "Stopping…";
                stop.Enabled = false;
                note.Text = "Stopping: waiting for the step that is running to finish.";
            });
            stop.X = 0; stop.Y = Pos.AnchorEnd(1);
            Add(stop);
        }

        // the command runs elsewhere; this tick, on the interface thread, shows what it reported and closes the window when it ends
        session.App.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = new System.Text.StringBuilder();
            while (lines.TryDequeue(out var line)) text.AppendLineLf(line);
            if (text.Length > 0) { log.Text += text.ToString(); log.MoveEnd(); }
            var span = DateTime.UtcNow - started;
            elapsed.Text = $"dbdatabuild {string.Join(' ', args.Take(1))}   {(int)span.TotalMinutes}:{span.Seconds:00}";
            if (task.IsCompleted) { session.App.RequestStop(); return false; }
            return true;
        });
    }
}
