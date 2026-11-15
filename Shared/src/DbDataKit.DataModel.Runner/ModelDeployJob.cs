using DbDataKit.Connections;
using DbDataKit.DataModel.Storage;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;

namespace DbDataKit.DataModel.Runner;

/// <summary>What a person asks the deploy task for: a dry run (the default: say what would change, change nothing) or the change itself; the schema of the copy (the model's name when empty); where the model is in the package.</summary>
public sealed record ModelDeployInput(bool DryRun = true, string Schema = "", string ModelFile = ModelDeployPackage.ModelFile);

/// <summary>
/// What a deploy found and did: <see cref="Action"/> is <c>create</c> (the schema had nothing of the model), <c>expand</c> (tables or columns were missing) or <c>none</c> (up to date); <see cref="Statements"/> are the descriptions of what ran
/// (or would run, on a dry run); <see cref="Blocked"/> are the changes a deploy does not make (a planned contract step of its own), <see cref="Notes"/> what was noticed. A run output the model's screen reads.
/// </summary>
public sealed record ModelDeployReport(string Model, string Schema, string Engine, string Action, bool DryRun, bool Applied, List<string> Statements, List<string> Blocked, List<string> Notes);

/// <summary>The one connection a deploy uses: the model's storage, bound on the Runner (its write login).</summary>
public sealed class ModelDeploySlots : Slots
{
    public const string StorageSlot = "storage";

    public Slot<ConnectionGrant> Storage { get; } = new(StorageSlot, "database");
}

public static class ModelDeploySteps
{
    /// <summary>The deploy's report, keyed by the schema.</summary>
    public static readonly ValueStep<ModelDeployReport, ModelDeployReport> Report = Step.Value<ModelDeployReport, ModelDeployReport>("deploy-report", r => r.Schema);
}

/// <summary>
/// A Store model's tables deployed on the Runner (plan 33 SC24, the Store without its server): the manual-only task <c>deploy</c> of a model's definition package, run by the <c>dbdatastore</c> runtime under the name of the person who
/// asked. It reads the model from the package, opens the storage slot with the login the channel binds, reads what the schema holds, and then creates the model's storage (nothing of it is there), expands it (adds the tables and
/// columns that are missing, as nullable) or does nothing; a change that would remove or retype is <see cref="ModelDeployReport.Blocked"/> and fails the run, writing nothing (a contract step is planned on its own). A dry run (the default)
/// reports the statements and changes nothing. Everything runs in one transaction (<see cref="ModelStorage"/>, the code the Store server's <c>storage create</c> used).
/// </summary>
[RunnerTask("deploy")]
[RunnerDeclarations(typeof(ModelDeploySteps))]
public sealed class ModelDeployJob : RunnerJob<ModelDeployInput, NoProgress, ModelDeploySlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var package = Environment.GetEnvironmentVariable(RunnerProtocolNames.PackageDirectoryVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.PackageDirectoryVariable} is not set: the runner tells a task where its package's view is.");
        var file = Path.GetFullPath(Path.Combine(package, Input.ModelFile));
        if (!file.StartsWith(Path.GetFullPath(package), StringComparison.Ordinal) || !File.Exists(file))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The package has no model at '{Input.ModelFile}'.");
        var load = DataModelLoader.Load(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Input.ModelFile);
        if (load.Model is not { } model)
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The model in the package does not read: {string.Join(" ", load.Diagnostics.Select(d => d.ToString()))}");

        var grant = await Slots.Storage.OpenAsync(ct).ConfigureAwait(false);
        var engine = grant.Engine;
        var schema = Input.Schema.Length > 0 ? Input.Schema : model.Name;
        await using var connection = await GrantConnections.OpenAsync(grant, LoginRole.Write, ct).ConfigureAwait(false);
        var live = await ModelStorage.ReadLiveAsync(connection, schema, ct).ConfigureAwait(false);

        ModelDeployReport report;
        if (live.Count == 0)
        {
            var plan = ModelStorage.Plan(model, engine, schema);
            report = new(model.Name, schema, engine, "create", Input.DryRun, false, [.. plan.Statements.Select(s => s.Description)], [], [.. plan.Notes]);
            if (!Input.DryRun)
            {
                await ModelStorage.CreateAsync(connection, plan, ct).ConfigureAwait(false);
                report = report with { Applied = true };
            }
        }
        else
        {
            var plan = ModelStorage.PlanExpand(model, engine, schema, live);
            report = new(model.Name, schema, engine, plan.Statements.Count == 0 ? "none" : "expand", Input.DryRun, false, [.. plan.Statements.Select(s => s.Description)], [.. plan.Blocked], [.. plan.Notes]);
            if (!Input.DryRun && plan.Blocked.Count == 0 && plan.Statements.Count > 0)
            {
                await ModelStorage.ApplyExpandAsync(connection, plan, ct).ConfigureAwait(false);
                report = report with { Applied = true };
            }
        }

        Status(Input.DryRun
            ? $"{report.Action}: {report.Statements.Count} statement(s) would run on {schema} (a dry run; nothing was changed)"
            : report.Applied ? $"{report.Action}: {report.Statements.Count} statement(s) ran on {schema}" : $"{schema} is up to date with {model.Name}");
        await Step(ModelDeploySteps.Report, report).Repeatable().RunAsync(_ => Task.FromResult(report), ct).ConfigureAwait(false);
        // a dry run reports what is blocked and succeeds: it was asked what would happen
        if (report.Blocked.Count > 0 && !Input.DryRun)
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The deploy of {model.Name} to {schema} needs a planned change it does not make, so nothing was changed: {string.Join(" ", report.Blocked)}");
    }
}
