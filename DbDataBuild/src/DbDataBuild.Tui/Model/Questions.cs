using System.Text.Json.Nodes;
using DbDataBuild.Core.Questions;

namespace DbDataBuild.Tui.Model;

public sealed record QuestionOptionItem(string Key, string Description, string? Consequence, bool TakesValue, string? ValueHint);

public sealed record QuestionProposal(string Option, string? Value, string Certainty, IReadOnlyList<string> Evidence);

/// <summary>A question `plan` or `define` could not answer by itself, as the JSON document describes it.</summary>
public sealed record QuestionPrompt(string Id, string Prompt, IReadOnlyList<string> Context, IReadOnlyList<QuestionOptionItem> Options, QuestionProposal? Proposal)
{
    public static QuestionPrompt From(JsonObject o) => new(
        (string?)o["id"] ?? "", (string?)o["prompt"] ?? "", o["context"]?.AsArray().Select(c => (string?)c ?? "").ToList() ?? [],
        o["options"]?.AsArray().Select(x => new QuestionOptionItem((string?)x!["key"] ?? "", (string?)x["description"] ?? "", (string?)x["consequence"], (bool?)x["takes_value"] ?? false, (string?)x["value_hint"])).ToList() ?? [],
        o["proposal"] is JsonObject p ? new QuestionProposal((string?)p["option"] ?? "", (string?)p["value"], (string?)p["certainty"] ?? "", p["evidence"]?.AsArray().Select(e => (string?)e ?? "").ToList() ?? []) : null);
}

/// <summary>The answers a person gave, written as the answers file the CLI reads (`--answers`), so a run in the TUI and a run in a pipeline are the same thing.</summary>
public sealed class AnswerSet
{
    private readonly Dictionary<string, ResolvedAnswer> answers = new(StringComparer.Ordinal);

    public int Count => answers.Count;
    public bool Has(string id) => answers.ContainsKey(id);

    public void Add(string questionId, string choice, string? value, string? note = null) =>
        answers[questionId] = new ResolvedAnswer(questionId, choice, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), string.IsNullOrWhiteSpace(note) ? null : note.Trim(), AnswerSource.Interactive);

    public string ToYaml() => AnswerSerializer.ToYaml(answers.Values);

    /// <summary>Writes the file under the project's .dbdatabuild directory and returns its path.</summary>
    public string Write(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, ".dbdatabuild");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tui-answers.yml");
        File.WriteAllText(path, ToYaml());
        return path;
    }
}
