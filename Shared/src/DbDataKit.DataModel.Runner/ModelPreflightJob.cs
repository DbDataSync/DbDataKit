using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.DataModel.Storage;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;

namespace DbDataKit.DataModel.Runner;

/// <summary>What the preflight is started with: the schema of the copy (the model's name when empty) and where the model is in the package; a control service starts it with neither.</summary>
public sealed record ModelPreflightInput(string Schema = "", string ModelFile = ModelDeployPackage.ModelFile);

public static class ModelPreflightSteps
{
    /// <summary>Each part's findings, keyed by the part (<c>deploy &lt;schema&gt;</c>, <c>contract &lt;schema&gt;</c>).</summary>
    public static readonly ValueStep<JsonObject, JsonObject> Findings = Step.Value<JsonObject, JsonObject>(Preflight.StepName, r => (string?)r["part"] ?? "preflight");
}

/// <summary>
/// What the preflight of a Store model's package says of a deploy's dry run and of the contract plan (Design: docs/publishing.md#preflight-before-activation). A deploy that needs a change it does not make (a column removed or retyped)
/// is an error: the version's deploy would fail and change nothing. What the deploy would create or add is information. The contract is a step of its own that a person approves later, so its plan is information and what blocks it a
/// warning.
/// </summary>
public static class ModelPreflight
{
    public static IReadOnlyList<PreflightFinding> DeployFindings(ModelDeployReport deploy)
    {
        var found = new List<PreflightFinding>();
        foreach (var blocked in deploy.Blocked) found.Add(new(Preflight.Error, $"The deploy of this version needs a change it does not make, so it would fail and change nothing: {blocked}", deploy.Schema));
        found.Add(new(Preflight.Info, deploy.Action switch
        {
            "create" => $"The deploy creates the model's tables in {deploy.Schema} ({deploy.Statements.Count} statement(s)).",
            "expand" => $"The deploy adds what {deploy.Schema} is missing ({deploy.Statements.Count} statement(s)): {string.Join("; ", deploy.Statements.Take(5))}{(deploy.Statements.Count > 5 ? "; ..." : "")}",
            _ => $"{deploy.Schema} already has every table and column of the model.",
        }, deploy.Schema));
        foreach (var note in deploy.Notes) found.Add(new(Preflight.Info, note, deploy.Schema));
        return found;
    }

    public static IReadOnlyList<PreflightFinding> ContractFindings(ModelContractReport contract)
    {
        var found = new List<PreflightFinding>();
        if (contract.Steps.Count > 0)
            found.Add(new(Preflight.Info, $"The contract plan {contract.Hash[..Math.Min(12, contract.Hash.Length)]} has {contract.Steps.Count} step(s), run only when a person approves it: {string.Join("; ", contract.Steps.Take(5).Select(s => s.Description))}{(contract.Steps.Count > 5 ? "; ..." : "")}", contract.Schema));
        foreach (var blocked in contract.Blocked) found.Add(new(Preflight.Warning, $"The contract plan is blocked: {blocked}", contract.Schema));
        return found;
    }
}

/// <summary>
/// The preflight of a Store model's package: the deploy's dry run and the contract's plan against the schema the storage slot opens, read only, under the name of the person who asked; each recorded as findings with its report as the detail.
/// It uses the login the deploy uses, so a login the deploy could not use is found here too, as a run that fails.
/// </summary>
[RunnerTask(Preflight.TaskName)]
[RunnerDeclarations(typeof(ModelPreflightSteps))]
public sealed class ModelPreflightJob : RunnerJob<ModelPreflightInput, NoProgress, ModelDeploySlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var model = await ModelDeployJob.ReadModelAsync(Input.ModelFile, ct).ConfigureAwait(false);
        var storage = await Slots.Storage.OpenAsync(ct).ConfigureAwait(false);
        var engine = storage.Engine;
        var schema = Input.Schema.Length > 0 ? Input.Schema : model.Name;
        await using var connection = await storage.OpenAsync(LoginRole.Write, ct).ConfigureAwait(false);
        var live = await ModelStorage.ReadLiveAsync(connection, schema, ct).ConfigureAwait(false);

        ModelDeployReport deploy;
        if (live.Count == 0)
        {
            var plan = ModelStorage.Plan(model, engine, schema);
            deploy = new(model.Name, schema, engine, "create", true, false, [.. plan.Statements.Select(s => s.Description)], [], [.. plan.Notes]);
        }
        else
        {
            var plan = ModelStorage.PlanExpand(model, engine, schema, live);
            deploy = new(model.Name, schema, engine, plan.Statements.Count == 0 ? "none" : "expand", true, false, [.. plan.Statements.Select(s => s.Description)], [.. plan.Blocked], [.. plan.Notes]);
        }
        var deployed = Preflight.Document($"deploy {schema}", ModelPreflight.DeployFindings(deploy), JsonSerializer.SerializeToNode(deploy));
        await Step(ModelPreflightSteps.Findings, deployed).Repeatable().RunAsync(_ => Task.FromResult(deployed), ct).ConfigureAwait(false);

        // the contract is planned only on what is there: a schema the deploy would create has nothing to contract
        if (live.Count > 0)
        {
            var plan = ModelStorage.PlanContract(model, engine, schema, live);
            var contract = new ModelContractReport(model.Name, schema, engine, plan.Hash, true, false,
                [.. plan.Steps.Select(s => new ModelContractStep(s.Object, s.Description, s.Text, s.NullCheck is not null))], [.. plan.Blocked], [.. plan.Notes], []);
            var contracted = Preflight.Document($"contract {schema}", ModelPreflight.ContractFindings(contract), JsonSerializer.SerializeToNode(contract));
            await Step(ModelPreflightSteps.Findings, contracted).Repeatable().RunAsync(_ => Task.FromResult(contracted), ct).ConfigureAwait(false);
        }

        Status($"preflight of {model.Name} on {schema}: {deploy.Blocked.Count} error(s)");
    }
}
