using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataKit.Lineage;
using DbDataKit.Schema.Planning;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// Build's half of the contract guard (plan 21 CH06): before an apply drops, renames away or retypes a column, the lineage of every serving group the Build server is paired with is asked who reads that column, in any channel
/// (<see cref="GatewayLineageClient"/>, <see cref="ContractGuard"/>). A published dashboard, report, app or semantic model that reads it refuses the step, naming the consumer and its channel (DDB-444), until it is published
/// without the column or the person allows it by name with <c>--allow-consumer &lt;name&gt;@&lt;channel&gt;</c>; each allowance used is written in the server's management audit and the document of the command. The pairings and the key are the server's
/// (<see cref="TrackingJournals.ServerDirectoryVariable"/> names its directory, which a command the server starts is given); a run with no such directory has no pairings and asks nothing, which is said. A table is identified as lineage identifies it,
/// from the connection's address in the logins the server stores (<see cref="BuildLineage.IdentityOf"/>), so it is the node the Gateway's graph holds.
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
        var directory = env(TrackingJournals.ServerDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory) && clientFor is null)
            return ([], [], $"The plan drops or retypes {steps.Count} column(s) and nothing was asked which published consumers read them: this run has no Build server directory ({TrackingJournals.ServerDirectoryVariable}), so no paired Gateway to ask.");
        var client = clientFor is not null ? clientFor(directory ?? "") : ClientOf(directory!);
        if (client is null) return ([], [], null);
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

    /// <summary>The lineage client of the server whose directory this is: its pairings (<c>build/proxies.json</c>) and its own key, opened only when there is a pairing to ask.</summary>
    internal static GatewayLineageClient ClientOf(string directory)
    {
        ConsoleKeyStore? keys = null;
        var gate = new object();
        Task<ConsoleIdentity> Identity(CancellationToken token)
        {
            lock (gate)
            {
                if (keys is null)
                {
                    var secrets = ServerSecrets.Open(directory);
                    keys = new ConsoleKeyStore(secrets, secrets);
                }
            }

            return keys.GetOrCreateAsync(token);
        }

        return new GatewayLineageClient(new BuildDataChanges.FromProxies(new ProxiesFile(directory)), Identity, product: "build");
    }

    /// <summary>Writes the allowances that were used in the server's management audit (who, which consumer in which channel, for which plan); a server with no directory has no audit and the command's document carries them.</summary>
    public static void Record(string? serverDirectory, string actor, string planId, IReadOnlyList<string> allowances)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory) || allowances.Count == 0) return;
        var audit = new FileAuditLog(Path.Combine(serverDirectory, "management-audit.log"));
        foreach (var allowance in allowances)
            audit.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild", actor, actor, false, "plan.apply.allow-consumer", $"{planId}: {allowance}", "ok", ""));
    }
}
