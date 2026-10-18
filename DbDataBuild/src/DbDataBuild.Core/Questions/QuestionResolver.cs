namespace DbDataBuild.Core.Questions;

/// <summary>What an interactive prompter returns. <see cref="AcceptedProposal"/> means the person accepted the inferred proposal.</summary>
public sealed record PromptResult(string Choice, string? Value, string? Note, bool AcceptedProposal);

/// <summary>Asks one question. Returns null when the person stops answering (end of input). Implementations validate input against the question.</summary>
public interface IPrompter
{
    PromptResult? Ask(Question question);
}

/// <param name="AcceptHighCertaintyProposals">`--accept-inferred`: accept proposals marked high certainty, and only those.</param>
/// <param name="WarnOnUnknownAnswers">
/// Report answers whose question was not asked (DDB-410). A caller that asks in several rounds turns this off and accounts for
/// unused answers once, at the end, so an answer for a later round is not reported early.
/// </param>
public sealed record ResolveOptions(bool AcceptHighCertaintyProposals = false, bool WarnOnUnknownAnswers = true);

public sealed record Resolution(IReadOnlyList<ResolvedAnswer> Answers, IReadOnlyList<Question> Unanswered, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Every question has a valid answer and nothing failed. Only then may a plan be generated.</summary>
    public bool Complete => Unanswered.Count == 0 && Diagnostics.All(d => d.Severity != Severity.Error);
}

/// <summary>
/// Turns the open questions plus an answers file (and optionally a prompter) into resolved answers (DESIGN.md 10.1).
/// No question is ever answered by default. Without a prompter, every unanswered question is reported at once with the YAML to add.
/// The result does not depend on the order questions or answers were supplied in.
/// </summary>
public static class QuestionResolver
{
    public const string NoAnswersFile = "<answers>";

    public static Resolution Resolve(IReadOnlyList<Question> questions, AnswerFile? file, string answersFilePath,
        IPrompter? prompter = null, ResolveOptions? options = null)
    {
        options ??= new ResolveOptions();
        var dup = questions.GroupBy(q => q.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"Question id `{dup.Key}` was produced twice: ids must be deterministic and unique.");

        var diags = new List<Diagnostic>();
        var byId = questions.ToDictionary(q => q.Id);
        var fileAnswers = new Dictionary<string, Answer>();
        foreach (var a in file?.Answers ?? [])
        {
            if (!fileAnswers.TryAdd(a.QuestionId, a))
                diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, a.Location, $"The answers file answers `{a.QuestionId}` more than once."));
            else if (!byId.ContainsKey(a.QuestionId) && options.WarnOnUnknownAnswers)
                diags.Add(new Diagnostic(DiagnosticCatalog.AnswerForUnknownQuestion, a.Location, $"The answers file has an answer for `{a.QuestionId}`, which was not asked in this run."));
        }

        var resolved = new List<ResolvedAnswer>();
        var unanswered = new List<Question>();
        var stopped = false;

        foreach (var q in questions.OrderBy(q => q.Id, StringComparer.Ordinal))
        {
            if (fileAnswers.TryGetValue(q.Id, out var fa))
            {
                if (FromFile(q, fa, diags) is { } r) resolved.Add(r);
                else unanswered.Add(q);   // an invalid file answer is an error; it is not silently replaced by a prompt
            }
            else if (options.AcceptHighCertaintyProposals && q.Proposal is { Certainty: ProposalCertainty.High } p)
            {
                resolved.Add(new ResolvedAnswer(q.Id, p.OptionKey, p.Value, null, AnswerSource.AcceptedProposalByFlag));
            }
            else if (prompter != null && !stopped)
            {
                var result = prompter.Ask(q);
                if (result == null) { stopped = true; unanswered.Add(q); }
                else resolved.Add(FromPrompt(q, result));
            }
            else unanswered.Add(q);
        }

        foreach (var q in unanswered.Where(q => !fileAnswers.ContainsKey(q.Id)))
            diags.Add(new Diagnostic(DiagnosticCatalog.QuestionUnanswered, new(answersFilePath, 0, 0),
                QuestionText.Describe(q), Fix: QuestionText.AnswerTemplate(q)));

        return new Resolution(resolved, unanswered, diags);
    }

    private static ResolvedAnswer? FromFile(Question q, Answer a, List<Diagnostic> diags)
    {
        if (a.AcceptInferred)
        {
            if (q.Proposal is not { } p)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.NoProposalToAccept, a.Location, $"`{q.Id}` has no inferred proposal, so `accept: inferred` cannot answer it.",
                    Fix: $"Answer with `choice:`, one of {string.Join(", ", q.Options.Select(o => o.Key))}."));
                return null;
            }
            return new ResolvedAnswer(q.Id, p.OptionKey, p.Value, a.Note, AnswerSource.AcceptedProposal);
        }

        var option = q.FindOption(a.Choice ?? "");
        if (option == null)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.AnswerChoiceInvalid, a.Location, $"`{a.Choice}` is not an option of `{q.Id}`.",
                $"One of: {string.Join(", ", q.Options.Select(o => o.Key))}."));
            return null;
        }
        if (option.TakesValue != (a.Value != null))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.AnswerValueMismatch, a.Location,
                option.TakesValue ? $"Option `{option.Key}` of `{q.Id}` needs a `value:` ({option.ValueHint ?? "value"}), but none is given."
                                  : $"Option `{option.Key}` of `{q.Id}` takes no value, but `value:` is given."));
            return null;
        }
        return new ResolvedAnswer(q.Id, option.Key, a.Value, a.Note, AnswerSource.File);
    }

    private static ResolvedAnswer FromPrompt(Question q, PromptResult r)
    {
        if (r.AcceptedProposal)
        {
            var p = q.Proposal ?? throw new InvalidOperationException($"The prompter accepted a proposal for `{q.Id}`, which has none.");
            return new ResolvedAnswer(q.Id, p.OptionKey, p.Value, r.Note, AnswerSource.AcceptedProposal);
        }
        var option = q.FindOption(r.Choice) ?? throw new InvalidOperationException($"The prompter returned `{r.Choice}`, which is not an option of `{q.Id}`.");
        if (option.TakesValue != (r.Value != null)) throw new InvalidOperationException($"The prompter returned a value that does not fit option `{option.Key}` of `{q.Id}`.");
        return new ResolvedAnswer(q.Id, option.Key, r.Value, r.Note, AnswerSource.Interactive);
    }
}
