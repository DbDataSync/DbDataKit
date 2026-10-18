using System.Text;

namespace DbDataBuild.Core.Questions;

/// <summary>Plain-text rendering of a question, shared by the interactive prompt and the non-interactive "unanswered" report.</summary>
public static class QuestionText
{
    public static string Describe(Question q)
    {
        var sb = new StringBuilder();
        sb.AppendLineLf(q.Id).Append("  ").AppendLineLf(q.Prompt);
        if (q.Context.Count > 0)
        {
            sb.AppendLineLf("  Context:");
            foreach (var c in q.Context) sb.Append("    - ").AppendLineLf(c);
        }
        sb.AppendLineLf("  Options:");
        foreach (var (o, i) in q.Options.Select((o, i) => (o, i)))
        {
            sb.Append($"    {i + 1}. {o.Key}  {o.Description}");
            if (o.Consequence != null) sb.Append($" (consequence: {o.Consequence})");
            sb.AppendLineLf();
        }
        if (q.Proposal is { } p)
        {
            sb.Append($"  Inferred ({p.Certainty.ToString().ToLowerInvariant()} certainty): {p.OptionKey}{(p.Value != null ? $" = {p.Value}" : "")}").AppendLineLf();
            foreach (var e in p.Evidence) sb.Append("    evidence: ").AppendLineLf(e);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The exact YAML that would answer the question, one paste-ready alternative per option (plus accepting the inferred proposal).</summary>
    public static string AnswerTemplate(Question q)
    {
        var sb = new StringBuilder();
        sb.AppendLineLf("Add one of these to the answers file:");
        foreach (var o in q.Options)
        {
            sb.AppendLineLf($"  - id: {q.Id}");
            sb.AppendLineLf($"    choice: {o.Key}");
            if (o.TakesValue) sb.AppendLineLf($"    value: <{o.ValueHint ?? "value"}>");
            sb.AppendLineLf($"    # {o.Description}");
        }
        if (q.Proposal != null)
        {
            sb.AppendLineLf($"  - id: {q.Id}");
            sb.AppendLineLf("    accept: inferred");
        }
        return sb.ToString().TrimEnd();
    }
}
