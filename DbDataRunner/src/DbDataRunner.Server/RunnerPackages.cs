using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DbDataKit.Config.Yaml;
using DbDataKit.Runner;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataRunner.Server;

/// <summary>A package could not be put into the runner; the message says why (and names the file and line for a manifest problem).</summary>
public sealed class PackageInstallException(string message) : InvalidOperationException(message);

/// <summary>What <see cref="RunnerPackages.Install"/> did.</summary>
public sealed record PackageInstalled(string Package, string Channel, int Version, bool Changed, int Files, long Bytes, IReadOnlyList<string> Tasks);

/// <summary>
/// How packages get into a runner in this slice (plan 28a R10): the kind <c>integration</c> of the definitions registry (plan 30), checked by the package manifest's own validator (R01), and <see cref="Install"/>, a development
/// command's whole job. Plan 30's publish API (R16) replaces the command; what it leaves behind is the same: a version of the definition <c>integration/&lt;package&gt;</c> whose files are payloads, active on the channel, and
/// the <c>task_projection</c> rows the scheduler reads.
/// </summary>
public static class RunnerPackages
{
    public const string Kind = DefinitionVersionSource.Kind;

    public const string DevChannel = "dev";

    /// <summary>The registry a runner accepts: <c>integration</c>, staged by default (plan 30 section 3.1: runner kinds stage and activate by themselves once every runner is ready; <c>serve</c> stays selectable), checked by <see cref="Check"/>.</summary>
    public static DefinitionKinds Kinds() => new DefinitionKinds().Register(new DefinitionKind(Kind, ["runner"], PublishMode.Stage) { Validator = candidate => Check(candidate), SlotsOf = SlotsOfManifest });

    /// <summary>The connection slots the manifest's tasks declare: each needs a binding in a channel before the version serves there (plan 30 section 3.2).</summary>
    private static IReadOnlyList<DefinitionSlot> SlotsOfManifest(string manifest) =>
        PackageManifestReader.Load(manifest, "manifest") is { Ok: true, Manifest: { } m }
            ? [.. m.Tasks.SelectMany(t => t.Connections).Select(c => c.Name).Distinct(StringComparer.Ordinal).Select(n => new DefinitionSlot(n))]
            : [];

    /// <summary>The validator of the kind: the manifest loads without errors, names the definition, and its launch file is among the files; null when it does, the problems in words otherwise.</summary>
    public static string? Check(DefinitionCandidate candidate)
    {
        var load = PackageManifestReader.Load(candidate.Manifest, $"{candidate.Name}/package.yaml");
        if (!load.Ok) return string.Join(" ", load.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString()));
        var manifest = load.Manifest!;
        if (manifest.Name != candidate.Name) return $"the manifest is for the package '{manifest.Name}', not '{candidate.Name}'.";
        if (manifest.Launch.Kind == LaunchKind.Assembly && manifest.Launch.Assembly is { } assembly && candidate.Artifacts.All(a => a.Path != assembly.Replace('\\', '/')))
            return $"the launch assembly '{assembly}' is not among the package's files.";
        // an executable's command is a path inside the package (it has a directory part and is relative) or a program on the runner; only the first must be among the files
        if (manifest.Launch.Kind == LaunchKind.Executable && manifest.Launch.Command is { } command)
        {
            var path = command.Replace('\\', '/');
            if (path.Contains('/') && !path.StartsWith('/') && candidate.Artifacts.All(a => a.Path != path))
                return $"the launch command '{command}' is not among the package's files.";
        }

        return RunnerContracts.Check(manifest.BuiltAgainst);
    }

    /// <summary>
    /// The development path: reads the manifest file and the files of <paramref name="directory"/> and publishes them to this runner's own state through the same service layer the publish API uses
    /// (<see cref="RunnerPublishing"/>), served at once on <paramref name="channel"/>, in process and without HTTP or pairing. Installing the same content again changes nothing.
    /// Throws <see cref="PackageInstallException"/> for what is wrong with the manifest or the files.
    /// </summary>
    public static PackageInstalled Install(StateDatabase state, string manifestPath, string directory, string channel, string by)
    {
        if (!File.Exists(manifestPath)) throw new PackageInstallException($"The manifest '{manifestPath}' does not exist.");
        if (!Directory.Exists(directory)) throw new PackageInstallException($"The package directory '{directory}' does not exist.");
        if (!PackageManifestValidator.NameRule.IsMatch(channel)) throw new PackageInstallException($"'{channel}' is not a channel name: use 1 to 64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");

        var text = File.ReadAllText(manifestPath);
        var load = PackageManifestReader.Load(text, manifestPath);
        if (!load.Ok) throw new PackageInstallException(string.Join(Environment.NewLine, load.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString())));
        var manifest = load.Manifest!;

        var service = new RunnerPublishing(state);
        var actor = new RunnerActor(by, by, "command");
        var manifestFile = Path.GetFullPath(manifestPath);
        var files = new List<PublishFile>();
        long bytes = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                // the manifest travels as the version's content, not as one of its files, when it is kept in the package's own directory
                if (Path.GetFullPath(path) == manifestFile) continue;
                var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                string sha;
                long size;
                using (var stream = File.OpenRead(path))
                {
                    size = stream.Length;
                    sha = Convert.ToHexStringLower(SHA256.HashData(stream));
                }

                if (!service.HasArtifact(sha))
                {
                    using var stream = File.OpenRead(path);
                    service.StoreArtifactAsync(sha, stream, null, actor, audit: false).GetAwaiter().GetResult();
                }

                files.Add(new PublishFile(relative, sha, size, null));
                bytes += size;
            }

            var published = service.Publish(new PublishInput(manifest.Name, text, files, channel, PublishMode.Serve, Note: "dbdatarunner package install (development)"), actor, RunnerPublishing.InstallVerb);
            return new PackageInstalled(manifest.Name, channel, published.Version, published.Changed, files.Count, bytes, published.Tasks);
        }
        catch (RunnerPublishException ex)
        {
            throw new PackageInstallException(ex.Message);
        }
    }
}
