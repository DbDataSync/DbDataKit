using DbDataBuild.Core;
using DbDataBuild.Core.Questions;

namespace DbDataBuild.Tests.Unit;

public class QuestionResolverTests
{
    private static Resolution Resolve(Question[] qs, Answer[]? answers = null, IPrompter? prompter = null, bool flag = false) =>
        QuestionResolver.Resolve(qs, answers == null ? null : new AnswerFile(answers), "answers.yml", prompter, new ResolveOptions(flag));

    private sealed class ScriptedPrompter(params PromptResult?[] results) : IPrompter
    {
        private readonly Queue<PromptResult?> queue = new(results);
        public List<string> Asked { get; } = [];
        public PromptResult? Ask(Question q) { Asked.Add(q.Id); return queue.Count > 0 ? queue.Dequeue() : null; }
    }

    [Fact]
    public void Answers_from_a_file_resolve_questions_with_source_file()
    {
        var r = Resolve([Qs.History(), Qs.Rename()],
        [
            Qs.FileAnswer(Qs.History().Id, "not_backfilled", note: "No history exists in source."),
            Qs.FileAnswer(Qs.Rename().Id, "rename_to", value: "customer_name"),
        ]);
        Assert.True(r.Complete);
        Assert.Empty(r.Diagnostics);
        var h = Assert.Single(r.Answers, a => a.QuestionId == Qs.History().Id);
        Assert.Equal(("not_backfilled", null, "No history exists in source.", AnswerSource.File), (h.Choice, h.Value, h.Note, h.Source));
        var rn = Assert.Single(r.Answers, a => a.QuestionId == Qs.Rename().Id);
        Assert.Equal(("rename_to", "customer_name"), (rn.Choice, rn.Value));
    }

    [Fact]
    public void Without_answers_or_a_prompter_every_open_question_is_listed_at_once_with_its_yaml()
    {
        var r = Resolve([Qs.Rename(), Qs.History(), Qs.DefineType()]);
        Assert.False(r.Complete);
        Assert.Equal(3, r.Unanswered.Count);
        var unanswered = r.Diagnostics.Where(d => d.Code == "DDB-414").ToList();
        Assert.Equal(3, unanswered.Count);
        Assert.All(unanswered, d => Assert.Equal(Severity.Error, d.Severity));
        Assert.All(unanswered, d => Assert.Equal("answers.yml", d.Location.File));
        var history = Assert.Single(unanswered, d => d.Found.StartsWith(Qs.History().Id));
        Assert.Contains($"  - id: {Qs.History().Id}\n    choice: backfilled", history.Fix);
        Assert.Contains("choice: not_backfilled", history.Fix);
        Assert.Contains("accept: inferred", Assert.Single(unanswered, d => d.Found.StartsWith(Qs.Rename().Id)).Fix);
        Assert.Empty(r.Answers);
    }

    [Fact]
    public void Nothing_is_answered_by_default()
    {
        var r = Resolve([Qs.History(), Qs.DefineType()]);          // DefineType has a high-certainty proposal; without the flag it is not used
        Assert.Empty(r.Answers);
        Assert.Equal(2, r.Unanswered.Count);
    }

    [Fact]
    public void Result_is_independent_of_input_order_and_sorted_by_id()
    {
        var a = Resolve([Qs.Rename(), Qs.History()], [Qs.FileAnswer(Qs.Rename().Id, "drop_and_add"), Qs.FileAnswer(Qs.History().Id, "backfilled")]);
        var b = Resolve([Qs.History(), Qs.Rename()], [Qs.FileAnswer(Qs.History().Id, "backfilled"), Qs.FileAnswer(Qs.Rename().Id, "drop_and_add")]);
        Assert.Equal(a.Answers, b.Answers);
        Assert.Equal(a.Answers.Select(x => x.QuestionId).Order(StringComparer.Ordinal), a.Answers.Select(x => x.QuestionId));
        Assert.Equal(a.Diagnostics.Count, b.Diagnostics.Count);
    }

