using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Agents.Evaluation;

/// <summary>One thing a good result must have: <see cref="Kind"/> is one of <see cref="EvaluationCaseLoader.CheckKinds"/>, <see cref="Values"/> the names it lists, <see cref="Text"/> the text it carries.</summary>
public sealed record EvaluationCheck(string Kind, IReadOnlyList<string> Values, string? Text, int Line)
{
    /// <summary>Whether the suite decides it (a tool called, a text in the answer, rows compared, a check the host knows); the rubric is the one a judge model reads.</summary>
    public bool IsJudged => Kind == "rubric";

    public string Describe() => Values.Count > 0 ? $"{Kind}: {string.Join(", ", Values)}" : $"{Kind}: {Text}";
}

/// <summary>A case of an agent's evaluation set (plan 27a AG23): the task it is given, the fixtures that go with it and what a good result is. <see cref="PassAt"/> is the score at which it counts as passed.</summary>
public sealed record EvaluationCase(string Name, string File, string Task, IReadOnlyList<KeyValuePair<string, string>> Fixtures, IReadOnlyList<EvaluationCheck> Expect, double PassAt);

public sealed class EvaluationCaseResult(EvaluationCase? evaluation, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public EvaluationCase? Case { get; } = evaluation;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Case is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of a case of an evaluation set, <c>evaluation/1</c> (plan 27a AG23), the files <c>tests/evaluation/*.yml</c> of an agent: <c>evaluation: 1</c>, <c>name</c> (the file's name when left out), <c>task</c> (what the agent is asked),
/// <c>fixtures</c> (named texts that go with the task), <c>pass-at</c> (a score from 0 to 1, 1 by default) and <c>expect</c>, a list of one-key entries: <c>tools-called</c>, <c>tools-not-called</c>, <c>answer-contains</c>, <c>answer-matches</c>
/// (a regular expression), <c>rows-match-verified-query</c> (the id of a verified query: the rows of the agent's query and the verified one are compared), <c>check</c> (a check the host knows by name, such as that the workspace compiles) and
/// <c>rubric</c> (free text judged by the judge model, the only thing that is). Every problem is a diagnostic with file, line and column (<c>DBD-EVL-001</c>); a newer format is refused naming the file and both numbers.
/// </summary>
public static class EvaluationCaseLoader
{
    public const string BadCase = "DBD-EVL-001";
    public const int Version = 1;
    public const string Kind = "evaluation";
    public const int MaximumTask = 4000;
    public const int MaximumChecks = 50;

    public static readonly string[] CaseKeys = ["evaluation", "name", "task", "fixtures", "pass-at", "expect"];

    public static readonly string[] CheckKinds = ["tools-called", "tools-not-called", "answer-contains", "answer-matches", "rows-match-verified-query", "check", "rubric"];

    public static string Schema()
    {
        using var stream = typeof(EvaluationCaseLoader).Assembly.GetManifestResourceStream("DbDataKit.Agents.schemas.evaluation.schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} case '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} case '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    public static EvaluationCaseResult Load(string text, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadCase, file, 1, 1, "The file is empty.", "Write `evaluation: 1`, a `task` and an `expect` list."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var item = new Reader(file, diags).Read(root);
        return new(diags.HasErrors() ? null : item, diags);
    }

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : YamlFieldReader(fileName, diags)
    {
        private void Bad(YamlNode at, string message, string? fix = null) => Add(BadCase, at, message, fix);

        public EvaluationCase? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "A case is a mapping with `evaluation: 1`, a `task` and an `expect` list."); return null; }
            if (!CheckFormat(map)) return null;
            CheckKeys(map, CaseKeys, "an evaluation case");

            var name = Path.GetFileNameWithoutExtension(File);
            if (map.Get("name") is { } nameNode)
            {
                if (nameNode is YamlScalar { Value.Length: > 0 and <= 100 } n && Regex.IsMatch(n.Value, "^[A-Za-z0-9][A-Za-z0-9_.-]*$")) name = n.Value;
                else Bad(nameNode, "`name` is a short name: letters, digits, `-`, `_` and `.`.");
            }
            string? task = null;
            if (map.Get("task") is not { } taskNode) Add(SharedCodes.MissingKey, map, "Required key `task` is missing: what the agent is asked.", "Add `task:`.");
            else if (taskNode is YamlScalar { Value.Length: > 0 } t && t.Value.Length <= MaximumTask) task = t.Value.Trim();
            else Bad(taskNode, $"`task` is the text the agent is given, up to {MaximumTask} characters.");

            var fixtures = new List<KeyValuePair<string, string>>();
            if (map.Get("fixtures") is { } fixturesNode)
            {
                if (fixturesNode is YamlMapping fm && fm.Entries.Count <= 20)
                    foreach (var e in fm.Entries)
                    {
                        if (e.Value is YamlScalar { Value.Length: > 0 } v) fixtures.Add(new(e.Key.Value, v.Value));
                        else Bad(e.Value, $"The fixture `{e.Key.Value}` is text.");
                    }
                else Bad(fixturesNode, "`fixtures` is a mapping of up to twenty named texts that go with the task.");
            }

            var passAt = 1.0;
            if (map.Get("pass-at") is { } passNode)
            {
                if (passNode is YamlScalar s && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 1) passAt = p;
                else Bad(passNode, "`pass-at` is a score above 0 and at most 1 (1 means every check must hold).");
            }

            var expect = new List<EvaluationCheck>();
            if (map.Get("expect") is not { } expectNode) Add(SharedCodes.MissingKey, map, "Required key `expect` is missing: what a good result is.", "Add `expect:` with one or more checks.");
            else if (expectNode is YamlSequence { Items.Count: > 0 } list && list.Items.Count <= MaximumChecks)
            {
                foreach (var entry in list.Items)
                    if (ReadCheck(entry) is { } check) expect.Add(check);
            }
            else Bad(expectNode, $"`expect` is a list of one to {MaximumChecks} checks, each a mapping with one key: {string.Join(", ", CheckKinds)}.");

            return task is null || expect.Count == 0 ? null : new EvaluationCase(name, File, task, fixtures, expect, passAt);
        }

        private EvaluationCheck? ReadCheck(YamlNode entry)
        {
            if (entry is not YamlMapping { Entries.Count: 1 } map)
            {
                Bad(entry, $"A check is a mapping with one key: {string.Join(", ", CheckKinds)}.", "Example: `- tools-called: [semantic_query]`.");
                return null;
            }
            var key = map.Entries[0].Key;
            var value = map.Entries[0].Value;
            if (!CheckKinds.Contains(key.Value))
            {
                var near = DidYouMean.Closest(key.Value, CheckKinds);
                Bad(key, $"`{key.Value}` is not a kind of check." + (near is not null ? $" Did you mean `{near}`?" : ""), $"Kinds: {string.Join(", ", CheckKinds)}.");
                return null;
            }
            switch (key.Value)
            {
                case "tools-called" or "tools-not-called":
                    var names = new List<string>();
                    foreach (var item in value is YamlSequence seq ? seq.Items : [value])
                    {
                        if (item is YamlScalar { Value.Length: > 0 } s && Regex.IsMatch(s.Value, "^[A-Za-z0-9_.:-]+$")) names.Add(s.Value);
                        else { Bad(item, $"`{key.Value}` names tools: words, as the agent's tools are named."); return null; }
                    }
                    return names.Count == 0 ? Fail(value, $"`{key.Value}` names at least one tool.") : new EvaluationCheck(key.Value, names, null, key.Line);
                case "answer-matches":
                    if (value is YamlScalar { Value.Length: > 0 } pattern)
                    {
                        try { _ = new Regex(pattern.Value, RegexOptions.None, TimeSpan.FromSeconds(1)); return new EvaluationCheck(key.Value, [], pattern.Value, key.Line); }
                        catch (ArgumentException ex) { return Fail(value, $"`answer-matches` is a regular expression, and this is not one: {ex.Message}"); }
                    }
                    return Fail(value, "`answer-matches` is a regular expression.");
                case "rubric":
                    return value is YamlScalar { Value.Length: > 0 and <= 2000 } rubric ? new EvaluationCheck(key.Value, [], rubric.Value.Trim(), key.Line) : Fail(value, "`rubric` is a written description of a good answer, up to 2000 characters, for the judge model to read.");
                default:
                    return value is YamlScalar { Value.Length: > 0 and <= 500 } text ? new EvaluationCheck(key.Value, [], text.Value, key.Line) : Fail(value, $"`{key.Value}` is one text.");
            }
        }

        private EvaluationCheck? Fail(YamlNode at, string message) { Bad(at, message); return null; }

        private bool CheckFormat(YamlMapping map)
        {
            var node = map.Get("evaluation");
            if (node is null) { Add(SharedCodes.MissingKey, map, $"`evaluation` is missing: the file says which format of {Kind} it is in.", "Add `evaluation: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var format) || format < 1) { Bad(node, "`evaluation` must be a positive whole number."); return false; }
            if (format == Version) return true;
            Add(BadCase, node, Refusal(File, format), format > Version ? $"Upgrade the tool, or write the file in format {Version}." : null);
            return false;
        }
    }
}
