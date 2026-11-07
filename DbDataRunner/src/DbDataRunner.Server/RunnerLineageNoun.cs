using DbDataKit.Lineage.Cli;
using DbDataKit.Cli;
using DbDataKit.Runner.Deployments;

namespace DbDataRunner.Server;

/// <summary>
/// <c>dbdatarunner lineage graph|impact|provenance|where|on|runs</c> (plan 28c E24): the deployment lineage of the group (<see cref="RunnerDeploymentLineage"/>) read from the runner's own state with the server stopped or running.
/// <c>where &lt;package&gt;</c> says where a package is deployed (channels, active and previous version, who activated and when, the runners with ready or active), <c>on &lt;runner&gt;</c> what a runner has, <c>runs &lt;package&gt; &lt;version&gt;</c>
/// the runs that named a version. The directory and the group come from <see cref="RunnerSettings.DirectoryVariable"/> and <see cref="RunnerSettings.GroupVariable"/>, as the other local verbs' defaults do.
/// </summary>
public static class RunnerLineageNoun
{
    public static Noun Create(Func<string, string?>? environment) => LineageNoun.Create(_ =>
    {
        var directory = Path.GetFullPath(RunnerSettings.DefaultDirectory(environment));
        var group = RunnerSettings.GroupFrom(environment);
        var state = RunnerState.Open(directory, RunnerSettings.StateFrom(environment), environment, group);
        try { return Task.FromResult(RunnerDeploymentLineage.Build(state, group, new RunnerSettings().InstanceLease)); }
        finally { (state as IDisposable)?.Dispose(); }
    }, deployment: true);
}
