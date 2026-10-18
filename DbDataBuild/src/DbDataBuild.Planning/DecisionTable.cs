using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Planning;

public sealed record DecisionRow(string Id, string Owner, string Kinds, string When, string Result, string? Risk, string? Code, string? Test);

/// <summary>The planning decision table as data (<c>matrix/decision-table.yml</c>, embedded). Loaded with the strict YAML reader like every other data file.</summary>
public static class DecisionTable
{
    private static readonly string[] Keys = ["id", "owner", "kinds", "when", "result", "risk", "code", "test"];
    public static readonly IReadOnlyList<string> Owners = ["planner", "command", "apply"];
    public static readonly IReadOnlyList<string> Results = ["ddl", "track", "load", "question", "block", "skip", "refuse", "note", "hook"];

    public static IReadOnlyList<DecisionRow> LoadEmbedded(List<Diagnostic> diags)
    {
        using var s = typeof(DecisionTable).Assembly.GetManifestResourceStream("matrix/decision-table.yml") ?? throw new InvalidOperationException("matrix/decision-table.yml is not embedded.");
        using var r = new StreamReader(s);
        return Load(r.ReadToEnd(), "matrix/decision-table.yml", diags);
    }

    public static IReadOnlyList<DecisionRow> Load(string text, string file, List<Diagnostic> diags)
    {
        var rows = new List<DecisionRow>();
        if (StrictYamlReader.Read(text, file, diags) is not YamlSequence seq) return rows;
        void Err(DiagnosticDescriptor d, YamlNode at, string found) => diags.Add(new Diagnostic(d, new(file, at.Line, at.Column), found));
        foreach (var item in seq.Items)
        {
            if (item is not YamlMapping m) { Err(DiagnosticCatalog.InvalidValue, item, "A decision-table row must be a mapping."); continue; }
            foreach (var e in m.Entries.Where(e => !Keys.Contains(e.Key.Value))) Err(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}`. Keys: {string.Join(", ", Keys)}.");
            string? Get(string k) => (m.Get(k) as YamlScalar)?.Value;
            var id = Get("id");
            if (string.IsNullOrEmpty(id)) { Err(DiagnosticCatalog.MissingKey, m, "A decision-table row needs an `id`."); continue; }
            foreach (var required in new[] { "owner", "kinds", "when", "result" })
                if (string.IsNullOrEmpty(Get(required))) Err(DiagnosticCatalog.MissingKey, m, $"Row `{id}` needs `{required}`.");
            if (Get("owner") is { } owner && !Owners.Contains(owner)) Err(DiagnosticCatalog.InvalidValue, m, $"Row `{id}`: owner `{owner}` is not one of {string.Join(", ", Owners)}.");
            if (Get("result") is { } result && !Results.Contains(result)) Err(DiagnosticCatalog.InvalidValue, m, $"Row `{id}`: result `{result}` is not one of {string.Join(", ", Results)}.");
            if (Get("owner") == "planner" && string.IsNullOrEmpty(Get("test"))) Err(DiagnosticCatalog.MissingKey, m, $"Planner row `{id}` needs a `test:` naming the test that proves it.");
            rows.Add(new DecisionRow(id, Get("owner") ?? "", Get("kinds") ?? "", Get("when") ?? "", Get("result") ?? "", Get("risk"), Get("code"), Get("test")));
        }
        foreach (var dup in rows.GroupBy(r => r.Id).Where(g => g.Count() > 1)) diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(file, 0, 0), $"Decision-table row id `{dup.Key}` appears more than once."));
        return rows;
    }
}
