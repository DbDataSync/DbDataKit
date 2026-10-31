using DbDataKit.Cli;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataConsole.Server;

/// <summary>
/// <c>dbdatakit identity run|client-add|client-list|assign|map-group</c> (plan 13a U15): the identity home on its own, and the few things its operator does at the command line (register the consoles and apps that sign in
/// through it, give a person or an external group roles). The people themselves are invited and listed with the web app's Users screen, as for a console. <c>--repo</c> names the home's directory
/// (<see cref="IdentityHostSettings.DirectoryVariable"/> by default).
/// </summary>
public static class IdentityCli
{
    public static Noun Noun(Func<string, string?>? environment) => new("identity", "The identity home on its own: who people are and what they may do, for every console and app",
    [
        new Verb("run", "Run the identity home: the OpenID Connect provider and the sign-in, with its own state, behind the firewall if you like", EffectClass.RepoFilesOnly,
            "[--repo <identity directory>] [--listen https://<name>:<port>] [--public-url https://<name>:<port>] [--web-root <folder>] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]",
            (args, context) => RunAsync(args, context, environment), DataSchemas.None),
        new Verb("client-add", "Register a console or an app that signs people in through the home; the secret of a confidential client is shown once", EffectClass.RepoFilesOnly,
            "--name <name> --redirect <uri>[,<uri>] [--logout-redirect <uri>[,<uri>]] [--public] [--repo <identity directory>]",
            (args, context) => ClientAddAsync(args, context, environment),
            DataSchemas.Object([("client_id", DataSchemas.String), ("name", DataSchemas.String), ("secret", DataSchemas.String), ("confidential", DataSchemas.Boolean)], "client_id", "name", "confidential")),
        new Verb("client-list", "The registered clients", EffectClass.OfflineOnly, "[--repo <identity directory>]",
            (args, context) => ClientListAsync(args, context, environment),
            DataSchemas.Object([("clients", DataSchemas.ListOf(DataSchemas.Object([("client_id", DataSchemas.String), ("name", DataSchemas.String), ("redirect_uris", DataSchemas.ListOf(DataSchemas.String)), ("confidential", DataSchemas.Boolean)], "client_id", "name", "confidential")))], "clients")),
        new Verb("assign", "Give a person roles on a server, one of its Build projects, or in an app (assigning again replaces; --roles none removes)", EffectClass.RepoFilesOnly,
            "--user <id> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> [--repo <identity directory>]",
            (args, context) => AssignAsync(args, context, environment),
            DataSchemas.Object([("user", DataSchemas.String), ("target", DataSchemas.String), ("roles", DataSchemas.ListOf(DataSchemas.String))], "user", "target", "roles")),
        new Verb("map-group", "Give everyone in an external group (an Entra group id, a Windows group) roles on a server, project or app (mapping again replaces; --roles none removes)", EffectClass.RepoFilesOnly,
            "--source entra|windows --group <group> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> [--repo <identity directory>]",
            (args, context) => MapGroupAsync(args, context, environment),
            DataSchemas.Object([("source", DataSchemas.String), ("group", DataSchemas.String), ("target", DataSchemas.String), ("roles", DataSchemas.ListOf(DataSchemas.String))], "source", "group", "target", "roles")),
    ]);

    private static string DirectoryOf(CliArgs parsed, Func<string, string?>? environment) =>
        Path.GetFullPath(parsed.Get("repo") ?? IdentityHostSettings.DefaultDirectory(environment));

    private static StateDatabase OpenState(CliArgs parsed, Func<string, string?>? environment) =>
        IdentityHostState.Open(DirectoryOf(parsed, environment), IdentityHostSettings.StateFrom(environment), environment);

