using System.Runtime.InteropServices;
using DbDataKit.Cli;
using DbDataSync.Libraries;
using DbDataSync.TaskRunner.Runner;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync package publish --to runner</c> (plan 28c E08): the repository's enabled replications as a definition package for a runner. <see cref="ReplicationSetPackage"/> makes the package (and refuses, naming the file, what would
/// fail at its first run: a bad schedule, a mapping with no column cache); <see cref="DriverLibrariesPart"/> restores the libraries and compiled drivers for the platform the runner is on. <c>--out</c> writes the package
/// (<c>package.yaml</c> and <c>files/</c>) and sends nothing. The package holds no secret: a connection's login is a slot, bound in a channel on the Runner. <c>--check</c> builds the manifest and says what the package would hold (the
/// replications, their tasks and slots) or what refuses it, restoring no library and contacting nothing; <c>--json</c> prints the outcome as one JSON object on stdout (the Workspace's sync project runs this verb with it and relays
/// the words of a refusal unchanged). Sending the package to a runner is the console's deploy: <c>--server</c> (and <c>--code</c>) answer the sentence that says so and exit 2.
/// </summary>
public static class PackageCommand
{
    /// <summary>The words a person typed for the removed form: the package sent to a runner from the command line.</summary>
    public const string SendToRunner = "dbdatasync package publish --to runner --server";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "publish") { Console.Error.WriteLine("Usage: package publish --to runner ..."); return 2; }
        var rest = args[1..];
        if (CliOptions.Read(rest, "--server") is not null || CliOptions.Read(rest, "--code") is not null)
        {
            Console.Error.WriteLine(RemovedCommands.PublishingGone(SendToRunner));
            return 2;
        }
        var repoRoot = DbDataSyncRoot.Resolve(rest);
        if (CliOptions.Read(rest, "--to") != "runner") { Console.Error.WriteLine("--to is `runner`, the only place a package is made for."); return 2; }
        var output = CliOptions.Read(rest, "--out");
        var json = CliOptions.Has(rest, "--json");
        var check = CliOptions.Has(rest, "--check");
        if (check && output is not null) { Console.Error.WriteLine("--check builds the package and writes nothing: give it without --out."); return 2; }
        if (output is null && !check) { Console.Error.WriteLine("Give --out <directory> to write the package, or --check to say what it would hold; the package reaches a runner through the console's deploy."); return 2; }

        var platform = CliOptions.Read(rest, "--platform") ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64");
        var package = CliOptions.Read(rest, "--package") ?? ReplicationSetPackage.NameOf(Path.GetFileName(Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var version = typeof(PackageCommand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0";
        var problems = new List<string>();
        var built = ReplicationSetPackage.Build(repoRoot, package, CliOptions.Read(rest, "--runtime-range") ?? ReplicationSetPackage.RuntimeRange(version), problems);
        if (built is null)
        {
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            if (!json) Console.WriteLine("Nothing was written.");
            return 1;
        }

        if (check)
        {
            if (json) Console.WriteLine(Result(package, "check", built));
            else Console.WriteLine($"The package {package} would hold {built.Replications.Count} task(s) and {built.Files.Count} file(s): {string.Join(", ", built.Replications.Select(r => r.Task))}; nothing was restored or written.");
            return 0;
        }

        var part = Directory.CreateTempSubdirectory("dbdatasync-part-").FullName;
        try
        {
            try { built = await ReplicationSetPackage.AddLibrariesAsync(built, repoRoot, Path.Combine(part, "part"), platform, CliOptions.Read(rest, "--source")); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                if (!json) Console.WriteLine("Nothing was written.");
                return 1;
            }

            ReplicationSetPackage.Write(built, Path.GetFullPath(output!));
            if (json) Console.WriteLine(Result(package, "out", built));
            else Console.WriteLine($"Wrote the package {package} ({built.Replications.Count} task(s), {built.Files.Count} file(s), for {platform}) to {Path.GetFullPath(output!)}; nothing was sent.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(part, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
    }

    /// <summary>The outcome as one JSON line: what the package holds (<c>check</c>) or was written (<c>out</c>) and, for each replication, the task, schedule and slots it became.</summary>
    private static string Result(string package, string mode, BuiltReplicationSet built) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            package,
            mode,
            files = built.Files.Count,
            replications = built.Replications.Select(r => new { replication = r.Replication, task = r.Task, mode = r.Mode, cron = r.Cron, frequencySeconds = r.FrequencySeconds, slots = r.Slots, mappings = r.Mappings, slotConnections = r.SlotConnections }),
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
}
