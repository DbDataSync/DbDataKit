using System.Text.Json.Nodes;
using DbDataKit.Lineage;

namespace DbDataKit.Deploy;

/// <summary>The plan of a Store model's contract a person approved: the columns it drops and the schema it runs on.</summary>
public sealed record ContractPlan(IReadOnlyList<ContractedColumn> Dropped, string Schema);

/// <summary>A run of contract read as an approved plan: the <see cref="Plan"/>, or the <see cref="Refusal"/> in words (<see cref="Blocked"/> when the plan has items a contract does not do).</summary>
public sealed record ContractPlanReading(ContractPlan? Plan, string? Refusal, bool Blocked = false);

/// <summary>
/// The checks before a Store model's contract runs on a Runner. A person reads the plan (a run of contract without an approval, whose report has a hash) and approves that hash. Before contract runs with it, the plan's report must be a dry
/// run with exactly that hash and nothing a contract does not do, and every column it drops is checked against the lineage of the Gateways: a published consumer that reads one, in any channel, refuses the step by name until it is
/// published without it, or a person allows it as <c>&lt;name&gt;@&lt;channel&gt;</c>; a Gateway that does not answer refuses it too, and <c>gateway:&lt;name&gt;</c> allows that.
/// </summary>
public static class ContractPlans
{
    /// <summary>The field of a request that carries the person's allowances, as the refusal names it.</summary>
    public const string AllowField = "allow";

    /// <summary>
    /// The plan of <paramref name="run"/> (a run read with its <c>contract-report</c> steps, as <see cref="RunnerCalls.RunOfAsync"/> answers) that <paramref name="approve"/> names: its last report must be a dry run with that hash and no item a
    /// contract does not do. <paramref name="planRun"/> is the run's id, for the refusal.
    /// </summary>
    public static ContractPlanReading Read(JsonObject run, Guid planRun, string approve)
    {
        var report = (run["steps"] as JsonArray)?.Select(x => x?["result"]).OfType<JsonObject>().LastOrDefault();
        if (report is null || report["dryRun"]?.GetValue<bool>() != true || !string.Equals((string?)report["hash"], approve, StringComparison.OrdinalIgnoreCase))
            return new ContractPlanReading(null, $"The run {planRun} did not read a plan with the hash {approve}. Read the plan again and approve the hash it gives.");
        if (report["blocked"] is JsonArray { Count: > 0 } blocked)
            return new ContractPlanReading(null, "The plan has items a contract does not do, so it is not run: " + string.Join(" ", blocked.Select(b => (string?)b)), Blocked: true);
        var dropped = (report["dropped"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => new ContractedColumn((string)c["description"]!, (string)c["columnId"]!, (string?)c["datasetId"])).ToList();
        return new ContractPlanReading(new ContractPlan(dropped, (string?)report["schema"] ?? ""), null);
    }

    /// <summary>Whether the Gateways' lineage lets <paramref name="plan"/> drop its columns: the consumers that still read one refuse it, unless <paramref name="allowed"/> names them; a Gateway in <paramref name="unreachable"/> refuses it too.</summary>
    public static ContractVerdict CheckConsumers(ContractPlan plan, IReadOnlyList<(string Server, LineageGraph Graph)> graphs, IReadOnlyList<(string Server, string Why)> unreachable, IReadOnlyCollection<string> allowed) =>
        ContractGuard.Check(plan.Dropped, graphs, unreachable, allowed, AllowField);

    /// <summary>The inputs of the run of contract that applies <paramref name="plan"/>: the approved hash and the schema.</summary>
    public static Dictionary<string, string> Inputs(ContractPlan plan, string approve) =>
        new(StringComparer.Ordinal) { ["approve"] = approve, ["schema"] = plan.Schema };
}
