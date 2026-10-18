using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Planning;

namespace DbDataBuild.Tui.Model;

/// <summary>A plan file as the TUI shows it: steps with risk, each step's full detail and script, the Markdown report, and what applying it needs to be allowed.</summary>
public sealed class PlanBrowser
{
    public required string Path { get; init; }
    public required Plan Plan { get; init; }
    public string? Report { get; init; }

    public static (PlanBrowser? Browser, IReadOnlyList<Diagnostic> Problems) Load(string path)
    {
        var diags = new List<Diagnostic>();
        var plan = PlanDocument.Parse(File.ReadAllText(path), path, diags);
        if (plan == null || diags.Any(d => d.Severity == Severity.Error)) return (null, diags);
        var md = System.IO.Path.ChangeExtension(path, null);   // .plan.yml -> .plan
        var reportPath = (md.EndsWith(".plan", StringComparison.Ordinal) ? md : path) + ".md";
        return (new PlanBrowser { Path = path, Plan = plan, Report = File.Exists(reportPath) ? File.ReadAllText(reportPath) : null }, diags);
    }

    public int Risky => Plan.Steps.Count(s => s.Risk == RiskClass.Risky);
    public int Destructive => Plan.Steps.Count(s => s.Risk == RiskClass.Destructive);

    /// <summary>The objects whose destructive steps need `--allow-destructive`.</summary>
    public IReadOnlyList<string> DestructiveObjects => Plan.Steps.Where(s => s.Risk == RiskClass.Destructive).Select(s => s.Object).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    public string Summary =>
        $"{Plan.Id}  |  target {Plan.Connection}  |  {Plan.Steps.Count} step(s): " +
        string.Join(", ", Enum.GetValues<StepType>().Select(t => (t, n: Plan.Steps.Count(s => s.Type == t))).Where(x => x.n > 0).Select(x => $"{x.n} {x.t.ToString().ToLowerInvariant()}")) +
        $"  |  {Risky} risky, {Destructive} destructive" + (Plan.GitDirty ? "  |  working tree was dirty" : "");

    public string Row(PlanStep s) => $"{s.Id,3} {s.Type.ToString().ToLowerInvariant(),-8} {Mark(s.Risk),-4} {s.Description}";

    private static string Mark(RiskClass r) => r switch { RiskClass.Safe => "ok", RiskClass.Risky => "RISK", _ => "DROP" };

    public string Detail(PlanStep s)
    {
        var sb = new StringBuilder();
        sb.AppendLineLf($"Step {s.Id}: {s.Description}");
        sb.AppendLineLf($"Type {s.Type.ToString().ToLowerInvariant()}   Risk {s.Risk.ToString().ToLowerInvariant()}   Object {s.Object}");
        if (s.Operation != null) sb.AppendLineLf($"Operation {s.Operation}");
        if (s.Hook != null) sb.AppendLineLf($"Hook {s.Hook} ({s.Effect})");
        if (s.Reasons.Count > 0) sb.AppendLineLf("Why: " + string.Join("; ", s.Reasons));
        foreach (var p in s.Parameters) sb.AppendLineLf($"Parameter @{p.Name} ({p.Type}, {p.Source}) = {p.Value ?? "NULL"}");
        if (s.HasResolver) sb.AppendLineLf($"Resolver result at plan time: {s.ResolverResult ?? "NULL"}");
        if (s.HashAfter != null) sb.AppendLineLf($"Shape hash afterwards: {s.HashAfter[..Math.Min(16, s.HashAfter.Length)]}…");
        if (s.Expect != null) sb.AppendLineLf($"Expect: {s.Expect}");
        sb.AppendLineLf();
        sb.AppendLineLf(s.Text);
        if (s.ResolverText != null) { sb.AppendLineLf(); sb.AppendLineLf("-- resolver"); sb.AppendLineLf(s.ResolverText); }
        return sb.ToString();
    }

    /// <summary>The plan files of a project for a target, newest first.</summary>
    public static IReadOnlyList<string> Find(string projectRoot, string? target = null)
    {
        var root = System.IO.Path.Combine(projectRoot, "plans");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.plan.yml", SearchOption.AllDirectories)
            .Where(f => target == null || System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f)) == target)
            .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).ToList();
    }
}
