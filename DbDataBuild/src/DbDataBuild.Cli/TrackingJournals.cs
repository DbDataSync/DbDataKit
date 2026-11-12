using DbDataBuild.Cli.Server;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.Tracking.Build;

namespace DbDataBuild.Cli;

/// <summary>
/// Gives a tracking scope its journal when the project's tracking has the state as its primary (plan 28c E16, `tracking: { mode: state }` or `primary: state` in the project file): from then on every record the commands write goes to the state first and
/// to the target second. The state is the one the host gave this flow (<see cref="TrackingJournalSource"/>: a runner task's, through the protocol; the Build server's own) or, for a command run by hand or by the Build server's scheduler as a
/// child process, the one the environment names (<see cref="ServerDirectoryVariable"/>: the Build server's directory, whose state settings are read as the server reads them). A project that asks for the state and has none to write to is refused
/// before anything is written, saying what to set.
/// </summary>
internal static class TrackingJournals
{
    /// <summary>The Build server's directory (its `server.yaml` and `state.db`): how a command that is not the server finds the state it writes to first. The Build server sets it for the commands it starts.</summary>
    public const string ServerDirectoryVariable = "DBDATAKIT_BUILD_SERVER_DIRECTORY";

    /// <summary>
    /// The primary the Build server hands to the commands it starts when its own `tracking:` setting in `server.yaml` says one (`state` or `target`): it wins over the project's keys, so the console's switch of a server's primary is what the scheduled
    /// refreshes of that server follow. Without it the project's keys decide.
    /// </summary>
    public const string PrimaryVariable = "DBDATAKIT_BUILD_TRACKING_PRIMARY";

    /// <summary>True when records go to the state first: the server's override when it gave one, else the project's `tracking:` keys.</summary>
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
        var sink = TrackingJournalSource.Sink ?? FromEnvironment(env)
            ?? throw new InvalidOperationException($"The project says its tracking is state-first (`tracking: {{ mode: state }}` in {ProductInfo.ConfigFile}), and this run has no state to write to first. A runner task has the runner's; a command takes the Build server's state from {ServerDirectoryVariable} (the server's directory), or the project says `mode: both` to keep the target first.");
        return scope with { Journal = new BuildTrackingJournal(BuildTrackingIdentity.For(trackingRead, scope), sink, (kind, json) => MetadataInfoRows.HasRows(kind) ? MetadataInfoRows.From(kind, json) : null) };
    }

    /// <summary>The journal of the project's tracking connection of <paramref name="connection"/>, or null when the project is target-first or nothing is tracked.</summary>
    public static ITrackingJournal? ForConnection(ProjectConfig config, string connection, Func<string, string?> env)
    {
        if (!IsStateFirst(config, env) || config.TrackingOf(connection).Target is not { } t) return null;
        var (read, _) = LoginSettings.FromEnvironment(t.Connection, t.Engine, Login.Read, env);
        return read is null ? null : For(config, new TrackingScope(t.Engine, t.SchemaName, connection), read, env).Journal;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, IBuildTrackingSink> Opened = new(StringComparer.Ordinal);

    /// <summary>The state of the Build server named by <see cref="ServerDirectoryVariable"/>, opened once for the process; null when the variable is not set.</summary>
    private static IBuildTrackingSink? FromEnvironment(Func<string, string?> env)
    {
        var directory = env(ServerDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        lock (Lock)
        {
            if (Opened.TryGetValue(directory, out var open)) return open;
            var settings = BuildState.Resolve(directory, env).Settings;
            var database = StateOpener.Open(settings, BuildState.SqlitePath(directory), BuildTrackingState.Modules, BuildTrackingState.Scope, settings.IsSqlite ? null : BuildState.Secrets(directory, env));
            return Opened[directory] = new StateBuildTrackingSink(new BuildTrackingStore(database));
        }
    }
}
