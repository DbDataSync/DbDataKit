using System.Text.Json.Nodes;

namespace DbDataBuild.Tui.Model;

public sealed record DiagnosticItem(string Code, string Severity, string Title, string File, int Line, string Found, string Supported, string Fix)
{
    public string Location => Line > 0 ? $"{File}:{Line}" : File;
    public string Summary => $"{Severity,-7} {Code}  {Found.Split('\n')[0]}";
}

/// <summary>A command's `--format json` document as the TUI uses it. A command that printed something else is shown as it is, never hidden.</summary>
public sealed class ResultModel
{
    public required string Command { get; init; }
    public required int Exit { get; init; }
    public required IReadOnlyList<DiagnosticItem> Diagnostics { get; init; }
    public required IReadOnlyList<string> Messages { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required JsonNode Data { get; init; }
    public string? RawOutput { get; init; }

    public bool Ok => Exit == 0;
    public bool IsDocument => RawOutput == null;
    public int Count(string severity) => Diagnostics.Count(d => d.Severity == severity);

    public static ResultModel From(string command, CommandResult r)
    {
        try
        {
            if (JsonNode.Parse(r.Out) is JsonObject doc && (string?)doc["schema"] == "dbdatabuild.output/1")
                return new ResultModel
                {
                    Command = (string?)doc["command"] ?? command, Exit = (int?)doc["exit_code"] ?? r.Exit,
                    Diagnostics = doc["diagnostics"]?.AsArray().Select(d => new DiagnosticItem(
                        (string?)d!["code"] ?? "", (string?)d["severity"] ?? "", (string?)d["title"] ?? "", (string?)d["location"]?["file"] ?? "", (int?)d["location"]?["line"] ?? 0,
                        (string?)d["found"] ?? "", (string?)d["supported"] ?? "", (string?)d["fix"] ?? "")).ToList() ?? [],
                    Messages = doc["messages"]?.AsArray().Select(m => (string?)m ?? "").ToList() ?? [],
                    Errors = doc["errors"]?.AsArray().Select(m => (string?)m ?? "").ToList() ?? [],
                    Data = doc["data"] as JsonObject ?? new JsonObject(),
                };
        }
        catch (System.Text.Json.JsonException) { /* not a document: fall through */ }
        return new ResultModel { Command = command, Exit = r.Exit, Diagnostics = [], Messages = [], Errors = r.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries), Data = new JsonObject(), RawOutput = r.Out };
    }

    /// <summary>One line: what happened, as the status bar shows it.</summary>
    public string Headline() => Exit switch
    {
        0 => $"{Command}: ok" + (Count("warning") + Count("note") > 0 ? $" ({Count("warning")} warning(s), {Count("note")} note(s))" : ""),
        1 => $"{Command}: findings ({Count("error")} error(s), {Count("warning")} warning(s))",
        2 => $"{Command}: usage error",
        70 => $"{Command}: internal error (a tool bug)",
        _ => $"{Command}: exit {Exit}",
    };

    /// <summary>The human text of the command (what text mode would have printed on standard output), for the Output tab.</summary>
    public string OutputText() => RawOutput ?? string.Join('\n', Messages) + (Errors.Count > 0 ? "\n\n" + string.Join('\n', Errors) : "");

    /// <summary>The open questions of a `plan` or `define` run, if it stopped to ask.</summary>
    public IReadOnlyList<QuestionPrompt> OpenQuestions()
    {
        var list = new List<QuestionPrompt>();
        void Read(JsonNode? n)
        {
            if (n is not JsonArray a) return;
            foreach (var q in a) if (q is JsonObject o) list.Add(QuestionPrompt.From(o));
        }
        Read(Data["open_questions"]);
        if (Data["models"] is JsonArray models) foreach (var m in models) Read(m?["open_questions"]);
        return list.DistinctBy(q => q.Id).ToList();
    }

    /// <summary>The plan file `plan` wrote, relative to the project.</summary>
    public string? PlanFile => (string?)Data["files"]?["plan"];
}
