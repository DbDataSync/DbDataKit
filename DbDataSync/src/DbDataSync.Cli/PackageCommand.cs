using System.Runtime.InteropServices;
using DbDataKit.Runner.Client;
using DbDataSync.Libraries;
using DbDataSync.TaskRunner.Runner;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync package publish --to runner</c> (plan 28c E08): the repository's enabled replications as a definition package on a runner. <see cref="ReplicationSetPackage"/> makes the package (and refuses, naming the file, what would
/// fail at its first run: a bad schedule, a mapping with no column cache); <see cref="DriverLibrariesPart"/> restores the libraries and compiled drivers for the platform the runner is on; the shared <see cref="RunnerPublishClient"/> sends
/// only the files the runner does not have and stages the version, which the runners of the group prepare and activate once every one of them has it ready. <c>--out</c> writes the package and sends nothing. The package holds
/// no secret: a connection's login is a slot, filled with <c>--bind slot=connection</c> in the channel.
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
        if (output is null && (server is null || channel is null)) { Console.Error.WriteLine("Name the runner and the channel: --server <name or https address> --channel <name> (or --out <directory> to write the package and send nothing)."); return 2; }
        if (output is not null && server is not null) { Console.Error.WriteLine("--out writes the package and sends nothing: give it or --server, not both."); return 2; }

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
        var built = ReplicationSetPackage.Build(repoRoot, package, CliOptions.Read(rest, "--runtime-range") ?? ReplicationSetPackage.RuntimeRange(version), problems);
        if (built is null)
        {
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            Console.WriteLine("Nothing was sent.");
            return 1;
        }

        var part = Directory.CreateTempSubdirectory("dbdatasync-part-").FullName;
        var directory = Directory.CreateTempSubdirectory("dbdatasync-publish-").FullName;
        try
        {
            try { built = await ReplicationSetPackage.AddLibrariesAsync(built, repoRoot, Path.Combine(part, "part"), platform, CliOptions.Read(rest, "--source")); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                Console.WriteLine("Nothing was sent.");
                return 1;
            }

            if (output is not null)
            {
                ReplicationSetPackage.Write(built, Path.GetFullPath(output));
                Console.WriteLine($"Wrote the package {package} ({built.Replications.Count} task(s), {built.Files.Count} file(s), for {platform}) to {Path.GetFullPath(output)}; nothing was sent.");
                return 0;
            }

            Func<string, string?> environment = Environment.GetEnvironmentVariable;
            var client = new RunnerPublishClient(new RunnerClientStore(CliOptions.Read(rest, "--client-dir") ?? RunnerClientStore.DefaultDirectory(environment)), environment);
            var code = CliOptions.Read(rest, "--code");
            try
            {
                if (bindings.Count > 0)
                {
                    await client.BindAsync(server!, code, package, channel!, bindings, null, Console.WriteLine);
                    Console.WriteLine($"Bound {string.Join(", ", bindings.Keys.Order(StringComparer.Ordinal))} in {channel}.");
                }

                ReplicationSetPackage.Write(built, directory);
                int? expected = int.TryParse(CliOptions.Read(rest, "--expected-version"), out var e) ? e : null;
                var serve = CliOptions.Has(rest, "--serve");
                var result = await client.PublishAsync(server!, code, Path.Combine(directory, "package.yaml"), Path.Combine(directory, "files"), channel!, stage: !serve, expected, Console.WriteLine);
                Console.WriteLine($"{(result.Mode == "stage" ? "Staged" : "Published")} {package} on {result.Channel}: version {result.Version}{(result.Changed ? "" : " (the same content as before, nothing changed)")}, {result.Uploaded} file(s) uploaded, {result.Skipped} already there."
                    + (result.Mode == "stage" ? " Runners prepare it and activate it once every runner of the group has it ready." : ""));
                return 0;
            }
            catch (RunnerClientException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.WriteLine("Nothing more was done.");
                return 1;
            }
        }
        finally
        {
            foreach (var d in new[] { part, directory })
                try { Directory.Delete(d, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
        }
    }
}
