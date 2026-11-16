using DbDataKit.Cli;

namespace DbDataStore.Server;

/// <summary>
/// What <c>dbdatastore</c> answers without <c>--runner</c> (plan 33 SC23: the Store server is deleted, the install is four services). The program is the runtime of a Store model's package (<see cref="StoreRunnerEntry"/>) and a command line on a
/// folder (<see cref="Router"/>: a model put into it, its tables made, the model published to the Gateways it is paired with, over the Gateway's per-kind routes until plan 30 PB02 retires them); every command of the server it was answers with
/// the sentence that names where its feature went (<see cref="Removed"/>).
/// </summary>
public static class StoreCli
{
    public const string Program = "dbdatastore";

    private const string Authoring = "a Store model is authored in a store project of the Workspace (dbdataworkspace)";
    private const string Deploy = "its tables are created, expanded and contracted from the Deploy tab of the store workspace, as the Runner tasks deploy and contract of the model's package (dbdatastore --runner is that task's runtime)";
    private const string Rows = "its rows are written on the Rows tab against a Gateway's store module, and change requests are decided on the console's Approvals page";

    /// <summary>The server's commands, each with where its feature went.</summary>
    public static RemovedCommands Removed { get; } = new(Program, new Dictionary<string, string>
    {
        ["server"] = $"the Store server was deleted (plan 33 SC23). {Capitalised(Authoring)}; {Deploy}; {Rows}.",
        ["service"] = "the Store server was deleted (plan 33 SC23), so there is no service to register: the install is four services (console, Workspace, Gateway, Runner), and a Runner starts dbdatastore --runner for a model's tasks.",
        ["health"] = "the Store server was deleted (plan 33 SC23), so there is no server to check: check the Workspace, the Gateway and the Runner that took over its work.",
        ["state"] = "the Store server was deleted (plan 33 SC23) with its state database: a model's runs are the Runner's (its Runs page), a row's history and audit are kept by the Gateway's store module.",
        ["secret"] = "the Store server was deleted (plan 33 SC23) with its secret store: the login a model's storage is opened with is bound to the slot storage on the Runner (its Packages page), and the Gateway keeps its own data sources' logins.",
    });

    /// <summary>What the program is now, first in its help.</summary>
    public static string About => $"{Program} is the runtime a Runner starts for the tasks of a Store model's package ({Program} {StoreRunnerEntry.Flag}) and a command line on a folder of models. The Store server was deleted (plan 33 SC23): {Authoring}; {Deploy}; {Rows}.";

    /// <summary>The command line on a folder (<c>--repo</c>, by default <see cref="StoreSettings.DefaultDirectory"/>): <c>model put|publish|unpublish</c>, <c>storage create</c>, <c>gateway add|list|remove</c>.</summary>
    public static NounRouter Router(Func<string, string?>? environment = null) => new(Program,
        [StoreAdminCommand.Model(), StoreAdminCommand.Storage(), StoreAdminCommand.Proxy()],
        typeof(StoreCli).Assembly.GetName().Version?.ToString() ?? "0", () => DbDataKit.State.SuiteEnvironment.OldNameProblem(environment));

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (await Removed.TryAnswerAsync(args, error) is { } removed) return removed;
        if (args is [] or ["help" or "--help" or "-h"]) await output.WriteLineAsync(About + "\n");
        return await Router().RunAsync(args, new CliContext(output, error, Console.In));
    }

    private static string Capitalised(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
