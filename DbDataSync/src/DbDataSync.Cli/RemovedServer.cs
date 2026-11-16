using DbDataKit.Cli;

namespace DbDataSync.Cli;

/// <summary>
/// What <c>dbdatasync</c> answers for the commands of the Sync server it was (plan 33 SC23: the Sync server is deleted, the install is four services). The program is a command line on a configuration repository (scripts, provisioning
/// plans, libraries, drivers, lineage, publishing the replication set to a Runner) and, as <c>DbDataSync.TaskRunner --runner</c>, the runtime a Runner starts for the tasks of the set; every command of the server answers with the sentence
/// that names where its feature went, through the shared <see cref="RemovedCommands"/> (a clean break, never a silent one; exit code 2).
/// </summary>
internal static class RemovedServer
{
    private const string Gone = "the Sync server was deleted (plan 33 SC23)";
    private const string Authoring = "replications are authored in a sync project of the Workspace (dbdataworkspace: its connections, replications, mappings, scripts, checks and provisioning plans)";
    private const string Publishing = "published from there, or with `dbdatasync package publish --to runner`, to a Runner as the replication set's package";
    private const string Running = "the Runner runs every replication (its drains on their schedules, run now, pause and resume, bulk loads, verifications, resyncs and the set's provision task) and keeps their runs, positions and results (its Runs, Tasks and Packages pages)";
    private const string Logins = "the logins are bound on the Runner, to the slots of the set's package (its Packages page), and never kept by a Sync program";

    /// <summary>The server's commands, each with where its feature went.</summary>
    public static RemovedCommands Commands { get; } = new(SyncCli.Program, new Dictionary<string, string>
    {
        ["server"] = $"{Gone}. {Capitalised(Authoring)}, {Publishing}; {Running}; {Logins}. The screens are the console's: the Workspace's and the Runner's.",
        ["service"] = $"{Gone}, so there is no service to register: the install is four services (console, Workspace, Gateway, Runner), and a Runner starts the Sync engine (dbdatasync-runtime) for the replications' tasks.",
        ["cert"] = $"{Gone}, so it has no listener and no certificate: each of the four services (console, Workspace, Gateway, Runner) presents its own, set with its own `cert` command.",
        ["secret"] = $"{Gone} with its secret store: {Logins}.",
        ["config"] = $"{Gone} with its server.yaml: the repository's own files are checked with `dbdatasync package publish --to runner --check` (and by the Workspace as it is edited).",
        ["state"] = $"{Gone} with its state database: the runs, positions, batches and verification results of the replications are the Runner's (its own state).",
        ["health"] = $"{Gone}, so there is no server to check: check the Workspace and the Runner that took over its work.",
        ["gateway"] = $"{Gone} with its Gateway pairings: a run that wrote rows is told to the serving groups the Runner is paired with (`dbdatarunner gateway add`), which clear their cache.",
        ["update"] = $"{Gone} with its updater: dbdatasync is a command line released as a zip; install a new release in its place (or the dbdatasync-runtime package on a Runner).",
        ["launcher"] = $"{Gone} with its updater and its service launcher: nothing installs or repairs a launcher any more.",
        ["internal"] = $"{Gone} with its container image, whose build was the only caller of these commands.",
        ["serve"] = $"{Gone}. {Capitalised(Running)}.",
        ["setup"] = $"{Gone}, so there is nothing to set up: author the replications in the Workspace and publish them to a Runner.",
        ["invite"] = "users and invitations are the dbdatakit console's (`dbdatakit user invite`), and the Sync server was deleted (plan 33 SC23).",
    });

    private static string Capitalised(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
