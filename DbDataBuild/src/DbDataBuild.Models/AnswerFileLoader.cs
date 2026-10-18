using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Loads an answers file (<c>answers.yml</c>, DESIGN.md 10.1) with the strict YAML rules. Each answer is explicit: a `choice` (plus `value` where the option takes one) or `accept: inferred`.</summary>
public static partial class AnswerFileLoader
{
    private static readonly string[] TopKeys = ["answers"];
    private static readonly string[] AnswerKeys = ["id", "choice", "value", "accept", "note"];

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ChoicePattern();

    public static AnswerFile? Load(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null) return diags.Count > before ? null : new AnswerFile([]);   // an empty file answers nothing
        var result = new Reader(file, diags).Read(root);
        return diags.Count > before ? null : result;
    }

    private sealed class Reader(string file, List<Diagnostic> diags) : YamlFieldReader(file, diags)
    {
        public AnswerFile? Read(YamlNode root)
        {
            if (root is not YamlMapping top) { Add(DiagnosticCatalog.InvalidValue, root, "An answers file must be a mapping with an `answers:` list."); return null; }
            CheckKeys(top, TopKeys, "the answers file");
            if (top.Get("answers") is not { } node) { Add(DiagnosticCatalog.MissingKey, top, "Required key `answers` is missing.", fix: "Add `answers:` followed by a list of answers."); return null; }
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`answers` must be a list."); return null; }

            var answers = new List<Answer>();
            var seen = new HashSet<string>();
            foreach (var item in seq.Items)
                if (ReadAnswer(item) is { } a)
                {
                    if (seen.Add(a.QuestionId)) answers.Add(a);
                    else Diagnostics.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, a.Location, $"`{a.QuestionId}` is answered more than once."));
                }
            return new AnswerFile(answers);
        }

        private Answer? ReadAnswer(YamlNode item)
        {
            if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each answer must be a mapping with `id` and `choice` (or `accept: inferred`)."); return null; }
            CheckKeys(m, AnswerKeys, "an answer");

            var id = Scalar(m, "id", required: true, at: m);
            if (id != null && !QuestionIds.IsValid(id.Value))
            {
                Add(DiagnosticCatalog.InvalidValue, id, $"`{id.Value}` is not a valid question id.", $"Q-<area>-<subject>, area one of {string.Join(", ", QuestionAreas.All)}.");
                id = null;
            }

            var choice = Scalar(m, "choice", required: false, at: m);
            var accept = Scalar(m, "accept", required: false, at: m);
            var value = Scalar(m, "value", required: false, at: m);
            var note = Scalar(m, "note", required: false, at: m);

            var ok = id != null;
            if (choice == null && accept == null && m.Get("choice") == null && m.Get("accept") == null)
            {
                Add(DiagnosticCatalog.MissingKey, m, "An answer needs `choice:` (or `accept: inferred`).", fix: "Add `choice: <option key>`.");
                ok = false;
            }
            if (choice != null && accept != null)
            {
                Add(DiagnosticCatalog.InvalidValue, accept, "Give either `choice:` or `accept: inferred`, not both.");
                ok = false;
            }
            if (choice != null && !ChoicePattern().IsMatch(choice.Value))
            {
                Add(DiagnosticCatalog.InvalidValue, choice, $"`{choice.Value}` is not a valid option key.", "Lowercase snake_case, such as `not_backfilled`.");
                ok = false;
            }
            if (accept != null && accept.Value != "inferred")
            {
                Add(DiagnosticCatalog.InvalidValue, accept, $"`accept` is `{accept.Value}`.", "`accept: inferred` is the only accepted form.");
                ok = false;
            }
            if (accept != null && value != null)
            {
                Add(DiagnosticCatalog.InvalidValue, value, "`value:` cannot be given with `accept: inferred`.");
                ok = false;
            }
            if (!ok || id == null) return null;
            return new Answer(id.Value, choice?.Value, value?.Value, note?.Value, accept != null, new SourceLocation(File, id.Line, id.Column));
        }
    }
}
