using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>Where a command line finds the home it administers.</summary>
/// <param name="Usage">How the place is named on the command line, appended to every usage (for example <c>[--repo &lt;identity directory&gt;]</c>).</param>
/// <param name="Open">Opens the home's state database from the parsed arguments (the <c>repo</c> option among them).</param>
/// <param name="PublicUrl">The home's public address, which an invitation link starts with, when <c>--url</c> does not give it.</param>
public sealed record IdentityAdminLocation(string Usage, Func<CliArgs, StateDatabase> Open, Func<CliArgs, string> PublicUrl);

/// <summary>
/// The verbs of the <c>identity</c> noun that administer a home at the command line (plan 13a U15, 13c D30: commands only for now), the same in every host of the home: <c>dbdatakit identity</c> for a home that
/// runs on its own, <c>dbdataproxy identity</c> for the serving layer's (plan 20 O06). They work on the home's state database directly, so they need no running server.
/// </summary>
public static class IdentityVerbs
{
    public static IReadOnlyList<Verb> Admin(IdentityAdminLocation where) =>
    [
        new Verb("client-add", "Register a console or an app that signs people in through the home; the secret of a confidential client is shown once", EffectClass.RepoFilesOnly,
            $"--name <name> --redirect <uri>[,<uri>] [--logout-redirect <uri>[,<uri>]] [--public] {where.Usage}",
            (args, context) => ClientAddAsync(args, context, where),
            DataSchemas.Object([("client_id", DataSchemas.String), ("name", DataSchemas.String), ("secret", DataSchemas.String), ("confidential", DataSchemas.Boolean)], "client_id", "name", "confidential")),
        new Verb("client-list", "The registered clients", EffectClass.OfflineOnly, where.Usage,
            (args, context) => ClientListAsync(args, context, where),
            DataSchemas.Object([("clients", DataSchemas.ListOf(DataSchemas.Object([("client_id", DataSchemas.String), ("name", DataSchemas.String), ("redirect_uris", DataSchemas.ListOf(DataSchemas.String)), ("confidential", DataSchemas.Boolean)], "client_id", "name", "confidential")))], "clients")),
        new Verb("user-list", "The people the home knows: name, address, whether enabled, how many ways each can sign in, and the id that assign takes", EffectClass.OfflineOnly, where.Usage,
            (args, context) => UserListAsync(args, context, where),
            DataSchemas.Object([("users", DataSchemas.ListOf(DataSchemas.Object(
                [("id", DataSchemas.String), ("name", DataSchemas.String), ("role", DataSchemas.String), ("enabled", DataSchemas.Boolean), ("credentials", DataSchemas.Integer), ("email", DataSchemas.String), ("emailVerified", DataSchemas.Boolean)],
                "id", "name", "role", "enabled", "credentials")))], "users")),
        new Verb("invite", "Print an invitation to the home: for a new person (their address), or for one who has lost their way in (their id)", EffectClass.RepoFilesOnly,
            $"[--role admin|viewer] (--email <address> | --user <id>) [--url <the home's public address>] {where.Usage}",
            (args, context) => InviteAsync(args, context, where),
            DataSchemas.Object([("url", DataSchemas.String), ("expires", DataSchemas.String), ("role", DataSchemas.String)], "url", "expires", "role")),
        new Verb("assign", "Give a person roles on a server, one of its Build projects, or in an app (assigning again replaces; --roles none removes)", EffectClass.RepoFilesOnly,
            $"--user <id> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> {where.Usage}",
            (args, context) => AssignAsync(args, context, where),
            DataSchemas.Object([("user", DataSchemas.String), ("target", DataSchemas.String), ("roles", DataSchemas.ListOf(DataSchemas.String))], "user", "target", "roles")),
        new Verb("map-group", "Give everyone in an external group (an Entra group id, a Windows group) roles on a server, project or app (mapping again replaces; --roles none removes)", EffectClass.RepoFilesOnly,
            $"--source entra|windows --group <group> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> {where.Usage}",
            (args, context) => MapGroupAsync(args, context, where),
            DataSchemas.Object([("source", DataSchemas.String), ("group", DataSchemas.String), ("target", DataSchemas.String), ("roles", DataSchemas.ListOf(DataSchemas.String))], "source", "group", "target", "roles")),
    ];