    [Fact]
    public void Same_scenario_gives_the_same_question_ids_and_an_answers_file_fully_resolves_them()
    {
        var first = new[] { Qs.History(), Qs.Rename() };
        var second = new[] { Qs.History(), Qs.Rename() };
        Assert.Equal(first.Select(q => q.Id), second.Select(q => q.Id));
        var answers = first.Select(q => Qs.FileAnswer(q.Id, q.Options[1].Key)).ToArray();
        Assert.True(Resolve(second, answers).Complete);
    }

    [Fact]
    public void Invalid_choice_value_and_accept_are_errors_that_keep_the_question_unanswered()
    {
        var bad = Resolve([Qs.History()], [Qs.FileAnswer(Qs.History().Id, "maybe")]);
        Assert.Contains(bad.Diagnostics, d => d.Code == "DDB-411" && d.Found.Contains("`maybe`"));
        Assert.False(bad.Complete);
        Assert.DoesNotContain(bad.Diagnostics, d => d.Code == "DDB-414");   // the answer exists; it is wrong, not missing

        var noValue = Resolve([Qs.Rename()], [Qs.FileAnswer(Qs.Rename().Id, "rename_to")]);
        Assert.Contains(noValue.Diagnostics, d => d.Code == "DDB-412" && d.Found.Contains("needs a `value:`"));

        var extraValue = Resolve([Qs.History()], [Qs.FileAnswer(Qs.History().Id, "backfilled", value: "x")]);
        Assert.Contains(extraValue.Diagnostics, d => d.Code == "DDB-412" && d.Found.Contains("takes no value"));

        var noProposal = Resolve([Qs.History()], [Qs.FileAnswer(Qs.History().Id, accept: true)]);
        Assert.Contains(noProposal.Diagnostics, d => d.Code == "DDB-413");
        Assert.All(new[] { bad, noValue, extraValue, noProposal }, r => Assert.Empty(r.Answers));
    }

    [Fact]
    public void Accept_inferred_resolves_to_the_proposals_option_and_value()
    {
        var r = Resolve([Qs.Rename()], [Qs.FileAnswer(Qs.Rename().Id, accept: true, note: "looks right")]);
        Assert.True(r.Complete);
        var a = Assert.Single(r.Answers);
        Assert.Equal(("rename_to", "customer_name", "looks right", AnswerSource.AcceptedProposal), (a.Choice, a.Value, a.Note, a.Source));
    }

    [Fact]
    public void Accept_inferred_flag_takes_only_high_certainty_proposals_and_records_it()
    {
        var r = Resolve([Qs.DefineType(), Qs.Rename(), Qs.History()], flag: true);
        var accepted = Assert.Single(r.Answers);
        Assert.Equal(Qs.DefineType().Id, accepted.QuestionId);
        Assert.Equal(("use_type", "DECIMAL(14, 2)", AnswerSource.AcceptedProposalByFlag), (accepted.Choice, accepted.Value, accepted.Source));
        Assert.Equal(2, r.Unanswered.Count);                                  // the normal-certainty proposal and the one without a proposal stay open
        Assert.Equal(2, r.Diagnostics.Count(d => d.Code == "DDB-414"));
    }

    [Fact]
    public void An_explicit_file_answer_wins_over_the_accept_flag()
    {
        var r = Resolve([Qs.DefineType()], [Qs.FileAnswer(Qs.DefineType().Id, "other_type")], flag: true);
        var a = Assert.Single(r.Answers);
        Assert.Equal(("other_type", AnswerSource.File), (a.Choice, a.Source));
    }

    [Fact]
    public void Answers_for_questions_not_asked_are_a_warning_not_a_failure()
    {
        var r = Resolve([Qs.History()], [Qs.FileAnswer(Qs.History().Id, "backfilled"), Qs.FileAnswer("Q-history-marts.other.x", "backfilled", line: 7)]);
        Assert.True(r.Complete);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(("DDB-410", Severity.Warning, 7), (d.Code, d.Severity, d.Location.Line));
    }

    [Fact]
    public void Duplicate_answers_for_one_question_are_an_error()
    {
        var r = Resolve([Qs.History()], [Qs.FileAnswer(Qs.History().Id, "backfilled"), Qs.FileAnswer(Qs.History().Id, "not_backfilled", line: 4)]);
        Assert.False(r.Complete);
        Assert.Contains(r.Diagnostics, d => d.Code == "DDB-102" && d.Location.Line == 4);
    }

