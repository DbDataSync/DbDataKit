using DbDataBuild.Cli.Server;
using DbDataBuild.Execution;
using DbDataBuild.State;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;

namespace DbDataBuild.Cli.Runner;

/// <summary>
/// The follower of a runner task (plan 28c E16): reads the tracking tables of a project's connections with the read login the run holds (<c>DBDATAKIT_BUILD_&lt;CONNECTION&gt;_READ</c>, set by the runner for the slots the run
/// holds) and copies them into the runner's <c>build_tracking</c> module through the protocol (<see cref="IRemoteTrackingCopy"/>). Never throws and never fails a run: a target that is down, a store not at the layout, a copy
/// the runner refused are lines in the result.
/// </summary>
internal static class RunnerTrackingCopy
{
    public static async Task<IReadOnlyList<string>> CopyAsync(IRemoteTrackingCopy copy, string projectRoot, string? connection, Func<string, string?> env, TimeSpan budget, CancellationToken ct)
    {
        var lines = new List<string>();
        if (BuildTrackedConnections.RunnerCopyOff(projectRoot))
        {
            lines.Add("the runner keeps no copy of this project's tracking tables (`tracking: { runner_copy: false }` in the project file), so nothing is copied");
            return lines;
        }

        if (BuildTrackedConnections.StateFirst(projectRoot, env))
        {
            lines.Add("the project's tracking is state-first (`tracking: { mode: state }`): its runs write every record to the runner's state before the target, so there is nothing for a follower to copy");
            return lines;
        }

        var started = DateTime.UtcNow;
        try
        {
            foreach (var (name, scope, login) in BuildTrackedConnections.Of(projectRoot, string.IsNullOrEmpty(connection) ? null : connection, env))
            {
                var remaining = budget - (DateTime.UtcNow - started);
                if (remaining <= TimeSpan.Zero) { lines.Add($"{name}: the time for the copy of the tracking tables ran out"); break; }
                lines.Add(await CopyOneAsync(copy, name, scope, login, remaining, ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) { lines.Add("the copy of the tracking tables was cancelled"); }
        catch (Exception ex) { lines.Add($"the copy of the tracking tables failed ({ex.GetType().Name})"); }
        return lines;
    }

    private static async Task<string> CopyOneAsync(IRemoteTrackingCopy copy, string connection, TrackingScope scope, LoginSettings login, TimeSpan budget, CancellationToken ct)
    {
        try
        {
            await using var read = await ReadSession.OpenAsync(login, ct).ConfigureAwait(false);
            var status = await TrackingStore.StatusAsync(read, scope.Engine, scope.SchemaName, ct).ConfigureAwait(false);
            if (status.State != TrackingState.Ready) return $"{connection}: the tracking tables are not at layout {TrackingSchema.Version} (`connection init --upgrade --apply`), so they are not copied";
            var identity = BuildTrackingIdentity.For(login, scope);
            var source = new BuildTrackingSource(read, scope, identity, BuildTrackingHost.DefaultSettleLag);
            var copier = new RemoteBuildTrackingCopier(identity, source, copy, (kind, json) => MetadataInfoRows.HasRows(kind) ? MetadataInfoRows.From(kind, json) : null);
            var result = await copier.RunAsync(budget, ct).ConfigureAwait(false);
            return result.Ok ? $"{connection}: {result.Copied} tracking row(s) copied{(result.CaughtUp ? "" : ", not caught up yet")}" : $"{connection}: {string.Join("; ", result.Problems)}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"{connection}: the tracking store could not be copied ({ex.GetType().Name})"; }
    }
}
