using System.Text;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Scheduling;

namespace DbDataKit.DataModel.Runner;

/// <summary>A model's definition package: its manifest (and the text of it) and the model's text, which goes under <see cref="ModelDeployPackage.ModelFile"/>.</summary>
public sealed record BuiltModelPackage(string Name, PackageManifest Manifest, string ManifestText, string Model, string ModelText);

/// <summary>
/// The definition package of a Store model on the Runner (plan 33 SC24): three manual-only tasks, <c>deploy</c> (<see cref="ModelDeployJob"/>), <c>contract</c> (<see cref="ModelContractJob"/>) and the read-only <c>preflight</c>
/// (<see cref="ModelPreflightJob"/>: the deploy's dry run and the contract's plan as findings, which a control service runs before it serves a version in production), run by the <c>dbdatastore</c> runtime (<see cref="RuntimeName"/>), with the one slot
/// <c>storage</c> that a channel binds to the model's connection on the Runner, never on a schedule, never retried, under the name of the person who asks. The model goes in the package as written (in the current format), so what a run
/// deploys is the version the channel serves.
/// </summary>
public static class ModelDeployPackage
{
    /// <summary>The runtime package that runs a model's tasks: <c>DbDataStore/scripts/runtime-manifest.sh</c>'s name.</summary>
    public const string RuntimeName = "dbdatastore-runtime";

    /// <summary>Where the model is in the package (under <c>files/</c>).</summary>
    public const string ModelFile = "model/model.yaml";

    public const string DeployTask = "deploy";

    /// <summary>The contract step (<see cref="ModelContractJob"/>): a plan without an approval, exactly the approved plan with one.</summary>
    public const string ContractTask = "contract";

    /// <summary>The package name of a model: <c>model-&lt;name&gt;</c>, with what a package name does not take made a dash.</summary>
    public static string NameOf(string model)
    {
        var name = "model-" + new string([.. model.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-')]);
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>The package of the model text, or null with the reasons in <paramref name="problems"/>. <paramref name="runtimeRange"/> is the runtimes it accepts (<c>*</c> for any).</summary>
    public static BuiltModelPackage? Build(string modelText, List<string> problems, string? package = null, string runtimeRange = "*")
    {
        var before = problems.Count;
        var load = DataModelLoader.Load(modelText, ModelFile);
        if (load.Model is not { } model)
        {
            problems.AddRange(load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString()));
            if (problems.Count == before) problems.Add($"{ModelFile}: the model does not read.");
            return null;
        }

        // every task is a person's act: by hand only, never scheduled, never retried
        static PackageTask Manual(Type job) { var t = ManifestGenerator.TaskOf(job); return t with { Entry = new TaskEntry(), Schedule = new Schedule(), Triggers = [TaskTrigger.ByHand()], Limits = t.Limits with { Attempts = 1 } }; }
        var name = package ?? NameOf(model.Name);
        var manifest = new PackageManifest(name, $"The Store model {model.Name}: deploy its tables",
            new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference(RuntimeName, runtimeRange)),
            new BuiltAgainst(TargetDotnet(), null, RunnerProtocol.V1), [Manual(typeof(ModelDeployJob)), Manual(typeof(ModelContractJob)), Manual(typeof(ModelPreflightJob))]);
        var text = PackageManifestWriter.Write(manifest);
        var check = PackageManifestReader.Load(text, "package.yaml");
        if (!check.Ok)
        {
            problems.AddRange(check.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => $"package.yaml: {d}"));
            return null;
        }

        return new BuiltModelPackage(name, manifest, text, model.Name, DataModelLoader.WithCurrentVersion(modelText.ReplaceLineEndings("\n")));
    }

    /// <summary>Writes the package into <paramref name="directory"/>: <c>package.yaml</c> and <c>files/model/model.yaml</c>, LF line endings.</summary>
    public static void Write(BuiltModelPackage package, string directory)
    {
        var encoding = new UTF8Encoding(false);
        Directory.CreateDirectory(Path.Combine(directory, "files", "model"));
        File.WriteAllText(Path.Combine(directory, "package.yaml"), package.ManifestText, encoding);
        File.WriteAllText(Path.Combine(directory, "files", "model", "model.yaml"), package.ModelText, encoding);
    }

    private static string TargetDotnet()
    {
        var framework = typeof(ModelDeployPackage).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>().FirstOrDefault()?.FrameworkName;
        const string marker = "Version=v";
        var at = framework?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        return at >= 0 && Version.TryParse(framework![(at + marker.Length)..], out var v) ? $"{v.Major}.{v.Minor}" : $"{Environment.Version.Major}.{Environment.Version.Minor}";
    }
}
