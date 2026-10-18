using System.Collections.ObjectModel;
using DbDataBuild.Planning;
using DbDataBuild.Tui.Model;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>
/// A plan to read before anything runs: every step with its risk, the reasons for it and the exact script, and the report. Apply is offered here, with the risk flags the plan
/// needs already explained; a dry run executes nothing.
/// </summary>
public sealed class PlanWindow : Ui.Modal
{
    private readonly PlanBrowser plan;
    private readonly ListView steps;
    private readonly TextView detail;
    private bool showingReport;

    public static void Open(TuiSession s, string path)
    {
        var (browser, problems) = PlanBrowser.Load(path);
        if (browser == null) { Ui.Message(s.App, "This plan cannot be read", string.Join("\n", problems.Take(5).Select(p => p.Found))); return; }
        s.App.Run(new PlanWindow(s, browser));
    }

    private PlanWindow(TuiSession s, PlanBrowser plan) : base($"Plan {plan.Plan.Id}", 96, 94)
    {
        this.plan = plan;
        var summary = new Ui.PlainLabel { X = 0, Y = 0, Width = Dim.Fill(1), Height = 2, Text = plan.Summary };
        steps = new ListView { X = 0, Y = 2, Width = 54, Height = Dim.Fill(2) };
        steps.SetSource(new ObservableCollection<string>(plan.Plan.Steps.Select(plan.Row)));
        detail = new TextView { X = Pos.Right(steps) + 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2), ReadOnly = true, WordWrap = false };
        steps.ValueChanged += (_, _) => ShowStep();

        var close = Ui.Button("Close", () => Close(s.App), isDefault: true);
        close.X = 0; close.Y = Pos.AnchorEnd(1);
        var report = Ui.Button("Report / steps", ToggleReport);
        report.X = Pos.Right(close) + 2; report.Y = Pos.AnchorEnd(1);
        var dry = Ui.Button("Dry run", () => Apply(s, dryRun: true));
        dry.X = Pos.Right(report) + 2; dry.Y = Pos.AnchorEnd(1);
        var apply = Ui.Button("Apply…", () => Apply(s, dryRun: false));
        apply.X = Pos.Right(dry) + 2; apply.Y = Pos.AnchorEnd(1);
        Add(summary, steps, detail, close, report, dry, apply);
        Ui.EscapeCloses(this, s.App);
        ShowStep();
    }

    private void ShowStep()
    {
        showingReport = false;
        var i = steps.SelectedItem ?? 0;
        detail.Text = i >= 0 && i < plan.Plan.Steps.Count ? plan.Detail(plan.Plan.Steps[i]) : "";
    }

    private void ToggleReport()
    {
        if (showingReport) { ShowStep(); return; }
        showingReport = true;
        detail.Text = plan.Report ?? "There is no report file next to this plan.";
    }

    private void Apply(TuiSession s, bool dryRun)
    {
        var info = s.Host.Commands.First(c => c.Name == "connection deploy");
        // the plan, the project, and the flags this plan needs are filled in; the person can still change any of them
        var form = new FormWindow(s, info, f =>
        {
            f.Field("--apply-plan").Value = plan.Path;
            f.Field("--dry-run").Checked = dryRun;
            if (plan.Risky > 0 || plan.Destructive > 0) f.Field("--allow-risky").Checked = !dryRun;
            if (plan.DestructiveObjects.Count > 0) f.Field("--allow-destructive").Value = string.Join(",", plan.DestructiveObjects);
        });
        s.App.Run(form);
        if (!form.Confirmed) return;
        Flow.Execute(s, info, form.Form.FullArguments(), form.Form.ChangesSomething(), form.Form.CommandLine());
    }
}
