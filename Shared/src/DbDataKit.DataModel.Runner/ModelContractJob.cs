using DbDataKit.Connections;
using DbDataKit.DataModel.Storage;
using DbDataKit.Lineage;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;

namespace DbDataKit.DataModel.Runner;

/// <summary>What a person asks the contract task for: with no <see cref="Approve"/> a plan (what would be dropped or made required, and its hash); with the hash of a plan they read, exactly that plan. The schema of the copy (the model's name when empty); where the model is in the package.</summary>
public sealed record ModelContractInput(string Approve = "", string Schema = "", string ModelFile = ModelDeployPackage.ModelFile);

/// <summary>One step of a contract as a report carries it: what it is about, what it does in words, the statement, and whether every row must have a value first (a column made required).</summary>
public sealed record ModelContractStep(string Object, string Description, string Text, bool RequiresEveryRowToHaveAValue);

/// <summary>
/// A column a contract drops, as the lineage graph names it (<see cref="LineageIdentity"/>, from the address of the connection bound to the slot, never a login): what a caller asks the paired Gateways about before it approves the plan
/// (plan 21 CH06, <see cref="ContractGuard"/>), since a task on the Runner reaches no Gateway.
/// </summary>
public sealed record ModelContractColumn(string Description, string ColumnId, string DatasetId);

/// <summary>What a contract found and did: the plan's <see cref="Hash"/> (what an approval names), its steps, what blocks it, the notes, the columns it drops as lineage names, and whether it ran (<see cref="Applied"/>; never on a plan, <see cref="DryRun"/>).</summary>
public sealed record ModelContractReport(string Model, string Schema, string Engine, string Hash, bool DryRun, bool Applied, List<ModelContractStep> Steps, List<string> Blocked, List<string> Notes, List<ModelContractColumn> Dropped);

public static class ModelContractSteps
{
    /// <summary>The contract's report, keyed by the schema.</summary>
    public static readonly ValueStep<ModelContractReport, ModelContractReport> Report = Step.Value<ModelContractReport, ModelContractReport>("contract-report", r => r.Schema);
}

/// <summary>
/// The contract step of expand-then-contract on the Runner (plan 33 SC24; plan 26 Z13b, plan 21 decision 4): the manual-only task <c>contract</c> of a model's definition package. With no approval it reads the schema and reports the plan (the
/// columns the model no longer has are dropped, the columns it requires are made required), its hash and the dropped columns as the lineage graph names them; with <see cref="ModelContractInput.Approve"/> it runs exactly the plan of that hash in one
/// transaction (<see cref="ModelStorage.ApplyContractAsync"/>), or nothing: a plan that changed since it was read, a blocked plan or a column made required that has rows without a value fail the run and change nothing. Who reads a dropped column is
/// asked by the caller before it approves (the Workspace's contract route asks its paired Gateways); the task itself reaches no Gateway.
/// </summary>
[RunnerTask("contract")]
[RunnerDeclarations(typeof(ModelContractSteps))]
public sealed class ModelContractJob : RunnerJob<ModelContractInput, NoProgress, ModelDeploySlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var model = await ModelDeployJob.ReadModelAsync(Input.ModelFile, ct).ConfigureAwait(false);
        var storage = await Slots.Storage.OpenAsync(ct).ConfigureAwait(false);
        var engine = storage.Engine;
        var schema = Input.Schema.Length > 0 ? Input.Schema : model.Name;
        await using var connection = await storage.OpenAsync(LoginRole.Write, ct).ConfigureAwait(false);
        var plan = ModelStorage.PlanContract(model, engine, schema, await ModelStorage.ReadLiveAsync(connection, schema, ct).ConfigureAwait(false));
        var address = storage.Definition;
        var dropped = plan.Steps.Where(s => s.DroppedColumn is not null && s.Table is not null).Select(s =>
        {
            var dataset = LineageIdentity.Dataset(address, schema, s.Table!);
            return new ModelContractColumn($"drop the column '{s.DroppedColumn}' of '{schema}.{s.Table}'", LineageIdentity.Column(address, dataset, s.DroppedColumn!), dataset.ToString());
        }).ToList();
        var approve = Input.Approve.Trim();
        var report = new ModelContractReport(model.Name, schema, engine, plan.Hash, approve.Length == 0, false,
            [.. plan.Steps.Select(s => new ModelContractStep(s.Object, s.Description, s.Text, s.NullCheck is not null))], [.. plan.Blocked], [.. plan.Notes], dropped);

        if (approve.Length > 0)
        {
            try { await ModelStorage.ApplyContractAsync(connection, plan, approve, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is StorageException or System.Data.Common.DbException)
            {
                await Step(ModelContractSteps.Report, report).Repeatable().RunAsync(_ => Task.FromResult(report), ct).ConfigureAwait(false);
                throw new TaskFailureException(TaskFailureKind.Permanent, $"The contract of {model.Name} in {schema} was not run, and nothing was changed: {ex.Message}", inner: ex);
            }
            report = report with { Applied = plan.Steps.Count > 0 };
        }

        Status(report.DryRun
            ? $"contract plan {plan.Hash[..12]}: {plan.Steps.Count} step(s), {plan.Blocked.Count} blocked (nothing was changed)"
            : report.Applied ? $"contract {plan.Hash[..12]}: {plan.Steps.Count} step(s) ran on {schema}" : $"{schema} has nothing to contract");
        await Step(ModelContractSteps.Report, report).Repeatable().RunAsync(_ => Task.FromResult(report), ct).ConfigureAwait(false);
    }
}
