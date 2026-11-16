namespace DbDataBuild.Cli;

/// <summary>
/// What <c>dbdatabuild</c> answers for the commands of the Build server it was (plan 33 SC23: the Build server is deleted, the install is four services). The program is a command line on a project folder and the runtime a Runner
/// starts for the tasks of a project's packages (<c>--runner-task</c>: refresh, catch-up, and the manual <c>apply_plan</c>, <c>ack</c> and <c>seed</c>); every command of the server answers with the sentence that names where its feature went,
/// through the shared <see cref="RemovedCommands"/> (a clean break, never a silent one; exit code 2).
/// </summary>
internal static class RemovedServer
{
    private const string Gone = "the Build server was deleted (plan 33 SC23)";
    private const string Authoring = "a project is authored in a build project of the Workspace (dbdataworkspace: its files, models, graph, checks, compile, sample, import, views and deploy plans)";
    private const string Publishing = "published from there to a Runner as a package and to a Gateway as its semantic models and data sources";
    private const string Running = "its refresh plans run on the Runner on their schedules, and a plan is applied, a change acknowledged or seeds loaded as the manual tasks apply_plan, ack and seed of the project's operations package (project publish --to runner --operations), under the name of the person who asked";
    private const string Logins = "the logins are bound on the Runner, to the build-login slot of the package (its Packages page), and never kept by a Build program";

    /// <summary>The server's commands, each with where its feature went.</summary>
    public static RemovedCommands Commands { get; } = new("dbdatabuild", new Dictionary<string, string>
    {
        ["server"] = $"{Gone}. {Capitalised(Authoring)}, {Publishing}; {Running}; {Logins}.",
        ["service"] = $"{Gone}, so there is no service to register: the install is four services (console, Workspace, Gateway, Runner), and a Runner starts dbdatabuild --runner-task for a project's tasks.",
        ["health"] = $"{Gone}, so there is no server to check: check the Workspace and the Runner that took over its work.",
        ["state"] = $"{Gone} with its state database: a project's runs, its schedules and the copy of its tracking tables are the Runner's (its Runs page and its own state).",
        ["secret"] = $"{Gone} with its secret store: {Logins}.",
        ["lineage"] = $"{Gone} with its lineage noun: the column lineage of a draft is the Workspace's Lineage panel of a build project, and what a refresh loaded is the run lineage the Runner reports.",
        ["semantic publish"] = $"{Gone} with its Gateway pairings: a semantic model is published from the Workspace, as the Gateway target of a build project's build.",
        ["semantic rollback"] = $"{Gone} with its Gateway pairings: a semantic model is published again from the Workspace (an earlier build of the project), and a published version is managed on the Gateway.",
    });

    private static string Capitalised(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