    private static async Task<int> RunAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "listen", "public-url", "web-root", "state-engine", "state-connection" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: identity run [--repo <identity directory>] [--listen https://<name>:<port>] [--public-url https://<name>:<port>] [--web-root <folder>] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Get("listen") ?? IdentityHostSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://localhost:8490.");
            return CliExit.Usage;
        }
        Uri? publicUrl = null;
        if (parsed.Get("public-url") is { } given && (!Uri.TryCreate(given, UriKind.Absolute, out publicUrl) || publicUrl.Scheme != Uri.UriSchemeHttps))
        {
            await context.Error.WriteLineAsync("--public-url is the https address every console, app and browser reaches the home at (the token issuer), such as https://identity.example.");
            return CliExit.Usage;
        }
        var settings = new IdentityHostSettings(DirectoryOf(parsed, environment), listen, publicUrl, IdentityHostSettings.StateFrom(environment, parsed.Get("state-engine"), parsed.Get("state-connection")));
        Directory.CreateDirectory(settings.Directory);
        var app = IdentityHost.Build(settings, ConsoleHost.Certificate(settings.Directory), line => context.Error.WriteLine(line), parsed.Get("web-root"));
        await context.Out.WriteLineAsync($"The identity home is on {listen}; its issuer is {settings.Issuer}. The first administrator redeems the invitation in {Path.Combine(settings.Directory, "FIRST-RUN.txt")}.");
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
    }

    private static string[]? Uris(string? text) => text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<int> ClientAddAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "public" }, new HashSet<string> { "repo", "name", "redirect", "logout-redirect" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("name") is not { Length: > 0 } name || Uris(parsed.Get("redirect")) is not { Length: > 0 } redirects)
        {
            await context.Error.WriteLineAsync("Usage: identity client-add --name <name> --redirect <uri>[,<uri>] [--logout-redirect <uri>[,<uri>]] [--public] [--repo <identity directory>]");
            return CliExit.Usage;
        }
        try
        {
            var (client, secret) = new OidcClients(OpenState(parsed, environment)).Register(name, redirects, confidential: !parsed.Has("public"), Uris(parsed.Get("logout-redirect")));
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

    private static async Task<int> ClientListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync("Usage: identity client-list [--repo <identity directory>]"); return CliExit.Usage; }
        var all = new OidcClients(OpenState(parsed, environment)).List();
        if (all.Count == 0) await context.Out.WriteLineAsync("No client yet. Register a console: identity client-add");
        foreach (var c in all) await context.Out.WriteLineAsync($"{c.Name}  {(c.Confidential ? "confidential" : "public")}  {string.Join(", ", c.RedirectUris)}  client id {c.ClientId}");
        context.Data["clients"] = all.Select(c => new Dictionary<string, object?> { ["client_id"] = c.ClientId, ["name"] = c.Name, ["redirect_uris"] = c.RedirectUris.ToList(), ["confidential"] = c.Confidential }).ToList();
        return CliExit.Ok;
    }

    /// <summary>The scope of an assignment from the flags: a server, a Build project of it, or an app. Null when the flags do not say exactly one.</summary>
    private static (string Description, Action<HomeDirectory, AssignmentSubject, ServerRole>? Assign, Action<HomeDirectory, string, string, ServerRole>? Map)? Scope(CliArgs parsed)
    {
        var server = parsed.Get("server");
        var app = parsed.Get("app");
        var project = parsed.Get("project");
        if (server is { Length: > 0 } && app is null) return ($"server {server}" + (project is { Length: > 0 } ? $" project {project}" : ""), (d, s, r) => d.Assign(s, server, r, project is { Length: > 0 } ? project : null), (d, src, g, r) => d.MapExternalGroup(src, g, server, r, project is { Length: > 0 } ? project : null));
        if (app is { Length: > 0 } && server is null && project is null) return ($"app {app}", (d, s, r) => d.AssignApp(s, app, r), (d, src, g, r) => d.MapExternalGroupToApp(src, g, app, r));
        return null;
    }

    private static bool TryRoles(string? text, out ServerRole roles)
    {
        roles = ServerRole.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        return ServerRoles.TryParse(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), out roles);
    }

    private static async Task<int> AssignAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "user", "server", "project", "app", "roles" });
        const string usage = "Usage: identity assign --user <id> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> [--repo <identity directory>]";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("user") is not { Length: > 0 } userId || Scope(parsed) is not { } scope || !TryRoles(parsed.Get("roles"), out var roles))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        var database = OpenState(parsed, environment);
        if (new UserStore(database).Get(userId) is not { } user)
        {
            await context.Error.WriteLineAsync($"No user has the id '{userId}'. The Users screen of the home lists them.");
            return CliExit.Failure;
        }
        scope.Assign!(new HomeDirectory(database), AssignmentSubject.User(user.Id), roles);
        var names = ServerRoles.Names(roles);
        await context.Out.WriteLineAsync($"{user.DisplayName} holds {(names.Count == 0 ? "no role" : string.Join(", ", names))} on {scope.Description} (what they hold through groups is added).");
        context.Data["user"] = user.Id;
        context.Data["target"] = scope.Description;
        context.Data["roles"] = names.ToList();
        return CliExit.Ok;
    }

    private static async Task<int> MapGroupAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "source", "group", "server", "project", "app", "roles" });
        const string usage = "Usage: identity map-group --source entra|windows --group <group> (--server <id> [--project <name>] | --app <id>) --roles <viewer,operator,author,approver,admin|none> [--repo <identity directory>]";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("source") is not { Length: > 0 } source || parsed.Get("group") is not { Length: > 0 } group || Scope(parsed) is not { } scope || !TryRoles(parsed.Get("roles"), out var roles))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        try
        {
            scope.Map!(new HomeDirectory(OpenState(parsed, environment)), source, group, roles);
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
