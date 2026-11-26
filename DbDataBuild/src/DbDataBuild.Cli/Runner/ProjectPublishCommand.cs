using DbDataBuild.Core;
using DbDataKit.Cli;

namespace DbDataBuild.Cli.Runner;

/// <summary>What `project publish` was asked.</summary>
internal sealed record PublishOptions(string To, string? Package, string? RuntimeRange, string? Out, bool RunnerSchedules = false, bool Operations = false);

/// <summary>
/// `dbdatabuild project publish --to gateway --out` writes the models and data sources a Gateway publish would send, with no login (<see cref="ProjectPublishGateway"/>).
/// `dbdatabuild project publish --to runner --out` writes the committed files of the project as a definition package (`package.yaml` and `files/`). Nothing is compiled here (see <see cref="RunnerProject"/>) and nothing is sent:
/// the package reaches a runner through the console's deploy (a Workspace build project's publish, or "Publish a package from files" on the Runner's Packages screen). The options that sent it (<see cref="SendingOptions"/>)
/// answer the sentence that says so, before the command is parsed.
/// </summary>
internal static class ProjectPublishCommand
{
    /// <summary>The options of the form that sent the package to a runner (paired it, bound a channel's slots, staged or served the version, or promoted one): each answers <see cref="RemovedCommands.ConsoleDeploys"/>.</summary>
    public static readonly IReadOnlyList<string> SendingOptions = ["--server", "--code", "--promote-from", "--channel", "--bind", "--serve", "--expected-version", "--client-dir"];

    /// <summary>The answer of `project publish` given an option of <see cref="SendingOptions"/>: <c>'dbdatabuild project publish --to runner --server' is gone: ...</c>, naming the first such option given; null otherwise.</summary>
    public static string? SendingGone(string[] args)
    {
        var words = args.Where(a => !a.StartsWith('-')).Take(2).ToArray();
        if (words is not ["project", "publish"]) return null;
        var given = args.FirstOrDefault(a => SendingOptions.Any(o => a == o || a.StartsWith(o + "=", StringComparison.Ordinal)));
        if (given is null) return null;
        var to = "runner";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--to" && i + 1 < args.Length) to = args[i + 1];
            else if (args[i].StartsWith("--to=", StringComparison.Ordinal)) to = args[i]["--to=".Length..];
        }

        return RemovedCommands.PublishingGone($"dbdatabuild project publish --to {to} {given.Split('=')[0]}");
    }

    public static int Run(CommandSpec spec, string root, PublishOptions o, TextWriter output, TextWriter error, Func<string, string?> environment)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (o.To == "gateway") return ProjectPublishGateway.Run(spec, root, o.Out, output, error);
        if (o.To != "runner") { error.WriteLine($"--to is `runner` or `gateway`, not `{o.To}`."); return CliApp.ExitUsage; }
        if (o.Operations && o.RunnerSchedules) { error.WriteLine("--operations writes the manual-only operations package; --runner-schedules belongs to the refresh package."); return CliApp.ExitUsage; }
        if (o.Out is null) { error.WriteLine("Give --out <directory> to write the package; it reaches a runner through the console's deploy."); return CliApp.ExitUsage; }
        var package = o.Package ?? (o.Operations ? BuildOperations.PackageOf(Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) : RunnerProject.DefaultPackageName(root));

        var diagnostics = new List<Diagnostic>();
        var range = o.RuntimeRange ?? RunnerProject.RuntimeRange(ProductInfo.Version);
        var built = o.Operations ? OperationsPackage.Build(root, package, range, diagnostics) : RunnerProject.Build(root, package, range, diagnostics);
        if (built is null)
        {
            foreach (var d in diagnostics) error.Diag(d);
            output.WriteLine("Nothing was written.");
            output.Next("project compile");
            return CliApp.ExitFindings;
        }

        // the operations package has no plan: its tasks are the manual-only writes, each with every connection of the project as a slot
        var tasks = o.Operations
            ? built.Manifest.Tasks.Select(t => new { name = t.Name, connection = (string?)null, plan = (string?)null, schedule = (string?)null, time_zone = (string?)null, slots = t.Connections.Select(c => c.Name).ToList(), timeout_seconds = t.Limits.Timeout is { } limit ? (long?)limit.TotalSeconds : null }).ToList()
            : built.Plans.Select(p => new { name = p.Task, connection = (string?)p.Connection, plan = (string?)p.Plan, schedule = p.Cron, time_zone = p.TimeZone, slots = p.Slots.ToList(), timeout_seconds = p.Timeout is { } t ? (long?)t.TotalSeconds : null }).ToList();
        if (built.LineageProblem is { } lineageProblem) error.WriteLine(lineageProblem);
        output.Payload("package", package);
        output.Payload("files", built.Files.Count);
        output.Payload("tasks", tasks);
        RunnerProject.Write(built, Path.GetFullPath(o.Out));
        output.Payload("written_to", Path.GetFullPath(o.Out));
        output.WriteLine($"Wrote the package {package} ({(o.Operations ? built.Manifest.Tasks.Count : built.Plans.Count)} task(s), {built.Files.Count} file(s)) to {Path.GetFullPath(o.Out)}; nothing was sent.");
        return CliApp.ExitOk;
    }
}
