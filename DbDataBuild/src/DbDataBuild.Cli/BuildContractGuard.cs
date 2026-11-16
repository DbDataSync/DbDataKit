using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataKit.Lineage;
using DbDataKit.Schema.Planning;
using DbDataKit.Serving.Notify;

namespace DbDataBuild.Cli;

/// <summary>
/// Build's half of the contract guard (plan 21 CH06): before an apply drops, renames away or retypes a column, the lineage of the serving groups is asked who reads that column, in any channel
/// (<see cref="GatewayLineageClient"/>, <see cref="ContractGuard"/>). A published dashboard, report, app or semantic model that reads it refuses the step, naming the consumer and its channel (DDB-444), until it is published
/// without the column or the person allows it by name with <c>--allow-consumer &lt;name&gt;@&lt;channel&gt;</c>; each allowance used is written in the document of the command. The lineage client is given by the caller; the Build
/// server that asked its own paired Gateways was deleted (plan 33 SC23), so a run that is given none asks nothing, and says so. A table is identified as lineage identifies it (<see cref="BuildLineage.IdentityOf"/>), so it is the node the Gateway's graph holds.
/// </summary>
internal static class BuildContractGuard
{
    /// <summary>The columns the plan's steps take away: one per drop, rename or retype step of a table.</summary>
    internal static IReadOnlyList<(PlanStep Step, string Verb, string Column)> Steps(Plan plan) =>
        [.. plan.Steps.Where(s => s.Type == StepType.Ddl).Select(s => (Step: s, Change: ContractSteps.Of(s.Description, s.Reasons))).Where(x => x.Change is not null).Select(x => (x.Step, x.Change!.Value.Verb, x.Change.Value.Column))];

    /// <summary>The refusals (DDB-444) for the plan's drops and retypes; empty when none is read by a consumer (or the check was allowed). <paramref name="allowed"/> are the <c>--allow-consumer</c> values.</summary>
    public static async Task<(IReadOnlyList<Diagnostic> Refusals, IReadOnlyList<string> AllowancesUsed, string? Note)> CheckAsync(Plan plan, ProjectConfig config, Func<string, string?> env, IReadOnlyCollection<string> allowed, Func<string, GatewayLineageClient?>? clientFor = null, CancellationToken ct = default)
    {
        var steps = Steps(plan);
        if (steps.Count == 0) return ([], [], null);
        if (clientFor?.Invoke(plan.Connection) is not { } client)
            return ([], [], $"The plan drops or retypes {steps.Count} column(s) and nothing was asked which published consumers read them: this run has no Gateway lineage to ask (the Build server that asked its paired Gateways was deleted, plan 33 SC23).");
        var identityOf = BuildLineage.IdentityOf(config, env);
        var connection = identityOf(plan.Connection);
        var changes = new List<ContractedColumn>();
        foreach (var (step, verb, column) in steps)
        {
            var (schema, table) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(step.Object);
            var dataset = LineageIdentity.Dataset(connection, schema, table);
            var what = verb switch { "drop" => "drop", "rename" => "rename away", _ => "retype" };
            changes.Add(new ContractedColumn($"{what} the column '{column}' of '{step.Object}' (step {step.Id}, connection {plan.Connection})", LineageIdentity.Column(connection, dataset, column), dataset.ToString()));
        }

        var fetched = await client.FetchAsync(ct).ConfigureAwait(false);
        var verdict = ContractGuard.Check(changes, fetched.Graphs, fetched.Unreachable, allowed, "--allow-consumer");
        var refusals = verdict.Refusals.Select(r => new Diagnostic(DiagnosticCatalog.ConsumerReadsColumn, new($"plan:{plan.Id}", 0, 0), r)).ToList();
        return (refusals, refusals.Count == 0 ? verdict.AllowancesUsed : [], null);
    }
}
