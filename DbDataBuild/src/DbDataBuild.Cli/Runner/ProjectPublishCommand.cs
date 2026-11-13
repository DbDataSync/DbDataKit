using DbDataBuild.Core;
using DbDataKit.Runner.Client;

namespace DbDataBuild.Cli.Runner;

/// <summary>What `project publish` was asked.</summary>
internal sealed record PublishOptions(string To, string? Server, string? Code, string? Channel, string? Package, bool Serve, int? ExpectedVersion, IReadOnlyList<string> Bind,
    string? PromoteFrom, string? RuntimeRange, string? ClientDir, string? Out, bool RunnerSchedules = false, bool Operations = false);

/// <summary>
/// `dbdatabuild project publish --to gateway --out` (plan 33 SC20e) writes the models and data sources a Gateway publish would send, with no login (<see cref="ProjectPublishGateway"/>).
/// `dbdatabuild project publish --to runner` (plan 28c E07): the committed files of the project as a definition package on a runner. Nothing is compiled here (see <see cref="RunnerProject"/>); the package is sent by the shared publishing
/// client (<see cref="RunnerPublishClient"/>: a file the runner has is not sent again, the version is staged and the runners of the group prepare it). A channel's connection slots are its own, so `--bind` fills them first, and
/// `--promote-from` moves the version one channel serves to another (the runner's `switch`, an administrator's call) instead of publishing.
/// </summary>
internal static class ProjectPublishCommand
{
    public static int Run(CommandSpec spec, string root, PublishOptions o, TextWriter output, TextWriter error, Func<string, string?> environment)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (o.To == "gateway")
        {
            if (o.Server is not null || o.Channel is not null || o.PromoteFrom is not null || o.Serve) { error.WriteLine("--to gateway only writes (--out <directory>); --server, --channel, --serve and --promote-from belong to --to runner."); return CliApp.ExitUsage; }
            return ProjectPublishGateway.Run(spec, root, o.Out, output, error);
        }
        if (o.To != "runner") { error.WriteLine($"--to is `runner` or `gateway`, not `{o.To}`."); return CliApp.ExitUsage; }
        if (o.Operations && (o.RunnerSchedules || o.PromoteFrom is not null)) { error.WriteLine("--operations publishes the manual-only operations package; --runner-schedules and --promote-from belong to the refresh package."); return CliApp.ExitUsage; }
        var package = o.Package ?? (o.Operations ? BuildOperations.PackageOf(Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) : RunnerProject.DefaultPackageName(root));
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in o.Bind)
        {
            var eq = text.IndexOf('=');
            if (eq <= 0 || eq == text.Length - 1) { error.WriteLine($"--bind is `slot=connection`, not `{text}`."); return CliApp.ExitUsage; }
            bindings[text[..eq]] = text[(eq + 1)..];
        }

        if (o.PromoteFrom is null && o.Out is not null && o.Server is not null) { error.WriteLine("--out writes the package and sends nothing: give it or --server, not both."); return CliApp.ExitUsage; }
        var sending = o.Out is null;
        if (sending && (o.Server is null || o.Channel is null)) { error.WriteLine("Name the runner and the channel: --server <name or https address> --channel <name> (or --out <directory> to write the package and send nothing)."); return CliApp.ExitUsage; }

        var client = new RunnerPublishClient(new RunnerClientStore(o.ClientDir ?? RunnerClientStore.DefaultDirectory(environment)), environment);
        try
        {
            if (o.PromoteFrom is { } from)
            {
                if (o.Server is null || o.Channel is null) { error.WriteLine("Promotion needs --server and --channel (the channel the version goes to)."); return CliApp.ExitUsage; }
                if (from == o.Channel) { error.WriteLine("--promote-from and --channel name the same channel."); return CliApp.ExitUsage; }
                if (bindings.Count > 0) { client.BindAsync(o.Server, o.Code, package, o.Channel, bindings, null, l => output.WriteLine(l)).GetAwaiter().GetResult(); output.WriteLine($"Bound {string.Join(", ", bindings.Keys.Order(StringComparer.Ordinal))} in {o.Channel}."); }
                var promoted = client.PromoteAsync(o.Server, o.Code, package, from, o.Channel, l => output.WriteLine(l)).GetAwaiter().GetResult();
                output.WriteLine($"Promoted {package} version {promoted.Version} from {from} to {o.Channel}: {promoted.State}. {promoted.Message}".TrimEnd());
                output.Payload("package", package);
                output.Payload("channel", o.Channel);
                output.Payload("version", promoted.Version);
                output.Payload("promoted_from", from);
                output.Payload("bound", bindings.Keys.Order(StringComparer.Ordinal).ToList());
                return CliApp.ExitOk;
            }

            var diagnostics = new List<Diagnostic>();
            var range = o.RuntimeRange ?? RunnerProject.RuntimeRange(ProductInfo.Version);
            var built = o.Operations ? OperationsPackage.Build(root, package, range, diagnostics) : RunnerProject.Build(root, package, range, diagnostics);
            if (built is null)
            {
                foreach (var d in diagnostics) error.Diag(d);
                output.WriteLine("Nothing was sent.");
                output.Next("project compile");
                return CliApp.ExitFindings;
            }

            // the operations package has no plan: its tasks are the manual-only writes, each with every connection of the project as a slot
            var tasks = o.Operations
                ? built.Manifest.Tasks.Select(t => new { name = t.Name, connection = (string?)null, plan = (string?)null, schedule = (string?)null, time_zone = (string?)null, slots = t.Connections.Select(c => c.Name).ToList(), timeout_seconds = t.Limits.Timeout is { } limit ? (long?)limit.TotalSeconds : null }).ToList()
                : built.Plans.Select(p => new { name = p.Task, connection = (string?)p.Connection, plan = (string?)p.Plan, schedule = p.Cron, time_zone = p.TimeZone, slots = p.Slots.ToList(), timeout_seconds = p.Timeout is { } t ? (long?)t.TotalSeconds : null }).ToList();
            output.Payload("package", package);
            output.Payload("files", built.Files.Count);
            output.Payload("tasks", tasks);
            if (o.Out is not null)
            {
                RunnerProject.Write(built, Path.GetFullPath(o.Out));
                output.Payload("written_to", Path.GetFullPath(o.Out));
                output.WriteLine($"Wrote the package {package} ({(o.Operations ? built.Manifest.Tasks.Count : built.Plans.Count)} task(s), {built.Files.Count} file(s)) to {Path.GetFullPath(o.Out)}; nothing was sent.");
                return CliApp.ExitOk;
            }

            if (bindings.Count > 0)
            {
                client.BindAsync(o.Server!, o.Code, package, o.Channel!, bindings, null, l => output.WriteLine(l)).GetAwaiter().GetResult();
                output.WriteLine($"Bound {string.Join(", ", bindings.Keys.Order(StringComparer.Ordinal))} in {o.Channel}.");
            }

            // plan 28c E09, ED6: a published plan is scheduled by this project's Build server until it is cut over, so the runner is told so before the version can activate; a task the runner already has an owner for keeps it
            var handedBack = new List<string>();
            if (!o.RunnerSchedules)
            {
                foreach (var plan in built.Plans)
                {
                    var known = client.ScheduleOfAsync(o.Server!, o.Code, package, o.Channel!, plan.Task, null).GetAwaiter().GetResult();
                    if (known.OwnerSetBy is not null) continue;
                    try { client.SetOwnerAsync(o.Server!, o.Code, package, o.Channel!, plan.Task, DbDataKit.State.TaskOwners.Old, "published by `project publish`: the runner does not schedule it until `server schedule cutover`", null).GetAwaiter().GetResult(); }
                    catch (RunnerClientException ex)
                    {
                        error.WriteLine($"{ex.Message}\nThe runner is told that the plans stay with the Build server's scheduler until they are cut over, which takes the operator role of this pairing. Pair with a code that allows it, or pass --runner-schedules to let the runner schedule the plans from the first activation.");
                        output.WriteLine("Nothing was sent.");
                        return CliApp.ExitFindings;
                    }

                    handedBack.Add(plan.Task);
                }
            }

            output.Payload("owner_old", handedBack);
            var directory = Directory.CreateTempSubdirectory("ddb-publish-").FullName;
            try
            {
                RunnerProject.Write(built, directory);
                var result = client.PublishAsync(o.Server!, o.Code, Path.Combine(directory, "package.yaml"), Path.Combine(directory, "files"), o.Channel!, stage: !o.Serve, o.ExpectedVersion, l => output.WriteLine(l)).GetAwaiter().GetResult();
                output.Payload("channel", result.Channel);
                output.Payload("mode", result.Mode);
                output.Payload("version", result.Version);
                output.Payload("changed", result.Changed);
                output.Payload("uploaded", result.Uploaded);
                output.Payload("skipped", result.Skipped);
                output.Payload("signature", result.Signature);
                output.Payload("bound", bindings.Keys.Order(StringComparer.Ordinal).ToList());
                output.WriteLine($"{(result.Mode == "stage" ? "Staged" : "Published")} {package} on {result.Channel}: version {result.Version}{(result.Changed ? "" : " (the same content as before, nothing changed)")}, {result.Uploaded} file(s) uploaded, {result.Skipped} already on the runner; {built.Plans.Count} task(s): {string.Join(", ", built.Plans.Select(p => p.Task))}."
                    + (result.Mode == "stage" ? " Runners prepare it and activate it once every runner of the group has it ready." : ""));
                return CliApp.ExitOk;
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
            }
        }
        catch (RunnerClientException ex)
        {
            error.WriteLine(ex.Message);
            output.WriteLine("Nothing more was done.");
            return CliApp.ExitFindings;
        }
    }
}
