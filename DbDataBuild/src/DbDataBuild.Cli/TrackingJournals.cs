using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.Tracking.Build;

namespace DbDataBuild.Cli;

/// <summary>
/// Gives a tracking scope its journal when the project's tracking has the state as its primary (plan 28c E16, `tracking: { mode: state }` or `primary: state` in the project file): from then on every record the commands write goes to the state first and
/// to the target second. The state is the one the host gave this flow (<see cref="TrackingJournalSource"/>: a runner task's, through the protocol). A project that asks for the state and has none to write to is refused
/// before anything is written, saying what to set. (A command run by hand has no state of its own: the Build server whose state a command could be pointed at was deleted, plan 33 SC23.)
/// </summary>
internal static class TrackingJournals
{
    /// <summary>A primary (`state` or `target`) that wins over the project's keys for this process, when the environment gives one; without it the project's keys decide.</summary>
    public const string PrimaryVariable = "DBDATAKIT_BUILD_TRACKING_PRIMARY";

    /// <summary>True when records go to the state first: the environment's override when it gives one, else the project's `tracking:` keys.</summary>
    public static bool IsStateFirst(ProjectConfig config, Func<string, string?> env) =>
        env(PrimaryVariable) switch
        {
            "state" => true,
            "target" => false,
            _ => config.Tracking.StatePrimary,
        };

    /// <summary>The scope with its journal when the project's tracking is state-first, else the scope as it is. Raises <see cref="InvalidOperationException"/> when it is and no state can be reached.</summary>
    public static TrackingScope For(ProjectConfig config, TrackingScope scope, LoginSettings trackingRead, Func<string, string?> env)
    {
        if (!IsStateFirst(config, env)) return scope;
        var sink = TrackingJournalSource.Sink
            ?? throw new InvalidOperationException($"The project says its tracking is state-first (`tracking: {{ mode: state }}` in {ProductInfo.ConfigFile}), and this run has no state to write to first. A runner task has the runner's (publish the project to a Runner and run it there), or the project says `mode: both` to keep the target first.");
        return scope with { Journal = new BuildTrackingJournal(BuildTrackingIdentity.For(trackingRead, scope), sink, (kind, json) => MetadataInfoRows.HasRows(kind) ? MetadataInfoRows.From(kind, json) : null) };
    }

    /// <summary>The journal of the project's tracking connection of <paramref name="connection"/>, or null when the project is target-first or nothing is tracked.</summary>
    public static ITrackingJournal? ForConnection(ProjectConfig config, string connection, Func<string, string?> env)
    {
        if (!IsStateFirst(config, env) || config.TrackingOf(connection).Target is not { } t) return null;
        var (read, _) = LoginSettings.FromEnvironment(t.Connection, t.Engine, Login.Read, env);
        return read is null ? null : For(config, new TrackingScope(t.Engine, t.SchemaName, connection), read, env).Journal;
    }
}
