using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

/// <summary>`dbdatabuild project tests list`: the project's tests with their kind, severity and tags, so a tag or a name for `project tests run` can be chosen. Reads the files under tests/ and runs nothing.</summary>
internal static class TestListCommand
{
    public static int Run(CommandSpec spec, string root, string? kind, string[] tags, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (kind is not (null or "metadata" or "model")) { error.WriteLine("--kind is `metadata` or `model`."); return CliApp.ExitUsage; }
        bool Wanted(IReadOnlyList<string> t) => tags.Length == 0 || t.Any(x => tags.Contains(x, StringComparer.Ordinal));

        var rows = new List<(string Name, string Kind, string Severity, IReadOnlyList<string> Tags, string File, string? Model, int Cases, string? Description)>();
        var damaged = 0;
        if (kind != "model")
            foreach (var file in TestRuleLoader.Discover(root))
            {
                var problems = new List<Diagnostic>();
                var rule = TestRuleLoader.Load(File.ReadAllText(Path.Combine(root, file)), file, problems);
                if (rule == null) { foreach (var d in problems) error.Diag(d); damaged++; continue; }
                if (Wanted(rule.Tags)) rows.Add((rule.Name, "metadata", rule.Severity, rule.Tags, file, null, 1, rule.Description));
            }
        if (kind != "metadata")
            foreach (var file in ModelTestLoader.Discover(root))
            {
                var problems = new List<Diagnostic>();
                var test = ModelTestLoader.Load(File.ReadAllText(Path.Combine(root, file)), file, problems);
                if (test == null) { foreach (var d in problems) error.Diag(d); damaged++; continue; }
                if (Wanted(test.Tags)) rows.Add((test.Name, "model", test.Severity, test.Tags, file, test.Model, test.Cases.Count, test.Description));
            }
        rows = rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        output.Payload("tests", rows.Select(r => new { name = r.Name, kind = r.Kind, file = r.File, model = r.Model, cases = r.Cases, description = r.Description, severity = r.Severity, tags = r.Tags }).ToList());
        output.Payload("damaged", damaged);
        if (rows.Count == 0) output.WriteLine(damaged > 0 ? "No test could be read." : "No tests.");
        else
        {
            var width = rows.Max(r => r.Name.Length);
            output.WriteLine($"{"test".PadRight(width)}  {"kind",-8}  {"severity",-8}  tags");
            foreach (var r in rows)
                output.WriteLine($"{r.Name.PadRight(width)}  {r.Kind,-8}  {r.Severity,-8}  {(r.Tags.Count == 0 ? "-" : string.Join(", ", r.Tags))}{(r.Model != null ? $"   (model {r.Model}, {r.Cases} case(s))" : "")}");
        }
        output.Next("project tests run" + (tags.Length > 0 ? " " + string.Join(" ", tags.Select(t => $"--tag {t}")) : ""));
        return damaged > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }
}
