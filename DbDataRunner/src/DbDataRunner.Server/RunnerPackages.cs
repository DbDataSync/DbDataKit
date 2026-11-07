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

    /// <summary>The registry a runner accepts: <c>integration</c>, served at once by default, checked by <see cref="Check"/>.</summary>
    public static DefinitionKinds Kinds() => new DefinitionKinds().Register(new DefinitionKind(Kind, ["runner"], PublishMode.Serve) { Validator = candidate => Check(candidate) });

    /// <summary>The validator of the kind: the manifest loads without errors, names the definition, and its launch file is among the files; null when it does, the problems in words otherwise.</summary>
    public static string? Check(DefinitionCandidate candidate)
    {
        var load = PackageManifestReader.Load(candidate.Manifest, $"{candidate.Name}/package.yaml");
        if (!load.Ok) return string.Join(" ", load.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString()));
        var manifest = load.Manifest!;
        if (manifest.Name != candidate.Name) return $"the manifest is for the package '{manifest.Name}', not '{candidate.Name}'.";
        if (manifest.Launch.Kind == LaunchKind.Assembly && manifest.Launch.Assembly is { } assembly && candidate.Artifacts.All(a => a.Path != assembly.Replace('\\', '/')))
            return $"the launch assembly '{assembly}' is not among the package's files.";
        return null;
    }

    /// <summary>
    /// Reads the manifest file and the files of <paramref name="directory"/>, stores the files as payloads (one that is stored already, by hash, is not stored again), publishes the version on <paramref name="channel"/> (served at once)
    /// and replaces the channel's task projection with the manifest's tasks. Installing the same content again changes nothing. Throws <see cref="PackageInstallException"/> for what is wrong with the manifest or the files.
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

        var payloads = new PayloadStore(state);
        var publisher = new DefinitionPublisher(state, Kinds(), payloads, "runner");
        var manifestFile = Path.GetFullPath(manifestPath);
        var refs = new List<ArtifactRef>();
        long bytes = 0;
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

            Guid? existing = publisher.FindArtifact(sha);
            if (existing is not null && payloads.Get(existing.Value) is not { Complete: true }) existing = null;
            if (existing is null)
            {
                using var stream = File.OpenRead(path);
                using var writer = payloads.OpenWrite(new PayloadOptions(OwnerModule: "runner", OwnerRow: $"{manifest.Name}/{relative}", CreatedBy: by));
                stream.CopyTo(writer);
                writer.Complete();
                existing = writer.Id;
            }

            refs.Add(new ArtifactRef(relative, sha, size, null, existing));
            bytes += size;
        }

        PublishResult published;
        try
        {
            published = publisher.Publish(new PublishRequest(Kind, manifest.Name, text, by, channel, PublishMode.Serve, Note: "dbdatarunner package install (development)", Artifacts: refs));
        }
        catch (Exception ex) when (ex is PublishRefusedException or DefinitionRejectedException or DefinitionConflictException)
        {
            throw new PackageInstallException(ex.Message);
        }

        var version = published.Version.Version;
        new TaskProjections(state).Replace(manifest.Name, channel, ProjectionDocuments.RowsOf(manifest, version.ToString(CultureInfo.InvariantCulture)));
        new AuditLog(state, RunnerState.ServiceId).Record(new AuditEvent("person", by, "runner.package.install", TargetKind: "package", TargetId: $"{manifest.Name}/{channel}",
            Detail: JsonSerializer.Serialize(new { version, changed = published.Changed, files = refs.Count })));
        return new PackageInstalled(manifest.Name, channel, version, published.Changed, refs.Count, bytes, [.. manifest.Tasks.Select(t => t.Name)]);
    }
}
