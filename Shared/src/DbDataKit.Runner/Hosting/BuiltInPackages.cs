using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.Scheduling;

namespace DbDataKit.Runner;

/// <summary>
/// The packages a runner ships with its program (<c>builtin/&lt;name&gt;/</c> beside it) and installs in a channel when they are first needed there. Their manifests are written here, so what is installed is what this runner's code expects.
/// Design: docs/authoring-metadata.md#the-built-in-package-dbdatakit-metadata
/// </summary>
public static class BuiltInPackages
{
    /// <summary>The folder beside the program that holds the built-in packages' files.</summary>
    public const string Folder = "builtin";

    /// <summary>
    /// The manifest of <see cref="MetadataPackage.Name"/>: the tasks <c>discover</c> and <c>describe</c>, started by hand only, never retried, allowed to overlap (two people reading at once is what a database does every day), with a
    /// timeout of ten minutes. The manifest declares no slot: a run's one slot is resolved from its inputs (<see cref="MetadataPackage.PackageInput"/>, <see cref="MetadataPackage.SlotInput"/>), so the package needs no binding of its own.
    /// </summary>
    public static string Metadata()
    {
        TaskInput Text(string name, string description, bool required = false, string? value = null) => new(name, ProgressType.Text, required, value, description);
        TaskInput[] common =
        [
            Text(MetadataPackage.PackageInput, "The package whose slot is read.", required: true),
            Text(MetadataPackage.SlotInput, "The slot of that package whose binding in this channel is read.", required: true),
            Text(MetadataPackage.RoleInput, "The login asked for: read (the connection's read login, else its default one) or default.", value: "read"),
        ];
        PackageTask Task(string name, string job, TaskInput[] inputs, string step, StepKind kind) =>
            new(name, new TaskEntry("DbDataKit.Runner.Metadata." + job), new Schedule(), [TaskTrigger.ByHand()], [.. common, .. inputs], [], ProgressDeclaration.Empty, new TaskLimits(Timeout: TimeSpan.FromMinutes(10), Attempts: 1), [], [],
                AllowOverlap: true, Steps: [new TaskStep(step, kind)]);
        var manifest = new PackageManifest(MetadataPackage.Name, "Reads the shape of a database through a project's slot, with the login the channel binds: never a row.",
            new PackageLaunch(LaunchKind.Assembly, Assembly: MetadataPackage.Assembly), new BuiltAgainst(RunnerContracts.Dotnet, null, RunnerContracts.Protocol),
            [
                Task(MetadataPackage.Discover, "DiscoverJob",
                    [Text("schemas", "The schemas to read, as a JSON list; none: every schema."), Text("pattern", "A SQL LIKE pattern over table names."), Text("table", "One table of the one schema given.")],
                    MetadataPackage.Discover, StepKind.Stream),
                Task(MetadataPackage.Describe, "DescribeJob", [Text("sql", "The query whose columns are described; it is not run.", required: true)], MetadataPackage.Describe, StepKind.Value),
            ]);
        return PackageManifestWriter.Write(manifest);
    }
}