    private static HashSet<string> Options(params string[] names) => [.. names, "repo"];

    private static string[]? Uris(string? text) => text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<int> ClientAddAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "public" }, Options("name", "redirect", "logout-redirect"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("name") is not { Length: > 0 } name || Uris(parsed.Get("redirect")) is not { Length: > 0 } redirects)
        {
            await context.Error.WriteLineAsync($"Usage: identity client-add --name <name> --redirect <uri>[,<uri>] [--logout-redirect <uri>[,<uri>]] [--public] {where.Usage}");
            return CliExit.Usage;
        }
        try
        {
            var (client, secret) = new OidcClients(where.Open(parsed)).Register(name, redirects, confidential: !parsed.Has("public"), Uris(parsed.Get("logout-redirect")));
            await context.Out.WriteLineAsync($"Registered {client.Name}: client id {client.ClientId}");
            if (secret is not null) await context.Out.WriteLineAsync($"Secret (shown once; give it to the console as the value of the variable --identity-secret-env names): {secret}");
            context.Data["client_id"] = client.ClientId;
            context.Data["name"] = client.Name;
            context.Data["confidential"] = client.Confidential;
            if (secret is not null) context.Data["secret"] = secret;
            return CliExit.Ok;
        }
        catch (ArgumentException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Usage;
        }
    }

    private static async Task<int> ClientListAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: identity client-list {where.Usage}"); return CliExit.Usage; }
        var all = new OidcClients(where.Open(parsed)).List();
        if (all.Count == 0) await context.Out.WriteLineAsync("No client yet. Register a console: identity client-add");
        foreach (var c in all) await context.Out.WriteLineAsync($"{c.Name}  {(c.Confidential ? "confidential" : "public")}  {string.Join(", ", c.RedirectUris)}  client id {c.ClientId}");
        context.Data["clients"] = all.Select(c => new Dictionary<string, object?> { ["client_id"] = c.ClientId, ["name"] = c.Name, ["redirect_uris"] = c.RedirectUris.ToList(), ["confidential"] = c.Confidential }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> UserListAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: identity user-list {where.Usage}"); return CliExit.Usage; }
        var users = new UserStore(where.Open(parsed));
        var all = users.List();
        if (all.Count == 0) await context.Out.WriteLineAsync("Nobody yet. The first administrator redeems the invitation in FIRST-RUN.txt, or run: identity invite --email <address>");
        foreach (var user in all)
            await context.Out.WriteLineAsync(
                $"{user.DisplayName}  {user.Role}  {(user.Enabled ? "enabled" : "disabled")}  {users.CredentialsOf(user.Id).Count} way(s) to sign in  " +
                $"{(user.EmailVerified ? user.Email : user.Email is null ? "no email" : user.Email + " (not verified)")}  id {user.Id}");
        context.Data["users"] = all.Select(u => new Dictionary<string, object?>
        {
            ["id"] = u.Id, ["name"] = u.DisplayName, ["role"] = u.Role.ToString(), ["enabled"] = u.Enabled, ["credentials"] = users.CredentialsOf(u.Id).Count, ["email"] = u.Email, ["emailVerified"] = u.EmailVerified,
        }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> InviteAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("role", "user", "email", "url"));
        var usage = $"Usage: identity invite [--role admin|viewer] (--email <address> | --user <id>) [--url <the home's public address>] {where.Usage}";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync(usage); return CliExit.Usage; }
        // a new account is for a person, and the address is how the same person is recognised whichever way they sign in (13a U16)
        var email = parsed.Get("email");
        if (parsed.Get("user") is null == string.IsNullOrWhiteSpace(email))
        {
            await context.Error.WriteLineAsync("Name who the invitation is for: --email <address> for a new person, or --user <id> to give someone another way in ('identity user-list' shows the ids).");
            return CliExit.Usage;
        }
        if (!Enum.TryParse<UserRole>(parsed.Get("role") ?? nameof(UserRole.Viewer), ignoreCase: true, out var role))
        {
            await context.Error.WriteLineAsync($"'{parsed.Get("role")}' is not a role. Use admin or viewer.");
            return CliExit.Usage;
        }
        var database = where.Open(parsed);
        var forUser = parsed.Get("user");
        if (forUser is not null && new UserStore(database).Get(forUser) is null)
        {
            await context.Error.WriteLineAsync($"No user has the id '{forUser}'. 'identity user-list' shows them.");
            return CliExit.Failure;
        }
        MintedInvite minted;
        try { minted = new InviteStore(database).Create(role, forUser, createdByUserId: null, email: email); }
        catch (AccountConflictException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }
        // the code goes in the fragment, which a browser never sends to a server and an access log never records
        var url = $"{(parsed.Get("url") ?? where.PublicUrl(parsed)).TrimEnd('/')}{IdentityHome.InvitePath}#{minted.Code}";
        await context.Out.WriteLineAsync($"An invitation to the identity home ({role}), valid until {minted.Invite.ExpiresAtUtc:u}:");
        await context.Out.WriteLineAsync($"    {url}");
        context.Data["url"] = url;
        context.Data["expires"] = minted.Invite.ExpiresAtUtc.UtcDateTime.ToString("O");
        context.Data["role"] = role.ToString();
        return CliExit.Ok;
    }

    /// <summary>The scope of an assignment from the flags: a server, a Build project of it, or an app. Null when the flags do not say exactly one.</summary>
    private static (string Description, Action<HomeDirectory, AssignmentSubject, ServerRole> Assign, Action<HomeDirectory, string, string, ServerRole> Map)? Scope(CliArgs parsed)
    {
        var server = parsed.Get("server");
        var app = parsed.Get("app");
        var project = parsed.Get("project");
        if (server is { Length: > 0 } && app is null)
            return ($"server {server}" + (project is { Length: > 0 } ? $" project {project}" : ""),
                (d, s, r) => d.Assign(s, server, r, project is { Length: > 0 } ? project : null),
                (d, src, g, r) => d.MapExternalGroup(src, g, server, r, project is { Length: > 0 } ? project : null));
        if (app is { Length: > 0 } && server is null && project is null)
            return ($"app {app}", (d, s, r) => d.AssignApp(s, app, r), (d, src, g, r) => d.MapExternalGroupToApp(src, g, app, r));
        return null;
    }

    private static bool TryRoles(string? text, out ServerRole roles)
    {
        roles = ServerRole.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        return ServerRoles.TryParse(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), out roles);
    }

    private static async Task<int> AssignAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("user", "server", "project", "app", "roles"));
        var usage = $"Usage: identity assign --user <id> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> {where.Usage}";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("user") is not { Length: > 0 } userId || Scope(parsed) is not { } scope || !TryRoles(parsed.Get("roles"), out var roles))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        var database = where.Open(parsed);
        if (new UserStore(database).Get(userId) is not { } user)
        {
            await context.Error.WriteLineAsync($"No user has the id '{userId}'. 'identity user-list' shows them.");
            return CliExit.Failure;
        }
        scope.Assign(new HomeDirectory(database), AssignmentSubject.User(user.Id), roles);
        var names = ServerRoles.Names(roles);
        await context.Out.WriteLineAsync($"{user.DisplayName} holds {(names.Count == 0 ? "no role" : string.Join(", ", names))} on {scope.Description} (what they hold through groups is added).");
        context.Data["user"] = user.Id;
        context.Data["target"] = scope.Description;
        context.Data["roles"] = names.ToList();
        return CliExit.Ok;
    }

    private static async Task<int> MapGroupAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("source", "group", "server", "project", "app", "roles"));
        var usage = $"Usage: identity map-group --source entra|windows --group <group> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> {where.Usage}";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("source") is not { Length: > 0 } source || parsed.Get("group") is not { Length: > 0 } group || Scope(parsed) is not { } scope || !TryRoles(parsed.Get("roles"), out var roles))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        try
        {
            scope.Map(new HomeDirectory(where.Open(parsed)), source, group, roles);
        }
        catch (ArgumentException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Usage;
        }
        var names = ServerRoles.Names(roles);
        await context.Out.WriteLineAsync($"Everyone in the {source} group '{group}' holds {(names.Count == 0 ? "no role (the mapping is removed)" : string.Join(", ", names))} on {scope.Description} from their next sign-in.");
        context.Data["source"] = source;
        context.Data["group"] = group;
        context.Data["target"] = scope.Description;
        context.Data["roles"] = names.ToList();
        return CliExit.Ok;
    }
}
