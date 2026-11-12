using DbDataKit.Cli;
using DbDataKit.Config.Git;
using DbDataKit.Lineage;
using DbDataKit.Lineage.Cli;
using DbDataSync.Core.Config;
using DbDataSync.Core.Lineage;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync lineage graph|impact|provenance [--repo &lt;path&gt;]</c> (plan 18, G07b): the lineage of the replications of the repository, built offline from its configuration exactly as <c>GET /api/lineage</c> builds it (<see cref="SyncLineage.Load"/> and
/// <see cref="SyncLineage.Design"/>), so a person or an agent asks "what would break if I drop this column" without a running server. What cannot be read is named on the error stream and left out; the runs are the running server's (they are in
/// its state) and are not part of these documents.
/// </summary>
public static class LineageCommand
{
    private static readonly AsyncLocal<(string[] Args, CliContext? Context)> Current = new();

    public static Noun Noun()
    {
        var inner = LineageNoun.Create(SourceAsync);
        return new Noun(inner.Name, inner.Summary, [.. inner.Verbs.Select(verb => verb with { Usage = verb.Usage + " [--repo <path>]", Run = (args, context) => RunAsync(verb, args, context) })]);
    }

    /// <summary>Takes <c>--repo</c> out of the words (the lineage verbs know no such option) and keeps it for the graph's source.</summary>
    private static Task<int> RunAsync(Verb verb, string[] args, CliContext context)
    {
        var repo = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Length) { repo.Add("--repo"); repo.Add(args[++i]); }
            else rest.Add(args[i]);
        }
        Current.Value = ([.. repo], context);
        return verb.Run([.. rest], context);
    }

    private static async Task<LineageGraph> SourceAsync(CancellationToken cancellation)
    {
        var (args, context) = Current.Value;
        var root = DbDataSyncRoot.Resolve(args);
        var repository = new ConfigRepository(Path.Combine(root, "config"), new GitCommitService(root), SecretStore.OpenInRepository(root));
        var problems = new List<string>();
        var (replications, connections) = SyncLineage.Load(repository, problems);
        var graph = SyncLineage.Design(replications, connections, problems);
        if (context is not null) foreach (var problem in problems) await context.Error.WriteLineAsync(problem);
        return graph;
    }
}
