using System.Security.Cryptography;
using DbDataKit.Config.Yaml;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner.Api;

/// <summary>A package could not be put into the runner; the message says why (and names the file and line for a manifest problem).</summary>
public sealed class PackageInstallException(string message) : InvalidOperationException(message);

/// <summary>What <see cref="RunnerPackageInstaller.Install"/> did.</summary>
public sealed record PackageInstalled(string Package, string Channel, int Version, bool Changed, int Files, long Bytes, IReadOnlyList<string> Tasks);

/// <summary>The development path of getting a package into a runner without HTTP or pairing (plan 28a R10), through the same service layer the publish API uses.</summary>
public static class RunnerPackageInstaller
{
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

            var published = service.Publish(new PublishInput(manifest.PackageName, text, files, channel, PublishMode.Serve, Note: "dbdatarunner package install (development)"), actor, RunnerPublishing.InstallVerb);
            return new PackageInstalled(manifest.PackageName, channel, published.Version, published.Changed, files.Count, bytes, published.Tasks);
        }
        catch (RunnerPublishException ex)
        {
            throw new PackageInstallException(ex.Message);
        }
}
}