    [Fact]
    public void Duplicate_question_ids_are_a_bug_in_the_caller()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve([Qs.History(), Qs.History()]));
    }

    [Fact]
    public void A_prompter_answers_the_questions_the_file_does_not()
    {
        var prompter = new ScriptedPrompter(new PromptResult("backfilled", null, "n", false));
        var r = Resolve([Qs.History(), Qs.Rename()], [Qs.FileAnswer(Qs.Rename().Id, "drop_and_add")], prompter);
        Assert.True(r.Complete);
        Assert.Equal([Qs.History().Id], prompter.Asked);                    // only the open question is asked
        Assert.Equal(AnswerSource.Interactive, r.Answers.Single(a => a.QuestionId == Qs.History().Id).Source);
        Assert.Equal(AnswerSource.File, r.Answers.Single(a => a.QuestionId == Qs.Rename().Id).Source);
    }

    [Fact]
    public void A_prompter_can_accept_a_proposal()
    {
        var r = Resolve([Qs.Rename()], prompter: new ScriptedPrompter(new PromptResult("rename_to", null, null, true)));
        var a = Assert.Single(r.Answers);
        Assert.Equal(("rename_to", "customer_name", AnswerSource.AcceptedProposal), (a.Choice, a.Value, a.Source));
    }

    [Fact]
    public void When_the_prompter_stops_the_remaining_questions_stay_open_and_are_listed()
    {
        var prompter = new ScriptedPrompter(new PromptResult("backfilled", null, null, false), null);
        var r = Resolve([Qs.History("a_col"), Qs.History("b_col"), Qs.History("c_col")], prompter: prompter);
        Assert.Single(r.Answers);
        Assert.Equal(2, r.Unanswered.Count);
        Assert.Equal(2, r.Diagnostics.Count(d => d.Code == "DDB-414"));
        Assert.Equal(2, prompter.Asked.Count);                              // not asked again after it stopped
    }

    [Fact]
    public void An_invalid_file_answer_is_never_replaced_by_a_prompt()
    {
        var prompter = new ScriptedPrompter(new PromptResult("backfilled", null, null, false));
        var r = Resolve([Qs.History()], [Qs.FileAnswer(Qs.History().Id, "maybe")], prompter);
        Assert.Empty(prompter.Asked);
        Assert.False(r.Complete);
    }

    [Fact]
    public void A_misbehaving_prompter_is_a_bug_not_a_user_error()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve([Qs.History()], prompter: new ScriptedPrompter(new PromptResult("nonsense", null, null, false))));
        Assert.Throws<InvalidOperationException>(() => Resolve([Qs.History()], prompter: new ScriptedPrompter(new PromptResult("backfilled", "x", null, false))));
        Assert.Throws<InvalidOperationException>(() => Resolve([Qs.History()], prompter: new ScriptedPrompter(new PromptResult("backfilled", null, null, true))));
    }

    [Fact]
    public void Resolved_answers_serialize_to_the_answers_file_format_and_round_trip()
    {
        var qs = new[] { Qs.Rename(), Qs.History() };
        var resolved = Resolve(qs, [Qs.FileAnswer(Qs.Rename().Id, "rename_to", value: "customer \"name\": v2"), Qs.FileAnswer(Qs.History().Id, "not_backfilled", note: "No history: source (see ticket #4)\nline two")]);
        var yaml = AnswerSerializer.ToYaml(resolved.Answers);
        Assert.StartsWith("answers:\n  - id: Q-history-marts.fct_orders.discount_code\n", yaml);   // sorted by id

        var diags = new List<Diagnostic>();
        var file = DbDataBuild.Models.AnswerFileLoader.Load(yaml, "plan-answers.yml", diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        var again = QuestionResolver.Resolve(qs, file, "plan-answers.yml");
        Assert.True(again.Complete);
        Assert.Equal(resolved.Answers.Select(a => (a.QuestionId, a.Choice, a.Value, a.Note)), again.Answers.Select(a => (a.QuestionId, a.Choice, a.Value, a.Note)));
    }
}
