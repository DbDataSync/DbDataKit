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
    public const string Gateway = "dbdatagateway";
    public const string Store = "dbdatastore";
    /// <summary>The runner (plan 28): scheduled and batch work, the tasks of published packages.</summary>
    public const string Runner = "dbdatarunner";

    /// <summary>The products a server may announce itself as.</summary>
    public static readonly IReadOnlyList<string> Announced = [Build, Sync, Gateway, Store, Runner];

    /// <summary>
    /// Whether the relay passes a call to a server of this product. Only a runner is limited: its documented management calls are the ones the console's screens make (status, the group's instances, runs, run now, pause and resume a task,
    /// who schedules a task and the decisions of an observed one, the packages with their versions, activate, switch, rollback and retire a package, bind a package's connection slots and the require-signatures setting, label an instance, and
    /// publish a package from files: the upload of a file by its hash, and the publish). The roles are the runner's own: the token carries the signed-in person's, and the runner refuses what they may not do. What a runner reports back
    /// to a product (<c>POST .../decisions</c>), a webhook and the service-to-service calls are not here.
    /// </summary>
    public static bool Relayable(string? product, string method, string path)
    {
        if (product != Runner) return true;
        var route = path.Split('?', 2)[0];
        return method.ToUpperInvariant() switch
        {
            "GET" or "HEAD" => RunnerReads().IsMatch(route) || (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && RunnerArtifact().IsMatch(route)),
            "POST" => RunnerActs().IsMatch(route),
            "PUT" => RunnerWrites().IsMatch(route),
            _ => false,
        };
    }

    [GeneratedRegex(@"^api/v1/(status|instances|connections|runs|settings|lineage|definitions/integration(/[A-Za-z0-9._-]+)?|instances/[A-Za-z0-9-]+/labels|tasks/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/(schedule|decisions))/?$")]
    private static partial Regex RunnerReads();

    [GeneratedRegex(@"^api/v1/artifacts/[0-9a-fA-F]{64}/?$")]
    private static partial Regex RunnerArtifact();

    [GeneratedRegex(@"^api/v1/(artifacts/[0-9a-fA-F]{64}|definitions/integration/[A-Za-z0-9._-]+|bindings/[A-Za-z0-9._-]+/integration/[A-Za-z0-9._-]+|settings/require_signatures|instances/[A-Za-z0-9-]+/labels|tasks/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/owner)/?$")]
    private static partial Regex RunnerWrites();

    [GeneratedRegex(@"^api/v1/(tasks/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/(run|pause|resume)|definitions/integration/[A-Za-z0-9._-]+/(activate|switch|rollback|retire))/?$")]
    private static partial Regex RunnerActs();
}
