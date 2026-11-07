using System.Text.RegularExpressions;

namespace DbDataConsole.Server;

/// <summary>
/// The kinds of server this console manages, as each reports itself (<c>/version</c>, <c>/api/v1/status</c>): what an announcement may name, and for a runner which of its calls the relay passes on. A runner's API has calls
/// that are for its publishers and for the serving layer (uploading a file, publishing a version, a webhook, a setting); a console is not their client, so the relay does not pass them, whoever signed in: the screens use the calls below.
/// </summary>
public static partial class ServerProducts
{
    public const string Build = "dbdatabuild";
    public const string Sync = "dbdatasync";
    public const string Proxy = "dbdataproxy";
    public const string Store = "dbdatastore";
    /// <summary>The runner (plan 28): scheduled and batch work, the tasks of published packages.</summary>
    public const string Runner = "dbdatarunner";

    /// <summary>The products a server may announce itself as.</summary>
    public static readonly IReadOnlyList<string> Announced = [Build, Sync, Proxy, Store, Runner];

    /// <summary>
    /// Whether the relay passes a call to a server of this product. Only a runner is limited: its documented management calls are the ones the console's screens make (status, the group's instances, runs, run now, pause and resume a task,
    /// the packages with their versions, and activate, switch, rollback and retire a package). The roles are the runner's own: the token carries the signed-in person's, and the runner refuses what they may not do.
    /// </summary>
    public static bool Relayable(string? product, string method, string path)
    {
        if (product != Runner) return true;
        var route = path.Split('?', 2)[0];
        return method.ToUpperInvariant() switch
        {
            "GET" => RunnerReads().IsMatch(route),
            "POST" => RunnerActs().IsMatch(route),
            _ => false,
        };
    }

    [GeneratedRegex(@"^api/v1/(status|instances|connections|runs|settings|definitions/integration(/[A-Za-z0-9._-]+)?)/?$")]
    private static partial Regex RunnerReads();

    [GeneratedRegex(@"^api/v1/(tasks/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/(run|pause|resume)|definitions/integration/[A-Za-z0-9._-]+/(activate|switch|rollback|retire))/?$")]
    private static partial Regex RunnerActs();
}
