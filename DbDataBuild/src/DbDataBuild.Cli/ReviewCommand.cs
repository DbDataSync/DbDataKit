using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project show plan` (DESIGN.md 9.7). Effect class: offline. Reads plan files the way `apply` does (a plan carries a hash of its own content, and an edited one does not parse) and shows them: with no
/// argument the plans of the project, newest first; with a plan file its steps with risk, reasons, parameters and exact statements, the report, and what applying it would need to be allowed.
/// It connects to nothing and changes nothing: it is how a terminal, a page or an agent looks at a plan before a person decides. It never applies one.
/// </summary>
internal static class ReviewCommand
{
    public static int Run(CommandSpec spec, string projectRoot, string? planFile, string? target, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (target != null && CommandTargets.NamesOf(projectRoot) is var names && !names.Contains(target)) { error.WriteLine($"Unknown connection `{target}`. One of: {string.Join(", ", names)}."); return CliApp.ExitUsage; }
        return planFile == null ? List(projectRoot, target, output) : Show(projectRoot, planFile, output, error);
    }

    private static int List(string projectRoot, string? target, TextWriter output)
    {
        var rows = new List<object>();
        foreach (var path in PlanBrowser.Find(projectRoot, target))
        {
            var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
            var (browser, problems) = PlanBrowser.Load(path);
            rows.Add(browser != null
                ? new { path = relative, id = browser.Plan.Id, connection = browser.Plan.Connection, steps = browser.Plan.Steps.Count, risky = browser.Risky, destructive = browser.Destructive, intact = true, problems = Array.Empty<string>() }
                : new { path = relative, id = Path.GetFileName(path).Replace(".plan.yml", ""), connection = Path.GetFileName(Path.GetDirectoryName(path)) ?? "", steps = 0, risky = 0, destructive = 0, intact = false, problems = problems.Select(DiagnosticFormatter.Format).ToArray() });
            output.WriteLine(browser != null ? $"{relative}  {browser.Summary}" : $"{relative}  NOT INTACT: {problems.FirstOrDefault()?.Found}");
        }
        output.Payload("plans", rows);
        if (rows.Count == 0) output.WriteLine("No plans under plans/. `dbdatabuild connection deploy` writes one.");
        return CliApp.ExitOk;
    }

    private static int Show(string projectRoot, string planFile, TextWriter output, TextWriter error)
    {
        var path = Path.GetFullPath(Path.Combine(projectRoot, planFile));
        if (!path.EndsWith(".plan.yml", StringComparison.Ordinal) || !File.Exists(path)) { error.WriteLine($"`{planFile}` is not a plan file of this project (plans/<connection>/<id>.plan.yml)."); return CliApp.ExitUsage; }
        var (browser, problems) = PlanBrowser.Load(path);
        if (browser == null)
        {
            foreach (var d in problems) error.Diag(d);
            output.Payload("path", Path.GetRelativePath(projectRoot, path).Replace('\\', '/'));
            output.Payload("intact", false);
            output.WriteLine("This plan does not parse or its hash does not match: it was edited or damaged, and applying it would be refused. Make a new plan.");
            return CliApp.ExitFindings;
        }
        var plan = browser.Plan;
        output.Payload("path", Path.GetRelativePath(projectRoot, path).Replace('\\', '/'));
        output.Payload("intact", true);
        output.Payload("plan", plan);
        output.Payload("report", browser.Report);
        output.Payload("risky", browser.Risky);
        output.Payload("destructive", browser.Destructive);
        output.Payload("apply_needs", new { allow_risky = browser.Risky > 0, allow_destructive = browser.DestructiveObjects });
        output.WriteLine(browser.Summary);
        foreach (var s in plan.Steps) output.WriteLine(browser.Row(s));
        if (browser.Risky + browser.Destructive > 0)
            output.WriteLine($"Applying needs {(browser.Risky > 0 ? "--allow-risky" : "")}{(browser.Risky > 0 && browser.Destructive > 0 ? " and " : "")}{(browser.Destructive > 0 ? "--allow-destructive for: " + string.Join(", ", browser.DestructiveObjects) : "")}. That is a person's decision.");
        return CliApp.ExitOk;
    }
}
