using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbDataBuild.Core.Questions;

/// <summary>One answer as written in an answers file: an explicit <c>choice</c> (with a <c>value</c> where the option takes one), or <c>accept: inferred</c>.</summary>
public sealed record Answer(string QuestionId, string? Choice, string? Value, string? Note, bool AcceptInferred, SourceLocation Location);

public sealed record AnswerFile(IReadOnlyList<Answer> Answers);

/// <summary>How a question came to be answered. Recorded so a plan or diff can show what was accepted and how.</summary>
public enum AnswerSource
{
    File,
    Interactive,
    /// <summary>`accept: inferred` in the file, or the accept key at a prompt.</summary>
    AcceptedProposal,
    /// <summary>`--accept-inferred`, which covers only high-certainty proposals.</summary>
    AcceptedProposalByFlag,
}

public sealed record ResolvedAnswer(string QuestionId, string Choice, string? Value, string? Note, AnswerSource Source);

/// <summary>Writes resolved answers in the answers-file format, so they can be embedded in a plan and reused with <c>--answers</c>.</summary>
public static class AnswerSerializer
{
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ToYaml(IEnumerable<ResolvedAnswer> answers)
    {
        var sb = new StringBuilder("answers:\n");
        foreach (var a in answers.OrderBy(a => a.QuestionId, StringComparer.Ordinal))
        {
            sb.Append("  - id: ").AppendLineLf(a.QuestionId);
            sb.Append("    choice: ").AppendLineLf(a.Choice);
            if (a.Value != null) sb.Append("    value: ").AppendLineLf(Scalar(a.Value));
            if (a.Note != null) sb.Append("    note: ").AppendLineLf(Scalar(a.Note));
        }
        return sb.ToString();
    }

    // A JSON string is a valid YAML double-quoted scalar, so every free-text value is written quoted and escaped.
    private static string Scalar(string value) => JsonSerializer.Serialize(value, Quote);
}
