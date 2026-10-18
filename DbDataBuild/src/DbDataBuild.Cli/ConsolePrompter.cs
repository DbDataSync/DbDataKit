using DbDataBuild.Core.Questions;

namespace DbDataBuild.Cli;

/// <summary>
/// Asks questions on a text terminal. Pick an option by number or key; press <c>a</c> to accept the inferred proposal when there is one.
/// Input that is not an option is refused and asked again, never defaulted. End of input stops answering (the question stays unanswered).
/// </summary>
public sealed class ConsolePrompter(TextReader input, TextWriter output) : IPrompter
{
    public PromptResult? Ask(Question question)
    {
        output.WriteLine();
        output.WriteLine(QuestionText.Describe(question));
        var accept = question.Proposal != null ? ", 'a' to accept the inferred answer" : "";

        while (true)
        {
            output.Write($"Choose 1-{question.Options.Count}{accept}: ");
            var line = input.ReadLine()?.Trim();
            if (line == null) return null;

            if (question.Proposal is { } p && line == "a")
                return new PromptResult(p.OptionKey, p.Value, AskNote(), AcceptedProposal: true);

            var option = int.TryParse(line, out var n) && n >= 1 && n <= question.Options.Count
                ? question.Options[n - 1]
                : question.FindOption(line);
            if (option == null)
            {
                output.WriteLine($"`{line}` is not one of the options. Enter a number from 1 to {question.Options.Count}, or an option key.");
                continue;
            }

            string? value = null;
            if (option.TakesValue)
            {
                while (string.IsNullOrWhiteSpace(value))
                {
                    output.Write($"Value ({option.ValueHint ?? "value"}): ");
                    value = input.ReadLine()?.Trim();
                    if (value == null) return null;
                    if (value.Length == 0) output.WriteLine("A value is required for this option.");
                }
            }
            return new PromptResult(option.Key, value, AskNote(), AcceptedProposal: false);
        }
    }

    private string? AskNote()
    {
        output.Write("Note (optional, Enter to skip): ");
        var note = input.ReadLine()?.Trim();
        return string.IsNullOrEmpty(note) ? null : note;
    }
}
