using System.Text.RegularExpressions;
using DbDataKit.Web.Trust;

namespace DbDataConsole.Server;

/// <summary>
/// The kinds of server this console manages, as each reports itself (<c>/version</c>, <c>/api/v1/status</c>): what an announcement may name, and for a runner which of its calls the relay passes on. A runner's API has calls
/// that are for a product's reports and for the serving layer (a decision reported by a product, a webhook, a setting other than the one the Packages screen switches); a console is not their client, so the relay does not pass them, whoever signed in: the screens use the calls listed in <see cref="RunnerRoutes"/>, and only for a person who holds the role each asks for.
/// </summary>
public static class ServerProducts
{
    public const string Build = "dbdatabuild";
    public const string Sync = "dbdatasync";
    public const string Gateway = "dbdatagateway";
    public const string Store = "dbdatastore";
    /// <summary>The runner (plan 28): scheduled and batch work, the tasks of published packages.</summary>
    public const string Runner = "dbdatarunner";

    /// <summary>The products a server may announce itself as.</summary>
    public static readonly IReadOnlyList<string> Announced = [Build, Sync, Gateway, Store, Runner];

    /// <summary>One call of a runner's API the relay may pass: the method, the route (the path without its query) and the role the runner asks of the caller. <see cref="Or"/> is the other role the runner accepts for the call (a publisher may upload and publish).</summary>
    public sealed record RunnerRoute(string Method, string Pattern, ServerRole Role, ServerRole Or = ServerRole.None)
    {
        private const string Name = "[A-Za-z0-9._-]+";

        /// <summary>The pattern as an expression over the path: <c>{name}</c> is one segment of letters, digits and <c>. _ -</c>, <c>{sha}</c> 64 hexadecimal digits, <c>{id}</c> an instance id.</summary>
        public Regex Route { get; } = new("^" + Pattern.Replace("{name}", Name).Replace("{sha}", "[0-9a-fA-F]{64}").Replace("{id}", "[A-Za-z0-9-]+") + "/?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Whether a person who holds <paramref name="held"/> may make the call (the runner's own rule, with the implications of the roles: an administrator operates and reads).</summary>
        public bool Allows(ServerRole held) => ServerRoles.Allows(held, Role) || (Or != ServerRole.None && ServerRoles.Allows(held, Or));

        /// <summary>The roles that would do, for the message that refuses without them.</summary>
        public string Needs => Or == ServerRole.None ? Role.ToString().ToLowerInvariant() : $"{Role.ToString().ToLowerInvariant()} (or {Or.ToString().ToLowerInvariant()})";
    }

    /// <summary>
    /// The calls of a runner's API that a console relays, one row each (plan 33 UE06, UE08, UE10 and UE11, plan 24a UC09 and the screens before them), with the role the runner itself asks for: owner decision of 2026-11-08, a console writes to a runner route by route
    /// and as the signed-in person, whose roles the relay checks before it forwards anything. Every other call of a runner (a product's report of its decisions, a webhook, any other write, any other method) is not a console's, whoever signed in.
    /// The roles are those of <c>DbDataKit.Runner.Api</c> (<c>RunnerApi</c>, <c>RunnerGroupApi</c>, <c>RunnerPublishApi</c>, <c>RunnerLineageApi</c>); a test pins each row to what the runner answers.
    /// </summary>
    public static readonly IReadOnlyList<RunnerRoute> RunnerRoutes =
    [
        // reads (viewer)
        new("GET", "api/v1/status", ServerRole.Viewer),
        new("GET", "api/v1/instances", ServerRole.Viewer),
        new("GET", "api/v1/connections", ServerRole.Viewer),
        new("GET", "api/v1/runs", ServerRole.Viewer),
        new("GET", "api/v1/settings", ServerRole.Viewer),
        new("GET", "api/v1/lineage", ServerRole.Viewer),
        new("GET", "api/v1/upgrade", ServerRole.Viewer),                                                    // UC09: the status of its own upgrade
        new("GET", "api/v1/definitions/integration", ServerRole.Viewer),
        new("GET", "api/v1/definitions/integration/{name}", ServerRole.Viewer),
        new("GET", "api/v1/instances/{id}/labels", ServerRole.Viewer),                                      // UE11
        new("GET", "api/v1/tasks/{name}/{name}/{name}/schedule", ServerRole.Viewer),                       // UE06
        new("GET", "api/v1/tasks/{name}/{name}/{name}/decisions", ServerRole.Viewer),                      // UE06
        new("HEAD", "api/v1/artifacts/{sha}", ServerRole.Viewer, ServerRole.Publisher),                    // UE10: does the runner have this file
        // run now, pause, resume, and who schedules a task (operator)
        new("POST", "api/v1/tasks/{name}/{name}/{name}/run", ServerRole.Operator),
        new("POST", "api/v1/tasks/{name}/{name}/{name}/pause", ServerRole.Operator),
        new("POST", "api/v1/tasks/{name}/{name}/{name}/resume", ServerRole.Operator),
        new("PUT", "api/v1/tasks/{name}/{name}/{name}/owner", ServerRole.Operator),                        // UE06
        // a package's life cycle (admin)
        new("POST", "api/v1/definitions/integration/{name}/activate", ServerRole.Admin),
        new("POST", "api/v1/definitions/integration/{name}/switch", ServerRole.Admin),
        new("POST", "api/v1/definitions/integration/{name}/rollback", ServerRole.Admin),
        new("POST", "api/v1/definitions/integration/{name}/retire", ServerRole.Admin),
        // publishing, binding, settings and labels (admin; a publisher may also upload and publish)
        new("PUT", "api/v1/artifacts/{sha}", ServerRole.Admin, ServerRole.Publisher),                      // UE10
        new("PUT", "api/v1/definitions/integration/{name}", ServerRole.Admin, ServerRole.Publisher),       // UE10
        new("PUT", "api/v1/bindings/{name}/integration/{name}", ServerRole.Admin, ServerRole.Publisher),   // UE08
        new("PUT", "api/v1/settings/require_signatures", ServerRole.Admin),                                // UE08
        new("PUT", "api/v1/instances/{id}/labels", ServerRole.Admin),                                      // UE11
        new("POST", "api/v1/upgrade", ServerRole.Admin),                                                   // UC09: prepare and apply its own upgrade
    ];

    /// <summary>The row of <see cref="RunnerRoutes"/> that a call matches, or null when a console does not relay that call to a runner.</summary>
    public static RunnerRoute? RunnerRouteOf(string method, string path)
    {
        var route = path.Split('?', 2)[0];
        return RunnerRoutes.FirstOrDefault(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && r.Route.IsMatch(route));
    }

    /// <summary>
    /// Whether the relay passes a call to a server of this product. Only a runner is limited: the rows of <see cref="RunnerRoutes"/>, each with the role the signed-in person must hold (the relay checks it and the runner checks it again).
    /// </summary>
    public static bool Relayable(string? product, string method, string path) => product != Runner || RunnerRouteOf(method, path) is not null;
}
