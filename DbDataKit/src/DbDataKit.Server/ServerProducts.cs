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
    public const string Gateway = "dbdatagateway";
    /// <summary>The runner (plan 28): scheduled and batch work, the tasks of published packages.</summary>
    public const string Runner = "dbdatarunner";
    /// <summary>The Workspace server (plan 22a GP31): projects and the git workspaces people author them in.</summary>
    public const string Workspace = "dbdataworkspace";

    /// <summary>The products a server may announce itself as (the four services of the suite but the console itself; the Build, Sync and Store servers were deleted, <see cref="RemovedProducts"/>).</summary>
    public static readonly IReadOnlyList<string> Announced = [Gateway, Runner, Workspace];

    /// <summary>One call of a runner's API the relay may pass: the method, the route (the path without its query) and the role the runner asks of the caller. <see cref="Or"/> is the other role the runner accepts for the call (a publisher may upload and publish).</summary>
    public sealed record RunnerRoute(string Method, string Pattern, ServerRole Role, ServerRole Or = ServerRole.None)
    {
        private const string Name = "[A-Za-z0-9._-]+";

        /// <summary>The pattern as an expression over the path: <c>{name}</c> is one segment of letters, digits and <c>. _ -</c>, <c>{secret}</c> a secret name (colons too, escaped or not), <c>{sha}</c> 64 hexadecimal digits, <c>{id}</c> an instance id.</summary>
        public Regex Route { get; } = new("^" + Pattern.Replace("{name}", Name).Replace("{secret}", "[A-Za-z0-9._:%-]+").Replace("{sha}", "[0-9a-fA-F]{64}").Replace("{id}", "[A-Za-z0-9-]+") + "/?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        new("GET", "api/v1/quality", ServerRole.Viewer),                                                    // plan 35 I3: the checks its tasks evaluated, the latest result and block of each
        new("GET", "api/v1/quality/history", ServerRole.Viewer),                                            // plan 35 I3: the newest results of one check, with the run that made each
        new("GET", "api/v1/state", ServerRole.Viewer),                                                      // 29b C05: its state database, the modules, the instances of its group
        new("GET", "api/v1/state/maintenance", ServerRole.Viewer),                                          // 29b C05: maintenance mode and whether it is idle
        new("GET", "api/v1/state/move", ServerRole.Viewer),                                                 // 29b C06: the progress of a move of its state
        new("GET", "api/v1/upgrade", ServerRole.Viewer),                                                    // UC09: the status of its own upgrade
        new("GET", "api/v1/certificate", ServerRole.Viewer),                                                // UI10: days left and subject of the certificate it presents, for Monitoring
        new("GET", "api/v1/secrets/overview", ServerRole.Admin),                                            // UI06: names, dates and references of the secrets and what names them, never a value
        new("PUT", "api/v1/secrets/{secret}", ServerRole.Admin),                                            // E20d: a run login set on the Packages page (BindingsCard); the group's state keeps it when the group has a key; write-only
        new("GET", "api/v1/group/key", ServerRole.Viewer),                                                  // E20d: whether the instance answering holds the group's key (never the key)
        new("GET", "api/v1/definitions/integration", ServerRole.Viewer),
        new("GET", "api/v1/definitions/integration/{name}", ServerRole.Viewer),
        new("GET", "api/v1/instances/{id}/labels", ServerRole.Viewer),                                      // UE11
        new("GET", "api/v1/tasks/{name}/{name}/{name}/schedule", ServerRole.Viewer),                       // UE06
        new("GET", "api/v1/tasks/{name}/{name}/{name}/decisions", ServerRole.Viewer),                      // UE06
        new("HEAD", "api/v1/artifacts/{sha}", ServerRole.Viewer, ServerRole.Publisher),                    // UE10: does the runner have this file
        new("GET", "api/v1/artifacts/{sha}", ServerRole.Viewer),                                           // PB06: a file by hash, which the console reads from one group to send to another
        new("GET", "api/v1/definitions/integration/{name}/versions/{name}", ServerRole.Viewer),             // PB06: one version whole (manifest, files, dependencies, signature), what a promotion publishes in another group
        new("GET", "api/v1/trust/integration", ServerRole.Viewer),                                       // the keys the runner trusts for packages: whether a deploy signs with the console's publisher key
        new("GET", "api/v1/posture-choices", ServerRole.Viewer),                                                    // R19: the loosened defaults with their effect and the recorded choices
        new("GET", "api/v1/runs/{id}/units", ServerRole.Viewer),                                            // R25: the table of units of a run
        new("GET", "api/v1/runs/{id}/steps", ServerRole.Viewer),                                            // R25: the steps of its attempt chain
        new("GET", "api/v1/tasks/{name}/{name}/{name}/steps/{name}", ServerRole.Viewer),                    // the results of a task's completed steps of one name: what a run started from a Workspace project's screens recorded
        // what the kit leaves to a person and the checkpoints (operator: they name what a task refused or where it got to)
        new("GET", "api/v1/tasks/{name}/{name}/{name}/in-doubt", ServerRole.Operator),                      // R25
        new("GET", "api/v1/tasks/{name}/{name}/{name}/dead-letters", ServerRole.Operator),                  // R25
        new("GET", "api/v1/tasks/{name}/{name}/{name}/checkpoints", ServerRole.Operator),                   // 29a Q20
        new("GET", "api/v1/tasks/{name}/{name}/{name}/checkpoints/{name}/history", ServerRole.Operator),    // 29a Q20
        // run now, pause, resume, and who schedules a task (operator)
        new("POST", "api/v1/tasks/{name}/{name}/{name}/run", ServerRole.Operator),
        new("GET", "api/v1/runs/{id}/answer", ServerRole.Operator),                                         // the answer of a run the person started (a preview's rows), taken once and kept nowhere
        new("POST", "api/v1/quality/run", ServerRole.Operator),                                            // plan 35 I3a: re-check a dataset (queues the `check` task of the package that recorded its results)
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
        new("POST", "api/v1/quality/release", ServerRole.Admin),                                           // plan 35 I3: release the block a failing check set (audited by the runner)
        new("POST", "api/v1/state/maintenance", ServerRole.Admin),                                         // 29b C05: put it into maintenance (it claims nothing new and reports idle) or out of it; audited by the runner
        new("POST", "api/v1/state/move", ServerRole.Admin),                                                // 29b C06: move its state to another database (in maintenance and idle); audited by the runner
        new("POST", "api/v1/upgrade", ServerRole.Admin),                                                  // UC09: prepare and apply its own upgrade
    ];

    /// <summary>
    /// The calls of a Gateway's shared publish API that a console makes when it promotes a definition or a release moves one (plan 21 CH02, plan 30 PB06): each with the role the Gateway itself asks (<c>DefinitionsApi</c>: a viewer reads, a publisher uploads and
    /// publishes its own, an administrator does the rest). The rest of a Gateway's API is relayed as before (its screens' calls); these rows are the ones the relay judges against what the signed-in person holds before it forwards, and
    /// <c>GatewayRolesTests</c> pins each row to what a real Gateway answers. <c>{name}</c> is a kind or a definition name.
    /// </summary>
    public static readonly IReadOnlyList<RunnerRoute> GatewayRoutes =
    [
        new("GET", "api/v1/definitions/{name}", ServerRole.Viewer),
        new("GET", "api/v1/definitions/{name}/{name}", ServerRole.Viewer),
        new("GET", "api/v1/definitions/{name}/{name}/versions/{name}", ServerRole.Viewer),                  // what a promotion reads: one version whole
        new("PUT", "api/v1/definitions/{name}/{name}", ServerRole.Admin, ServerRole.Publisher),             // what a promotion publishes in the target group
        new("POST", "api/v1/definitions/{name}/{name}/activate", ServerRole.Admin),
        new("POST", "api/v1/definitions/{name}/{name}/switch", ServerRole.Admin),
        new("POST", "api/v1/definitions/{name}/{name}/rollback", ServerRole.Admin),
        new("POST", "api/v1/definitions/{name}/{name}/retire", ServerRole.Admin),
        new("POST", "api/v1/definitions/{name}/{name}/prune", ServerRole.Admin),
        new("HEAD", "api/v1/artifacts/{sha}", ServerRole.Viewer, ServerRole.Publisher),                    // does the Gateway have this file
        new("GET", "api/v1/artifacts/{sha}", ServerRole.Viewer, ServerRole.Publisher),                     // a file by hash, read from one group to send to another
        new("PUT", "api/v1/artifacts/{sha}", ServerRole.Admin, ServerRole.Publisher),
        new("PUT", "api/v1/bindings/{name}/{name}/{name}", ServerRole.Admin, ServerRole.Publisher),         // a channel's own bindings, written before a promoted version is published
        new("DELETE", "api/v1/bindings/{name}/{name}/{name}/{name}", ServerRole.Admin, ServerRole.Publisher),
        new("GET", "api/v1/settings/handovers", ServerRole.Viewer),                                         // what one paired client owns, handed to another
        new("PUT", "api/v1/settings/handovers/{name}", ServerRole.Admin),
        new("DELETE", "api/v1/settings/handovers/{name}", ServerRole.Admin),
        new("GET", "api/v1/clients", ServerRole.Admin),                                                      // the paired server clients: the Workspace servers' publisher keys among them
    ];

    /// <summary>The row of <see cref="GatewayRoutes"/> that a call matches, or null (a call of a Gateway's screens that is not a publish call).</summary>
    public static RunnerRoute? GatewayRouteOf(string method, string path)
    {
        var route = path.Split('?', 2)[0];
        return GatewayRoutes.FirstOrDefault(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && r.Route.IsMatch(route));
    }

    /// <summary>The row of the product's table that a call matches, for the products whose relay judges a call by the person's roles route by route: a runner's and, for its publish calls, a Gateway's.</summary>
    public static RunnerRoute? RouteOf(string? product, string method, string path) =>
        product == Runner ? RunnerRouteOf(method, path) : product == Gateway ? GatewayRouteOf(method, path) : null;

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
        ("GET", "api/v1/state", ServerRole.Viewer),                 // 29b C05: its state database (the console's state panel; plan 33 SC23 moved the state-move story onto a Workspace when it deleted the Store server)
        ("GET", "api/v1/state/maintenance", ServerRole.Viewer),     // 29b C05: maintenance mode and whether it is idle
        ("POST", "api/v1/state/maintenance", ServerRole.Admin),     // 29b C05: put it into maintenance or out of it; audited by the server
        ("GET", "api/v1/state/move", ServerRole.Viewer),            // 29b C06: the progress of a move of its state
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
        ("POST", "api/v1/projects/{p}/workspaces/{id}/import-preview", ServerRole.Viewer),   // plan 33 SC20k: the mapped models the catalog cache would make, on a copy
        ("POST", "api/v1/projects/{p}/workspaces/{id}/lineage", ServerRole.Viewer),   // plan 33 SC20h: the column lineage of a Build draft, read only
        // plan 33 SC22k: a Sync draft's scripts (read); the route that tested one answers that a script is tested on a Runner (the set's task script-test, through the console's deploy)
        ("GET", "api/v1/projects/{p}/workspaces/{id}/scripts", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/script-test", ServerRole.Author),
        // plan 33 SC22j: the provisioning plan of a Sync draft from its catalog cache (offline, read only)
        ("POST", "api/v1/projects/{p}/workspaces/{id}/provisioning-plan", ServerRole.Viewer),
        // the sync Designer's questions of a draft, answered by the Workspace from Sync's libraries and the draft's catalog cache: the script slots, a connection's
        // capabilities and a mapping's inferred types and key (read), and Create mappings, whose files the author keeps as unsaved edits
        ("GET", "api/v1/projects/{p}/workspaces/{id}/sync/script-slots", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/sync/capabilities", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/sync/inferred-columns", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/sync/bulk-create", ServerRole.Author),
        ("GET", "api/v1/app-build", ServerRole.Viewer),
        ("POST", "api/v1/projects/{p}/workspaces/{id}/builds", ServerRole.Author),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/preview", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/preview/{path}", ServerRole.Viewer),
        // what a build holds to send and its record, and each file by hash: what the console reads to deploy a build as the person
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/contents", ServerRole.Viewer),
        ("GET", "api/v1/projects/{p}/workspaces/{id}/builds/{b}/files/{name}", ServerRole.Viewer),
        // the routes that sent a build to a Runner or a Gateway, ran its tasks there, read a Gateway's catalog or kept publisher keys are gone from the Workspace
        // server (it answers 410 naming the console's call); the console does them itself as the person (/api/deploys, /api/workspace-catalog)
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
        ? $"{name} is a Workspace server: the console relays only the calls its screens make (projects, workspaces and their files, changes, history, commit, push and merge, builds and previews). The console deploys a build, runs its tasks and reads a Gateway's catalog itself: POST /api/deploys, /api/deploys/{{id}}/..., /api/workspace-catalog/..."
        : $"{name} is a runner: the console relays only its management calls (status, instances, runs, tasks, packages and their life cycle).";

    /// <summary>A relayed path as it goes to the server: each part escaped, because the relay holds the decoded one and a file name may hold <c>#</c>, <c>?</c> or <c>%</c>.</summary>
    public static string EscapedPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
