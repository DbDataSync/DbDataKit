using System.Text.RegularExpressions;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// The kinds of server this console manages, as each reports itself (<c>/version</c>, <c>/api/v1/status</c>): what an announcement may name, and for a runner and a Workspace server which of their calls the relay passes on. A runner's API has calls
/// that are for a product's reports and for the serving layer (a decision reported by a product, a webhook, a setting other than the one the Packages screen switches); a console is not their client, so the relay does not pass them, whoever signed in: the screens use the calls listed in <see cref="RunnerRoutes"/>, and only for a person who holds the role each asks for.
/// A Workspace server's relay is a table, route by route (<see cref="WorkspaceRoutes"/>): each route a screen uses, with the role it needs, which the relay checks against what the signed-in person holds before it forwards (and the server checks again).
/// </summary>
public static class ServerProducts
{
    public const string Build = "dbdatabuild";
    public const string Sync = "dbdatasync";
    public const string Gateway = "dbdatagateway";
    public const string Store = "dbdatastore";
    /// <summary>The runner (plan 28): scheduled and batch work, the tasks of published packages.</summary>
    public const string Runner = "dbdatarunner";
    /// <summary>The Workspace server (plan 22a GP31): projects and the git workspaces people author them in.</summary>
    public const string Workspace = "dbdataworkspace";

