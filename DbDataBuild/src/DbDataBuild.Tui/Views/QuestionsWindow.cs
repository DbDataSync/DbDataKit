using DbDataBuild.Core;
using System.Collections.ObjectModel;
using System.Text;
using DbDataBuild.Tui.Model;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>One question a command could not answer by itself, with its context, its options and the tool's own proposal (never taken without an explicit choice).</summary>
public sealed class QuestionsWindow : Ui.Modal
{
    private readonly QuestionPrompt question;
    private readonly ListView options;
    private readonly TextField value;
    private readonly TextField note;

    public string Choice { get; private set; } = "";
    public string? Value { get; private set; }
    public string? Note { get; private set; }

    /// <summary>Asks each question in turn. False when the person cancelled; the answers given so far stay in <paramref name="answers"/>.</summary>
    public static bool Ask(TuiSession s, IReadOnlyList<QuestionPrompt> questions, AnswerSet answers)
    {
        var n = 0;
        foreach (var q in questions)
        {
            var w = new QuestionsWindow(s, q, ++n, questions.Count);
            s.App.Run(w);
            if (!w.Confirmed) return false;
            answers.Add(q.Id, w.Choice, w.Value, w.Note);
        }
        return true;
    }

    private QuestionsWindow(TuiSession s, QuestionPrompt q, int number, int total) : base($"Question {number} of {total}", 86, 90)
    {
        question = q;
        var text = new StringBuilder();
        text.AppendLineLf(q.Prompt).AppendLineLf();
        foreach (var c in q.Context) text.AppendLineLf("  " + c);
        if (q.Proposal != null)
            text.AppendLineLf().AppendLineLf($"Proposed: {q.Proposal.Option}{(q.Proposal.Value != null ? " " + q.Proposal.Value : "")} ({q.Proposal.Certainty}) because: {string.Join("; ", q.Proposal.Evidence)}");
        text.AppendLineLf().AppendLineLf(q.Id);
        var body = new TextView { X = 0, Y = 0, Width = Dim.Fill(1), Height = 10, ReadOnly = true, WordWrap = true, Text = text.ToString() };

        options = new ListView { X = 0, Y = Pos.Bottom(body) + 1, Width = Dim.Fill(1), Height = Math.Min(q.Options.Count + 1, 8) };
        options.SetSource(new ObservableCollection<string>(q.Options.Select(o => $"{o.Key}  -  {o.Description}{(o.Consequence is { Length: > 0 } c ? "  [" + c + "]" : "")}")));
        var vLabel = new Ui.PlainLabel { X = 0, Y = Pos.Bottom(options) + 1, Text = "Value:" };
        value = new TextField { X = 8, Y = Pos.Bottom(options) + 1, Width = Dim.Fill(1) };
        var nLabel = new Ui.PlainLabel { X = 0, Y = Pos.Bottom(options) + 2, Text = "Note:" };
        note = new TextField { X = 8, Y = Pos.Bottom(options) + 2, Width = Dim.Fill(1) };
        var hint = new Ui.PlainLabel { X = 0, Y = Pos.Bottom(options) + 3, Width = Dim.Fill(1), Text = "" };

        void SyncHint()
        {
            var o = Current();
            hint.Text = o is { TakesValue: true } ? $"This option takes a value{(o.ValueHint != null ? " (" + o.ValueHint + ")" : "")}." : "This option takes no value.";
        }
        options.ValueChanged += (_, _) => SyncHint();

        var answer = Ui.Button("Answer", () => Submit(s), isDefault: true);
        answer.X = 0; answer.Y = Pos.AnchorEnd(1);
        var cancel = Ui.Button("Cancel", () => Close(s.App));
        cancel.X = Pos.Right(answer) + 2; cancel.Y = Pos.AnchorEnd(1);
        Add(body, options, vLabel, value, nLabel, note, hint, answer, cancel);

        if (q.Proposal != null)
        {
            var i = q.Options.ToList().FindIndex(o => o.Key == q.Proposal.Option);
            if (i >= 0) options.SelectedItem = i;
            value.Text = q.Proposal.Value ?? "";
        }
        SyncHint();
        Ui.EscapeCloses(this, s.App);
    }

    private QuestionOptionItem? Current() => (options.SelectedItem ?? -1) is var i && i >= 0 && i < question.Options.Count ? question.Options[i] : null;

    private void Submit(TuiSession s)
    {
        var o = Current();
        if (o == null) return;
        if (o.TakesValue && string.IsNullOrWhiteSpace(value.Text)) { Ui.Message(s.App, "A value is needed", $"The option `{o.Key}` takes a value{(o.ValueHint != null ? " (" + o.ValueHint + ")" : "")}."); return; }
        Choice = o.Key;
        Value = o.TakesValue ? value.Text : null;
        Note = note.Text;
        Close(s.App, confirmed: true);
    }
}
