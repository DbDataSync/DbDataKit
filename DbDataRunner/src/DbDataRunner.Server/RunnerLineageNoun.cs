using DbDataKit.Cli;
using DbDataKit.Lineage.Cli;
using DbDataKit.Runner.Deployments;

namespace DbDataRunner.Server;

/// <summary>
/// <c>dbdatarunner lineage graph|impact|provenance|where|on|runs</c> (plan 28c E24): the deployment lineage of the group (<see cref="RunnerDeploymentLineage"/>) read from the runner's own state, with the server stopped or running.
/// <c>where &lt;package&gt;</c> says where a package is deployed (channels, active and previous version, who activated and when, the runners with ready or active), <c>on &lt;runner&gt;</c> what a runner has, <c>runs &lt;package&gt; &lt;version&gt;</c>
/// the runs that named a version. <c>--repo</c> and <c>--group</c> are the other local verbs' (the directory and the group the environment names by default).
/// </summary>
public static class RunnerLineageNoun
{
    private const string Where = "[--repo <server directory>] [--group <name>]";

    private static readonly AsyncLocal<(string? Repo, string? Group)> Options = new();

    public static Noun Create(Func<string, string?>? environment)
    {
        var inner = LineageNoun.Create(_ =>
        {
            var directory = Path.GetFullPath(Options.Value.Repo ?? RunnerSettings.DefaultDirectory(environment));
            var group = RunnerSettings.GroupFrom(environment, Options.Value.Group);
            var state = RunnerState.Open(directory, RunnerSettings.StateFrom(directory, environment), environment, group);
            try { return Task.FromResult(RunnerDeploymentLineage.Build(state, group, new RunnerSettings(directory, new Uri("https://localhost:0")).InstanceLease)); }
            finally { (state as IDisposable)?.Dispose(); }
        }, deployment: true);
        return new Noun(inner.Name, inner.Summary, [.. inner.Verbs.Select(verb => verb with { Usage = $"{verb.Usage} {Where}", Run = (args, context) => RunAsync(verb, args, context) })]);
    }

    /// <summary>Takes <c>--repo</c> and <c>--group</c> out of the words (the lineage verbs know no such options) and keeps them for the graph's source.</summary>
    private static Task<int> RunAsync(Verb verb, string[] args, CliContext context)
    {
        string? repo = null, group = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--repo" or "--group" && i + 1 < args.Length)
            {
                if (args[i] == "--repo") repo = args[i + 1]; else group = args[i + 1];
                i++;
            }
            else rest.Add(args[i]);
        }
        Options.Value = (repo, group);
        return verb.Run([.. rest], context);
    }
}
