using DbDataKit.Config.Yaml;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>
/// How packages get into a runner in this slice (plan 28a R10): the kind <c>integration</c> of the definitions registry (plan 30), checked by the package manifest's own validator (R01), and <c>package install</c>, a development
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
}
