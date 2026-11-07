using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>Where a command line finds the home it administers.</summary>
/// <param name="Usage">How the place is named on the command line, appended to every usage (for example <c>[--repo &lt;identity directory&gt;]</c>).</param>
/// <param name="Open">Opens the home's state database from the parsed arguments (the <c>repo</c> option among them).</param>
/// <param name="PublicUrl">The home's public address, which an invitation link starts with, when <c>--url</c> does not give it.</param>
/// <param name="Tokens">The home's token lifetimes (its install's maximum); null: the defaults.</param>
public sealed record IdentityAdminLocation(string Usage, Func<CliArgs, StateDatabase> Open, Func<CliArgs, string> PublicUrl, Func<CliArgs, AccessTokenPolicy>? Tokens = null);

/// <summary>
/// The verbs of the <c>identity</c> noun that administer a home at the command line (plan 13a U15, 13c D30: commands only for now), the same in every host of the home: <c>dbdatakit identity</c> for a home that
/// runs on its own, <c>dbdataproxy identity</c> for the serving layer's (plan 20 O06). They work on the home's state database directly, so they need no running server.
/// </summary>
public static class IdentityVerbs
{
    public static Verb[] Admin(IdentityAdminLocation where) =>
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
        new Verb("service-account-add", "Make a service account for unattended work (a home user that signs in only with its tokens; give it roles with assign)", EffectClass.RepoFilesOnly,
            $"--name <name> [--description <text>] {where.Usage}",
            (args, context) => ServiceAccountAddAsync(args, context, where),
            DataSchemas.Object([("id", DataSchemas.String), ("name", DataSchemas.String)], "id", "name")),
        new Verb("service-account-list", "The service accounts, with the id that assign takes", EffectClass.OfflineOnly, where.Usage,
            (args, context) => ServiceAccountListAsync(args, context, where),
            DataSchemas.Object([("accounts", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("name", DataSchemas.String), ("description", DataSchemas.String), ("tokens", DataSchemas.Integer)], "id", "name", "tokens")))], "accounts")),
        new Verb("service-account-remove", "Remove a service account: its tokens are revoked and its user disabled (kept, so the audit still names it)", EffectClass.RepoFilesOnly,
            $"--name <name> {where.Usage}",
            (args, context) => ServiceAccountRemoveAsync(args, context, where),
            DataSchemas.Object([("name", DataSchemas.String)], "name")),
        new Verb("token-create", "Make a token for a service account; the token is shown once (a person makes their own at the identity home)", EffectClass.RepoFilesOnly,
            $"--account <service account> --name <name> [--days <n> | --no-expiry] {where.Usage}",
            (args, context) => TokenCreateAsync(args, context, where),
            DataSchemas.Object([("id", DataSchemas.String), ("token", DataSchemas.String), ("user_name", DataSchemas.String), ("expires", DataSchemas.String)], "id", "token", "user_name")),
        new Verb("token-list", "Personal access tokens and service accounts' tokens, anyone's or one user's (never the secrets)", EffectClass.OfflineOnly, $"[--user <id>] {where.Usage}",
            (args, context) => TokenListAsync(args, context, where),
            DataSchemas.Object([("tokens", DataSchemas.ListOf(DataSchemas.Object(
                [("id", DataSchemas.String), ("user", DataSchemas.String), ("name", DataSchemas.String), ("created", DataSchemas.String), ("expires", DataSchemas.String), ("revoked", DataSchemas.String), ("last_used", DataSchemas.String), ("active", DataSchemas.Boolean)],
                "id", "user", "name", "created", "active")))], "tokens")),
        new Verb("token-revoke", "Revoke anyone's token (it is refused from then on)", EffectClass.RepoFilesOnly, $"--id <token id> {where.Usage}",
            (args, context) => TokenRevokeAsync(args, context, where),
            DataSchemas.Object([("id", DataSchemas.String)], "id")),
    ];

    /// <summary>Who made something at the command line: the operator of the machine, not a person of the home.</summary>
    private const string CommandLine = "command-line";

    private static AccessTokens Tokens(CliArgs parsed, IdentityAdminLocation where)
    {
        var database = where.Open(parsed);
        return new AccessTokens(database, new IdentityUserStore(database), where.Tokens?.Invoke(parsed));
    }

    private static async Task<int> ServiceAccountAddAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("name", "description"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("name") is not { Length: > 0 } name)
        {
            await context.Error.WriteLineAsync($"Usage: identity service-account-add --name <name> [--description <text>] {where.Usage}");
            return CliExit.Usage;
        }
        try
        {
            var account = Tokens(parsed, where).CreateServiceAccount(name, parsed.Get("description"), CommandLine);
            await context.Out.WriteLineAsync($"Made the service account {account.Name} (id {account.UserId}). Give it roles with: identity assign --user {account.UserId} ...; make its token with: identity token-create --account {account.Name} --name <name>");
            context.Data["id"] = account.UserId;
            context.Data["name"] = account.Name;
            return CliExit.Ok;
        }
        catch (AccessTokenException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }
    }

    private static async Task<int> ServiceAccountListAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: identity service-account-list {where.Usage}"); return CliExit.Usage; }
        var tokens = Tokens(parsed, where);
        var all = tokens.ServiceAccounts();
        var now = DateTimeOffset.UtcNow;
        if (all.Count == 0) await context.Out.WriteLineAsync("No service account yet. Make one: identity service-account-add --name <name>");
        var rows = all.Select(a => (Account: a, Active: tokens.List(a.UserId).Count(t => t.IsActiveAt(now)))).ToList();
        foreach (var (a, active) in rows) await context.Out.WriteLineAsync($"{a.Name}  {active} active token(s)  {a.Description}  id {a.UserId}");
        context.Data["accounts"] = rows.Select(r => new Dictionary<string, object?> { ["id"] = r.Account.UserId, ["name"] = r.Account.Name, ["description"] = r.Account.Description, ["tokens"] = r.Active }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ServiceAccountRemoveAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("name"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("name") is not { Length: > 0 } name)
        {
            await context.Error.WriteLineAsync($"Usage: identity service-account-remove --name <name> {where.Usage}");
            return CliExit.Usage;
        }
        if (!Tokens(parsed, where).RemoveServiceAccount(name)) { await context.Error.WriteLineAsync($"There is no service account '{name}'. 'identity service-account-list' shows them."); return CliExit.Failure; }
        await context.Out.WriteLineAsync($"Removed the service account {name}; its tokens are revoked.");
        context.Data["name"] = name;
        return CliExit.Ok;
    }

    private static async Task<int> TokenCreateAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "no-expiry" }, Options("account", "name", "days"));
        var usage = $"Usage: identity token-create --account <service account> --name <name> [--days <n> | --no-expiry] {where.Usage}";
        int? days = parsed.Get("days") is { } text ? int.TryParse(text, out var n) && n > 0 ? n : -1 : null;
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("account") is not { Length: > 0 } accountName || parsed.Get("name") is not { Length: > 0 } name || days == -1 || days is not null && parsed.Has("no-expiry"))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        var tokens = Tokens(parsed, where);
        if (tokens.FindServiceAccount(accountName) is not { } account)
        {
            await context.Error.WriteLineAsync($"There is no service account '{accountName}'. A person makes their own tokens at the identity home; 'identity service-account-list' shows the service accounts.");
            return CliExit.Failure;
        }
        try
        {
            var made = tokens.Create(account.UserId, name, CommandLine, days is { } d ? TimeSpan.FromDays(d) : null, noExpiry: parsed.Has("no-expiry"));
            await context.Out.WriteLineAsync($"A token for {account.Name} ({(made.Record.ExpiresAtUtc is { } e ? $"until {e:u}" : "no expiry")}), shown once; the user name is {account.Name}:");
            await context.Out.WriteLineAsync($"    {made.Secret}");
            context.Data["id"] = made.Record.Id;
            context.Data["token"] = made.Secret;
            context.Data["user_name"] = account.Name;
            if (made.Record.ExpiresAtUtc is { } until) context.Data["expires"] = until.UtcDateTime.ToString("O");
            return CliExit.Ok;
        }
        catch (AccessTokenException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }
    }

    private static async Task<int> TokenListAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("user"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: identity token-list [--user <id>] {where.Usage}"); return CliExit.Usage; }
        var all = Tokens(parsed, where).List(parsed.Get("user"));
        var now = DateTimeOffset.UtcNow;
        if (all.Count == 0) await context.Out.WriteLineAsync("No token.");
        foreach (var t in all)
            await context.Out.WriteLineAsync($"{t.Id}  {t.Name}  user {t.UserId}  {(t.RevokedAtUtc is not null ? "revoked" : t.IsActiveAt(now) ? t.ExpiresAtUtc is { } e ? $"until {e:u}" : "no expiry" : "expired")}");
        context.Data["tokens"] = all.Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id, ["user"] = t.UserId, ["name"] = t.Name, ["created"] = t.CreatedAtUtc.UtcDateTime.ToString("O"), ["expires"] = t.ExpiresAtUtc?.UtcDateTime.ToString("O"),
            ["revoked"] = t.RevokedAtUtc?.UtcDateTime.ToString("O"), ["last_used"] = t.LastUsedAtUtc?.UtcDateTime.ToString("O"), ["active"] = t.IsActiveAt(now),
        }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> TokenRevokeAsync(string[] args, CliContext context, IdentityAdminLocation where)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Options("id"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("id") is not { Length: > 0 } id) { await context.Error.WriteLineAsync($"Usage: identity token-revoke --id <token id> {where.Usage}"); return CliExit.Usage; }
        if (!Tokens(parsed, where).Revoke(id)) { await context.Error.WriteLineAsync($"There is no token '{id}' that is not already revoked. 'identity token-list' shows them."); return CliExit.Failure; }
        await context.Out.WriteLineAsync($"Revoked the token {id}.");
        context.Data["id"] = id;
        return CliExit.Ok;
    }

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
        var users = new IdentityUserStore(where.Open(parsed));
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
        if (forUser is not null && new IdentityUserStore(database).Get(forUser) is null)
        {
            await context.Error.WriteLineAsync($"No user has the id '{forUser}'. 'identity user-list' shows them.");
            return CliExit.Failure;
        }
        MintedInvite minted;
        try { minted = new IdentityInviteStore(database).Create(role, forUser, createdByUserId: null, email: email); }
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
        if (new IdentityUserStore(database).Get(userId) is not { } user)
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
