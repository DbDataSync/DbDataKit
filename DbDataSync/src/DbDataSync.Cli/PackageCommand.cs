using System.Runtime.InteropServices;
using DbDataKit.Runner.Client;
using DbDataSync.Libraries;
using DbDataSync.TaskRunner.Runner;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync package publish --to runner</c> (plan 28c E08): the repository's enabled replications as a definition package on a runner. <see cref="ReplicationSetPackage"/> makes the package (and refuses, naming the file, what would
/// fail at its first run: a bad schedule, a mapping with no column cache); <see cref="DriverLibrariesPart"/> restores the libraries and compiled drivers for the platform the runner is on; the shared <see cref="RunnerPublishClient"/> sends
/// only the files the runner does not have and stages the version, which the runners of the group prepare and activate once every one of them has it ready. <c>--out</c> writes the package and sends nothing. The package holds
/// no secret: a connection's login is a slot, filled with <c>--bind slot=connection</c> in the channel. <c>--check</c> builds the manifest and says what would be published (the replications, their tasks and slots) or what refuses it,
/// restoring no library and contacting nothing; <c>--json</c> prints the outcome as one JSON object on stdout (the Sync API's publish action runs this verb with it and relays the words of a refusal unchanged).
/// </summary>
public static class PackageCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "publish") { Console.Error.WriteLine("Usage: package publish --to runner ..."); return 2; }
        var rest = args[1..];
        var repoRoot = DbDataSyncRoot.Resolve(rest);
        if (CliOptions.Read(rest, "--to") != "runner") { Console.Error.WriteLine("--to is `runner`, the only place a package is published to."); return 2; }
        var server = CliOptions.Read(rest, "--server");
        var channel = CliOptions.Read(rest, "--channel");
        var output = CliOptions.Read(rest, "--out");
        var json = CliOptions.Has(rest, "--json");
        var check = CliOptions.Has(rest, "--check");
        if (check && (output is not null || server is not null)) { Console.Error.WriteLine("--check builds the package and sends nothing: give it without --out and --server."); return 2; }
        if (output is null && !check && (server is null || channel is null)) { Console.Error.WriteLine("Name the runner and the channel: --server <name or https address> --channel <name> (or --out <directory> to write the package and send nothing)."); return 2; }
        if (output is not null && server is not null) { Console.Error.WriteLine("--out writes the package and sends nothing: give it or --server, not both."); return 2; }
        Action<string> log = json ? _ => { } : Console.WriteLine;

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Length - 1; i++)
        {
            if (rest[i] != "--bind") continue;
            var text = rest[i + 1];
            var eq = text.IndexOf('=');
            if (eq <= 0 || eq == text.Length - 1) { Console.Error.WriteLine($"--bind is `slot=connection`, not `{text}`."); return 2; }
            bindings[text[..eq]] = text[(eq + 1)..];
        }

        var platform = CliOptions.Read(rest, "--platform") ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64");
        var package = CliOptions.Read(rest, "--package") ?? ReplicationSetPackage.NameOf(Path.GetFileName(Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var version = typeof(PackageCommand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0";
        var problems = new List<string>();
        // what the working tree holds now: taken before the package is built, so an edit made while it is sent counts as unpublished (plan 28c E12g)
        var digest = check || output is not null ? null : DbDataSync.Libraries.DefinitionDigest.Of(repoRoot);
        var built = ReplicationSetPackage.Build(repoRoot, package, CliOptions.Read(rest, "--runtime-range") ?? ReplicationSetPackage.RuntimeRange(version), problems);
        if (built is null)
        {
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            if (!json) Console.WriteLine("Nothing was sent.");
            return 1;
        }

        if (check)
        {
            if (json) Console.WriteLine(Result(package, channel, "check", built, null, null));
            else Console.WriteLine($"The package {package} would hold {built.Replications.Count} task(s) and {built.Files.Count} file(s): {string.Join(", ", built.Replications.Select(r => r.Task))}; nothing was restored or sent.");
            return 0;
        }

        var part = Directory.CreateTempSubdirectory("dbdatasync-part-").FullName;
        var directory = Directory.CreateTempSubdirectory("dbdatasync-publish-").FullName;
        try
        {
            try { built = await ReplicationSetPackage.AddLibrariesAsync(built, repoRoot, Path.Combine(part, "part"), platform, CliOptions.Read(rest, "--source")); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                if (!json) Console.WriteLine("Nothing was sent.");
                return 1;
            }

            if (output is not null)
            {
                ReplicationSetPackage.Write(built, Path.GetFullPath(output));
                if (json) Console.WriteLine(Result(package, channel, "out", built, null, null));
                else Console.WriteLine($"Wrote the package {package} ({built.Replications.Count} task(s), {built.Files.Count} file(s), for {platform}) to {Path.GetFullPath(output)}; nothing was sent.");
                return 0;
            }

            Func<string, string?> environment = Environment.GetEnvironmentVariable;
            var client = new RunnerPublishClient(new RunnerClientStore(CliOptions.Read(rest, "--client-dir") ?? RunnerClientStore.DefaultDirectory(environment)), environment);
            var code = CliOptions.Read(rest, "--code");
            try
            {
                if (bindings.Count > 0)
                {
                    await client.BindAsync(server!, code, package, channel!, bindings, null, log);
                    log($"Bound {string.Join(", ", bindings.Keys.Order(StringComparer.Ordinal))} in {channel}.");
                }

                // plan 28c E09, ED6 (and E12e): a published replication is scheduled by this server until its owner is moved, so the runner is told so before the version can activate; a task the runner already has an owner for keeps it
                var handedBack = new List<string>();
                if (!CliOptions.Has(rest, "--runner-schedules"))
                {
                    foreach (var replication in built.Replications)
                    {
                        var known = await client.ScheduleOfAsync(server!, code, package, channel!, replication.Task, null);
                        if (known.OwnerSetBy is not null) continue;
                        try { await client.SetOwnerAsync(server!, code, package, channel!, replication.Task, DbDataKit.State.TaskOwners.Old, "published by `package publish`: the runner does not schedule it until its owner is moved to the runner", null); }
                        catch (RunnerClientException ex)
                        {
                            Console.Error.WriteLine($"{ex.Message}\nThe runner is told that the replications stay with this server's scheduler until their owner is moved, which takes the operator role of this pairing. Pair with a code that allows it, or pass --runner-schedules to let the runner schedule the replications from the first activation.");
                            if (!json) Console.WriteLine("Nothing was sent.");
                            return 1;
                        }

                        handedBack.Add(replication.Task);
                    }

                    if (handedBack.Count > 0) (json ? Console.Error : Console.Out).WriteLine($"The runner leaves {string.Join(", ", handedBack)} to this server until the owner is moved (owner old).");
                }

                ReplicationSetPackage.Write(built, directory);
                int? expected = int.TryParse(CliOptions.Read(rest, "--expected-version"), out var e) ? e : null;
                var serve = CliOptions.Has(rest, "--serve");
                var result = await client.PublishAsync(server!, code, Path.Combine(directory, "package.yaml"), Path.Combine(directory, "files"), channel!, stage: !serve, expected, log);
                try { DbDataSync.Libraries.PublishedDefinition.Record(repoRoot, new DbDataSync.Libraries.PublishedMarker(package, channel!, server!, result.Version, result.Mode, DateTimeOffset.UtcNow, digest!)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Published, but the record of what was published could not be written ({ex.Message}); the screens will show unpublished changes until the next publish."); }
                if (json) Console.WriteLine(Result(package, channel, result.Mode, built, result, bindings.Keys));
                else Console.WriteLine($"{(result.Mode == "stage" ? "Staged" : "Published")} {package} on {result.Channel}: version {result.Version}{(result.Changed ? "" : " (the same content as before, nothing changed)")}, {result.Uploaded} file(s) uploaded, {result.Skipped} already there."
                    + (result.Mode == "stage" ? " Runners prepare it and activate it once every runner of the group has it ready." : "")
                    + $" A replication runs on the runner when its owner is `runner` (a continuous one as a drain that stays up and plans on the runner; a periodic one as a drain per cron time): observe or cut it over with `PUT /api/replications/<name>/owner` or on its Runner tab, and until then this server's scheduler and workers run it.");
                return 0;
            }
            catch (RunnerClientException ex)
            {
                Console.Error.WriteLine(ex.Message);
                if (!json) Console.WriteLine("Nothing more was done.");
                return 1;
            }
        }
        finally
        {
            foreach (var d in new[] { part, directory })
                try { Directory.Delete(d, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
    }

    /// <summary>The outcome as one JSON line: what was published (or only built, with <c>check</c> and <c>out</c>), the version the runner gave it and, for each replication, the task, schedule and slots it became.</summary>
    private static string Result(string package, string? channel, string mode, BuiltReplicationSet built, ClientPublished? published, IEnumerable<string>? bound) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            package,
            channel = published?.Channel ?? channel,
            mode,
            version = published?.Version,
            changed = published?.Changed,
            uploaded = published?.Uploaded,
            skipped = published?.Skipped,
            files = built.Files.Count,
            bound = bound?.Order(StringComparer.Ordinal).ToArray(),
            replications = built.Replications.Select(r => new { replication = r.Replication, task = r.Task, mode = r.Mode, cron = r.Cron, frequencySeconds = r.FrequencySeconds, slots = r.Slots, mappings = r.Mappings, slotConnections = r.SlotConnections }),
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
}