    /// <summary>The products a server may announce itself as.</summary>
    public static readonly IReadOnlyList<string> Announced = [Build, Sync, Gateway, Store, Runner, Workspace];

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
        new("GET", "api/v1/connection-copies", ServerRole.Viewer),                                          // IX23: the connections the console handed over, their version and whether their secret references resolve there
        new("PUT", "api/v1/connection-copies/{name}", ServerRole.Admin),                                   // IX23: send a connection's definition (secret references, never a value)
        new("GET", "api/v1/runs", ServerRole.Viewer),
        new("GET", "api/v1/runs/{id}", ServerRole.Viewer),                                                  // UI08: one run
        new("GET", "api/v1/runs/{id}/logs", ServerRole.Viewer),                                             // UI08: its output, a page at a time
        new("GET", "api/v1/settings", ServerRole.Viewer),
        new("GET", "api/v1/lineage", ServerRole.Viewer),
        new("GET", "api/v1/tracking", ServerRole.Viewer),                                                   // E18: the runner's copy of Build's tracking, its divergences and their common points
        new("GET", "api/v1/state", ServerRole.Viewer),                                                      // 29b C05: its state database, the modules, the instances of its group
        new("GET", "api/v1/state/maintenance", ServerRole.Viewer),                                          // 29b C05: maintenance mode and whether it is idle
        new("GET", "api/v1/state/move", ServerRole.Viewer),                                                 // 29b C06: the progress of a move of its state
        new("GET", "api/v1/upgrade", ServerRole.Viewer),                                                    // UC09: the status of its own upgrade
        new("GET", "api/v1/certificate", ServerRole.Viewer),                                                // UI10: days left and subject of the certificate it presents, for Monitoring
        new("GET", "api/v1/secrets/overview", ServerRole.Admin),                                            // UI06: names, dates and references of the secrets and what names them, never a value
        new("GET", "api/v1/definitions/integration", ServerRole.Viewer),
        new("GET", "api/v1/definitions/integration/{name}", ServerRole.Viewer),
        new("GET", "api/v1/instances/{id}/labels", ServerRole.Viewer),                                      // UE11
        new("GET", "api/v1/tasks/{name}/{name}/{name}/schedule", ServerRole.Viewer),                       // UE06
        new("GET", "api/v1/tasks/{name}/{name}/{name}/decisions", ServerRole.Viewer),                      // UE06
        new("HEAD", "api/v1/artifacts/{sha}", ServerRole.Viewer, ServerRole.Publisher),                    // UE10: does the runner have this file
        new("GET", "api/v1/posture-choices", ServerRole.Viewer),                                                    // R19: the loosened defaults with their effect and the recorded choices
        new("GET", "api/v1/runs/{id}/units", ServerRole.Viewer),                                            // R25: the table of units of a run
        new("GET", "api/v1/runs/{id}/steps", ServerRole.Viewer),                                            // R25: the steps of its attempt chain
        // what the kit leaves to a person and the checkpoints (operator: they name what a task refused or where it got to)
        new("GET", "api/v1/tasks/{name}/{name}/{name}/in-doubt", ServerRole.Operator),                      // R25
        new("GET", "api/v1/tasks/{name}/{name}/{name}/dead-letters", ServerRole.Operator),                  // R25
        new("GET", "api/v1/tasks/{name}/{name}/{name}/checkpoints", ServerRole.Operator),                   // 29a Q20
        new("GET", "api/v1/tasks/{name}/{name}/{name}/checkpoints/{name}/history", ServerRole.Operator),    // 29a Q20
        // run now, pause, resume, and who schedules a task (operator)
        new("POST", "api/v1/tasks/{name}/{name}/{name}/run", ServerRole.Operator),
        new("POST", "api/v1/tasks/{name}/{name}/{name}/pause", ServerRole.Operator),
        new("POST", "api/v1/tasks/{name}/{name}/{name}/resume", ServerRole.Operator),
        new("PUT", "api/v1/tasks/{name}/{name}/{name}/owner", ServerRole.Operator),                        // UE06
        new("POST", "api/v1/tasks/{name}/{name}/{name}/steps/{name}/resolve", ServerRole.Operator),         // R25: say whether an in-doubt step happened (audited by the runner)
        new("POST", "api/v1/tasks/{name}/{name}/{name}/dead-letters/{name}/replay", ServerRole.Operator),   // R25
        new("POST", "api/v1/tasks/{name}/{name}/{name}/dead-letters/{name}/discard", ServerRole.Operator),  // R25
        new("POST", "api/v1/tasks/{name}/{name}/{name}/checkpoints/{name}/rewind", ServerRole.Operator),    // 29a Q20
        // a package's life cycle (admin)
        new("POST", "api/v1/definitions/integration/{name}/activate", ServerRole.Admin),
        new("POST", "api/v1/definitions/integration/{name}/switch", ServerRole.Admin),
        new("POST", "api/v1/definitions/integration/{name}/rollback", ServerRole.Admin),
        new("POST", "api/v1/definitions/integration/{name}/retire", ServerRole.Admin),
        // publishing, binding, settings and labels (admin; a publisher may also upload and publish)
        new("PUT", "api/v1/artifacts/{sha}", ServerRole.Admin, ServerRole.Publisher),                      // UE10
        new("PUT", "api/v1/definitions/integration/{name}", ServerRole.Admin, ServerRole.Publisher),       // UE10
        new("PUT", "api/v1/bindings/{name}/integration/{name}", ServerRole.Admin, ServerRole.Publisher),   // UE08
        new("DELETE", "api/v1/bindings/{name}/integration/{name}/{name}", ServerRole.Admin, ServerRole.Publisher),   // IX23: empty a slot's channel
        new("POST", "api/v1/bindings/{name}/integration/{name}/test", ServerRole.Admin),                    // UI08: try a slot's binding (never a value in the answer)
        new("PUT", "api/v1/settings/require_signatures", ServerRole.Admin),                                // UE08
        new("PUT", "api/v1/settings/require_sandbox_run", ServerRole.Admin),                               // IX16
        new("PUT", "api/v1/posture-choices", ServerRole.Admin),                                                    // R19: choose a loosened default (confirmed) or take it back; audited by the runner
        new("PUT", "api/v1/instances/{id}/labels", ServerRole.Admin),                                      // UE11
        new("POST", "api/v1/tracking/divergences/{id}/resolve", ServerRole.Admin),                         // E18: archive or reset the runner's copy from the common point (audited by the runner)
        new("POST", "api/v1/state/maintenance", ServerRole.Admin),                                         // 29b C05: put it into maintenance (it claims nothing new and reports idle) or out of it; audited by the runner
        new("POST", "api/v1/state/move", ServerRole.Admin),                                                // 29b C06: move its state to another database (in maintenance and idle); audited by the runner
        new("POST", "api/v1/upgrade", ServerRole.Admin),                                                  // UC09: prepare and apply its own upgrade
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
    public static bool Relayable(string? product, string method, string path) =>
        product == Workspace ? RoleNeeded(product, method, path) is not null : product != Runner || RunnerRouteOf(method, path) is not null;

