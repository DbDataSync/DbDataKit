using DbDataBuild.Tui.Model;

namespace DbDataBuild.Tui.Views;

/// <summary>
/// The path from "I want to run this" to a result: fill in the form, confirm if it changes something, run, answer any questions the command stopped to ask (they go into
/// an answers file and the command runs again with `--answers`, exactly as a pipeline would), and show what came back.
/// </summary>
public static class Flow
{
    private const int MaxRounds = 30;

    public static void Command(TuiSession s, CommandInfo info, Action<FormModel>? prefill = null)
    {
        // the terminal interface asks its questions itself, so a deploy from here plans and stops: applying is its own, confirmed, step (the plan window)
        void Prefill(FormModel f) { if (info.Name == "connection deploy") f.Field("--write-plan").Checked = true; prefill?.Invoke(f); }
        var form = new FormWindow(s, info, Prefill);
        s.App.Run(form);
        if (!form.Confirmed) return;
        Execute(s, info, form.Form.FullArguments(), form.Form.ChangesSomething(), form.Form.CommandLine());
    }

    public static void Execute(TuiSession s, CommandInfo info, IReadOnlyList<string> args, bool changes, string commandLine)
    {
        if (changes && !Ui.Confirm(s.App, "This changes something", $"{info.Name}: {info.Effect}\n\n{commandLine}\n\nRun it?", "Run", "Cancel")) return;
        var result = s.Run(args);
        var answers = new AnswerSet();
        for (var round = 0; round < MaxRounds; round++)
        {
            var open = result.OpenQuestions().Where(q => !answers.Has(q.Id)).ToList();
            if (open.Count == 0) break;
            if (!QuestionsWindow.Ask(s, open, answers)) { s.Status?.Invoke($"{info.Name}: {open.Count} question(s) left unanswered; nothing was done"); break; }
            args = WithAnswers(args, answers.Write(s.ProjectRoot));
            result = s.Run(args);
        }
        Show(s, result);
    }

    public static void Show(TuiSession s, ResultModel result)
    {
        var window = new ResultWindow(s, result);
        s.App.Run(window);
        if (window.OpenPlan && result.PlanFile != null) PlanWindow.Open(s, Path.Combine(s.ProjectRoot, result.PlanFile));
    }

    internal static IReadOnlyList<string> WithAnswers(IReadOnlyList<string> args, string file)
    {
        var list = args.ToList();
        var i = list.IndexOf("--answers");
        if (i >= 0 && i + 1 < list.Count) list[i + 1] = file;
        else { list.Add("--answers"); list.Add(file); }
        return list;
    }
}