    /// <summary>
    /// The relay's table for a Workspace server, route by route: what a screen calls, and the role the call needs. Only these pass: a call this table does not name is refused whoever signed in. <c>{p}</c> is a project, <c>{id}</c> a workspace and
    /// <c>{b}</c> a build.
    /// </summary>
    public static IReadOnlyList<(string Method, string Route, ServerRole Needed)> WorkspaceRoutes { get; } =
    [
        ("GET", "api/v1/projects", ServerRole.Viewer),
        ("GET", "api/v1/certificate", ServerRole.Viewer),   // UI10: days left and subject of the certificate it presents, for Monitoring
        ("POST", "api/v1/projects", ServerRole.Admin),
        ("GET", "api/v1/projects/{p}/workspaces", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces", ServerRole.Author),
        ("DELETE", "api/v1/projects/{p}/workspaces/{id}", ServerRole.Author),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/events", ServerRole.Viewer),
        // GP35a: the files of a draft, what changed in it, its history, and the git steps; the server limits a path again (inside the draft, no .git, text of at most 1 MiB)
        ("GET", "api/v1/projects/{p}/workspaces/{id}/files", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/files/{path}", ServerRole.Viewer),
        ("PUT", "api/v1/projects/{p}/workspaces/{id}/files/{path}", ServerRole.Author),
        ("DELETE", "api/v1/projects/{p}/workspaces/{id}/files/{path}", ServerRole.Author),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/move", ServerRole.Author),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/changes", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/history", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/file-history/{path}", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/file-at/{sha}/{path}", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/commit", ServerRole.Author),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/push", ServerRole.Author),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/merge", ServerRole.Approver),
        // GP35b: what a server can build with (the package store and what is missing), the builds of a draft and each one's result, the preview of a build (read only, a sandbox with no network) and its publish to a paired Gateway
        // plan 33 SC20a: the kinds of project (what a new project can be and where each publishes) and the check of a draft by its kind's validators
        ("GET", "api/v1/project-kinds", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/check", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/compile", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/lowered", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/sample", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/lineage", ServerRole.Viewer),   // plan 33 SC20h: the column lineage of a Build draft, read only
        // plan 33 SC22k: a Sync draft's scripts (read) and the test of one (it runs the draft's C# in a child process, so the author's)
        ("GET", "api/v1/projects/{p}/workspaces/{id}/scripts", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/script-test", ServerRole.Author),
        // plan 33 SC22j: the provisioning plan of a Sync draft from its catalog cache (offline, read only)
        ("POST", "api/v1/projects/{p}/workspaces/{id}/provisioning-plan", ServerRole.Viewer),
        ("GET", "api/v1/app-build", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/builds", ServerRole.Author),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/preview", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/preview/{path}", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/publish", ServerRole.Approver),
        // GP35d: send the build to the Gateway's preview channel (a read-only preview instance with the instance's data); nothing goes to production, so the author may
        ("POST", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/preview-data", ServerRole.Author),
        ("GET", "api/v1/publish-targets", ServerRole.Approver),
        // the publisher's key reference: which secret holds the key of a target and which Gateway it is paired with; an administrator's, audited at the server. The key itself is never in a call.
        ("GET", "api/v1/publisher-keys", ServerRole.Admin),
        ("PUT", "api/v1/publisher-keys/{name}", ServerRole.Admin),
        ("POST", "api/v1/publisher-keys/{name}/pair", ServerRole.Admin),
        ("DELETE", "api/v1/publisher-keys/{name}", ServerRole.Admin),
    ];

    private static readonly IReadOnlyList<(string Method, Regex Pattern, ServerRole Needed)> WorkspaceTable =
        [.. WorkspaceRoutes.Select(r => (r.Method, new Regex("^" + r.Route.Replace("{p}", "(?<p>[A-Za-z0-9._-]+)").Replace("{id}", "[A-Za-z0-9._-]+").Replace("{b}", "[0-9A-Fa-f-]{36}").Replace("{sha}", "[0-9A-Fa-f]{7,40}").Replace("{name}", "[A-Za-z0-9_-]+").Replace("{path}", ".+") + "/?$", RegexOptions.CultureInvariant), r.Needed))];

    /// <summary>The role the relay requires of the signed-in person for a call to a server of this product, or null when the relay does not pass the call (or, for a product whose server alone decides, does not judge it).</summary>
    public static ServerRole? RoleNeeded(string? product, string method, string path)
    {
        if (product != Workspace) return null;
        var route = path.Split('?', 2)[0];
        foreach (var (verb, pattern, needed) in WorkspaceTable)
            if (string.Equals(verb, method, StringComparison.OrdinalIgnoreCase) && pattern.IsMatch(route)) return needed;
        return null;
    }

    /// <summary>The project a Workspace route names, for the person's roles in that project; null for a route about the server.</summary>
    public static string? ProjectOf(string? product, string method, string path)
    {
        if (product != Workspace) return null;
        var route = path.Split('?', 2)[0];
        foreach (var (verb, pattern, _) in WorkspaceTable)
            if (string.Equals(verb, method, StringComparison.OrdinalIgnoreCase) && pattern.Match(route) is { Success: true } m && m.Groups["p"].Success) return m.Groups["p"].Value;
        return null;
    }

    /// <summary>What the relay tells a person whose call it does not pass.</summary>
    public static string RefusalFor(string? product, string name) => product == Workspace
        ? $"{name} is a Workspace server: the console relays only the calls its screens make (projects, workspaces and their files, changes, history, commit, push and merge, builds, previews, publishing and the publisher keys)."
        : $"{name} is a runner: the console relays only its management calls (status, instances, runs, tasks, packages and their life cycle).";

    /// <summary>A relayed path as it goes to the server: each part escaped, because the relay holds the decoded one and a file name may hold <c>#</c>, <c>?</c> or <c>%</c>.</summary>
    public static string EscapedPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
