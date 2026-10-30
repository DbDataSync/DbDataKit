using System.Net.Http;
using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataConsole.Server;

/// <summary>
/// The command line, in the grammar every DbData product shares (plan 13, C02): <c>dbdatakit server run</c>, <c>service</c>, <c>health check</c>, and the console's own <c>user list|invite</c>. <c>--repo</c> names the console's directory
/// (<see cref="ConsoleSettings.DirectoryVariable"/> by default).
/// </summary>
public static class ConsoleCli
{
    public const string Program = "dbdatakit";

    /// <summary>Where <c>health check</c> asks by default: the console's own address, as a container sees it.</summary>
    public const string HealthUrl = "https://localhost:8480/health/live";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliContext.Console());

    public static Task<int> RunAsync(string[] args, CliContext context) => Router().RunAsync(args, context);

    public static NounRouter Router(Func<string, string?>? environment = null) => new(Program,
    [
        ServerNoun.Create((args, context) => RunServerAsync(args, context, environment)),
        InstanceNoun(environment),
        UserNoun(environment),
        ConsoleService.Noun(ConsoleServiceEnvironment.Real()),
        HealthNoun.Create(new HttpHealthProbe(LoopbackTolerantClient()), HealthUrl),
    ], ConsoleHost.Version);

    /// <summary>The console's certificate is self-signed, so a probe of its own address cannot validate it; it is accepted for a loopback address only, where the answer is this machine and the probe reads a status code and no body.</summary>
    private static HttpClient LoopbackTolerantClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
    });

    private static string DirectoryOf(CliArgs parsed, Func<string, string?>? environment) =>
        Path.GetFullPath(parsed.Get("repo") ?? ConsoleSettings.DefaultDirectory(environment));

    private static async Task<int> RunServerAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "listen", "public-url", "web-root", "announce-listen", "state-engine", "state-connection" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--repo <console directory>] [--listen https://<name>:<port>] [--public-url https://<name>:<port>] [--announce-listen http://<address>:<port>] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Get("listen") ?? ConsoleSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://localhost:8480.");
            return CliExit.Usage;
        }

        Uri? publicUrl = null;
        if (parsed.Get("public-url") is { } given && (!Uri.TryCreate(given, UriKind.Absolute, out publicUrl) || publicUrl.Scheme != Uri.UriSchemeHttps))
        {
            await context.Error.WriteLineAsync("--public-url is the https address people open the console at, such as https://console.example.");
            return CliExit.Usage;
        }

        // The private port: --announce-listen, or the port of the one variable a compose file shares with its servers (where they announce is where the console listens, on every address)
        Uri? announce = null;
        var announceText = parsed.Get("announce-listen");
        if (announceText is null && Uri.TryCreate((environment ?? Environment.GetEnvironmentVariable)(DbDataKit.Web.Trust.ConsoleAnnouncer.UrlVariable), UriKind.Absolute, out var shared) && shared.Scheme == Uri.UriSchemeHttp)
            announceText = new UriBuilder(Uri.UriSchemeHttp, "0.0.0.0", shared.Port).Uri.AbsoluteUri;
        if (announceText is { Length: > 0 } && (!Uri.TryCreate(announceText, UriKind.Absolute, out announce) || announce.Scheme != Uri.UriSchemeHttp || announce.Port == listen.Port))
        {
            await context.Error.WriteLineAsync("--announce-listen is a plain http address on a port of its own, such as http://0.0.0.0:8481, and is reachable only on the network the servers share with the console.");
            return CliExit.Usage;
        }

        var settings = new ConsoleSettings(DirectoryOf(parsed, environment), listen, publicUrl, announce, ConsoleState.SettingsFrom(environment, parsed.Get("state-engine"), parsed.Get("state-connection")));
        Directory.CreateDirectory(settings.Directory);
        var app = ConsoleHost.Build(settings, ConsoleHost.Certificate(settings.Directory), line => context.Error.WriteLine(line), parsed.Get("web-root"));
        await context.Out.WriteLineAsync($"The console is on {listen} (people open it at {settings.Public}). The first administrator redeems the invitation in {Path.Combine(settings.Directory, "FIRST-RUN.txt")}.");
        // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
    }

    // ---- instance ----

    private static (ConsoleInstances Instances, ConsoleKeyStore Keys, InstanceRegistry Registry) OpenInstances(CliArgs parsed, Func<string, string?>? environment)
    {
        var directory = DirectoryOf(parsed, environment);
        Directory.CreateDirectory(directory);
        var store = ConsoleSecrets.Open(directory, environment);
        var keys = new ConsoleKeyStore(store, store);
        var registry = new InstanceRegistry(Path.Combine(directory, "instances.yaml"));
        return (new ConsoleInstances(registry, keys), keys, registry);
    }

    private static Noun InstanceNoun(Func<string, string?>? environment) => new("instance", "The servers this console manages",
    [
        new Verb("add", "Pair this console with a server, using the single-use code its administrator made with 'server pair'", EffectClass.RepoFilesOnly, "<https address> --code <code> [--name <label>] [--repo <console directory>]",
            (args, context) => InstanceAddAsync(args, context, environment),
            DataSchemas.Object([("id", DataSchemas.String), ("name", DataSchemas.String), ("url", DataSchemas.String), ("fingerprint", DataSchemas.String)], "id", "name", "url", "fingerprint")),
        new Verb("list", "The servers: name, address, product, version, and whether each answers (--offline reads only the list)", EffectClass.OfflineOnly, "[--offline] [--repo <console directory>]",
            (args, context) => InstanceListAsync(args, context, environment),
            DataSchemas.Object([("instances", DataSchemas.ListOf(DataSchemas.Object(
                [("id", DataSchemas.String), ("name", DataSchemas.String), ("url", DataSchemas.String), ("fingerprint", DataSchemas.String), ("reachable", DataSchemas.Boolean), ("ready", DataSchemas.Boolean), ("product", DataSchemas.String), ("version", DataSchemas.String), ("problem", DataSchemas.String)],
                "id", "name", "url", "fingerprint")))], "instances")),
        new Verb("remove", "Forget a server (it still lists this console as paired until its administrator runs 'server unpair' there)", EffectClass.RepoFilesOnly, "<id or name> [--repo <console directory>]",
            (args, context) => InstanceRemoveAsync(args, context, environment), DataSchemas.Object([("removed", DataSchemas.String)], "removed")),
        new Verb("pending", "The servers that announced themselves on the private port and wait to be confirmed", EffectClass.OfflineOnly, "[--repo <console directory>]",
            InstancePendingAsync,
            DataSchemas.Object([("pending", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("name", DataSchemas.String), ("product", DataSchemas.String), ("url", DataSchemas.String)], "id", "name", "product", "url")))], "pending")),
        new Verb("confirm", "Pair with a server that is waiting (see 'instance pending')", EffectClass.RepoFilesOnly, "<id or name> [--repo <console directory>]",
            (args, context) => InstanceConfirmAsync(args, context, environment),
            DataSchemas.Object([("id", DataSchemas.String), ("name", DataSchemas.String), ("url", DataSchemas.String), ("fingerprint", DataSchemas.String)], "id", "name", "url", "fingerprint")),
        new Verb("reject", "Forget a waiting server without pairing; it is listed again while it keeps announcing", EffectClass.RepoFilesOnly, "<id or name> [--repo <console directory>]",
            (args, context) => InstanceRejectAsync(args, context, environment), DataSchemas.Object([("rejected", DataSchemas.String)], "rejected")),
        new Verb("rotate-key", "Move to a new signing key on every server without pairing again; run it again if a server could not be reached", EffectClass.RepoFilesOnly, "[--repo <console directory>]",
            (args, context) => InstanceRotateAsync(args, context, environment),
            DataSchemas.Object([("rotated", DataSchemas.ListOf(DataSchemas.String)), ("pending", DataSchemas.ListOf(DataSchemas.String))], "rotated", "pending")),
    ]);

    private static PendingInstances OpenPending(CliArgs parsed, Func<string, string?>? environment) => new(Path.Combine(DirectoryOf(parsed, environment), "pending.json"));

    private static async Task<int> InstancePendingAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync("Usage: instance pending [--repo <console directory>]"); return CliExit.Usage; }
        var waiting = OpenPending(parsed, null).All();
        if (waiting.Count == 0) await context.Out.WriteLineAsync("No server is waiting.");
        foreach (var p in waiting) await context.Out.WriteLineAsync($"{p.Name}  {p.Url}  {p.Product}  id {p.InstanceId}  last heard {p.LastSeen:u}");
        context.Data["pending"] = waiting.Select(p => new Dictionary<string, object?> { ["id"] = p.InstanceId, ["name"] = p.Name, ["product"] = p.Product, ["url"] = p.Url.AbsoluteUri }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> InstanceConfirmAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) { await context.Error.WriteLineAsync("Usage: instance confirm <id or name> [--repo <console directory>]"); return CliExit.Usage; }
        var (instances, _, _) = OpenInstances(parsed, environment);
        try
        {
            var paired = await instances.ConfirmAsync(OpenPending(parsed, environment), parsed.Words[0], context.Cancellation);
            await context.Out.WriteLineAsync($"Paired with {paired.Name} ({paired.InstanceId}) at {paired.Url}. Its certificate is pinned: {paired.CertificateFingerprint}");
            context.Data["id"] = paired.InstanceId;
            context.Data["name"] = paired.Name;
            context.Data["url"] = paired.Url.AbsoluteUri;
            context.Data["fingerprint"] = paired.CertificateFingerprint;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> InstanceRejectAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) { await context.Error.WriteLineAsync("Usage: instance reject <id or name> [--repo <console directory>]"); return CliExit.Usage; }
        var pending = OpenPending(parsed, environment);
        if (pending.Find(parsed.Words[0]) is not { } found) { await context.Error.WriteLineAsync($"No server waiting is called '{parsed.Words[0]}'."); return CliExit.Failure; }
        pending.Remove(found.InstanceId);
        await context.Out.WriteLineAsync($"Forgot the waiting server {found.Name}.");
        context.Data["rejected"] = found.InstanceId;
        return CliExit.Ok;
    }

    private static async Task<int> InstanceAddAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "code", "name" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("code") is not { Length: > 0 } code)
        {
            await context.Error.WriteLineAsync("Usage: instance add <https address> --code <code> [--name <label>] [--repo <console directory>]");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Words[0], UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("The address is an https address, such as https://build.example:8470.");
            return CliExit.Usage;
        }
        var (instances, _, _) = OpenInstances(parsed, environment);
        try
        {
            var paired = await instances.PairAsync(url, code, parsed.Get("name"), context.Cancellation);
            await context.Out.WriteLineAsync($"Paired with {paired.Name} ({paired.InstanceId}) at {paired.Url}. Its certificate is pinned: {paired.CertificateFingerprint}");
            context.Data["id"] = paired.InstanceId;
            context.Data["name"] = paired.Name;
            context.Data["url"] = paired.Url.AbsoluteUri;
            context.Data["fingerprint"] = paired.CertificateFingerprint;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> InstanceListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "offline" }, new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync("Usage: instance list [--offline] [--repo <console directory>]"); return CliExit.Usage; }
        var (instances, _, registry) = OpenInstances(parsed, environment);
        foreach (var problem in registry.Problems) await context.Error.WriteLineAsync(problem);
        var all = parsed.Has("offline")
            ? instances.All().Select(i => new InstanceStatus(i, false, null, null, null, i.ApiVersion, null)).ToList()
            : (await instances.StatusOfAllAsync(context.Cancellation)).ToList();
        if (all.Count == 0) await context.Out.WriteLineAsync("No server is paired. Pair one with: instance add <https address> --code <code>");
        foreach (var s in all)
        {
            var state = parsed.Has("offline") ? "" : s.Reachable ? (s.Ready == true ? "  ready" : "  not ready") : $"  unreachable: {s.Problem}";
            await context.Out.WriteLineAsync($"{s.Instance.Name}  {s.Instance.Url}  {s.Product ?? "?"} {s.Version ?? ""}{state}  id {s.Instance.InstanceId}");
        }
        context.Data["instances"] = all.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.Instance.InstanceId, ["name"] = s.Instance.Name, ["url"] = s.Instance.Url.AbsoluteUri, ["fingerprint"] = s.Instance.CertificateFingerprint,
            ["reachable"] = parsed.Has("offline") ? null : s.Reachable, ["ready"] = s.Ready, ["product"] = s.Product, ["version"] = s.Version, ["problem"] = s.Problem,
        }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> InstanceRemoveAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) { await context.Error.WriteLineAsync("Usage: instance remove <id or name> [--repo <console directory>]"); return CliExit.Usage; }
        var (instances, _, registry) = OpenInstances(parsed, environment);
        var found = registry.Find(parsed.Words[0]);
        if (found is null || !instances.Remove(found.InstanceId))
        {
            await context.Error.WriteLineAsync($"No server is called '{parsed.Words[0]}'. 'instance list' shows them.");
            return CliExit.Failure;
        }
        await context.Out.WriteLineAsync($"Forgot {found.Name} ({found.InstanceId}). It still lists this console as paired until its administrator runs 'server unpair' there.");
        context.Data["removed"] = found.InstanceId;
        return CliExit.Ok;
    }

    private static async Task<int> InstanceRotateAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync("Usage: instance rotate-key [--repo <console directory>]"); return CliExit.Usage; }
        var (_, keys, registry) = OpenInstances(parsed, environment);
        var reports = await ConsoleKeyRotation.RunAsync(keys, registry, null, context.Cancellation);
        foreach (var r in reports) await context.Out.WriteLineAsync($"{r.Instance.Name}: {(r.Rotated ? "now signed for with the new key" : "still on the old key: " + r.Problem)}");
        var pending = reports.Where(r => !r.Rotated).Select(r => r.Instance.Name).ToList();
        context.Data["rotated"] = reports.Where(r => r.Rotated).Select(r => r.Instance.Name).ToList();
        context.Data["pending"] = pending;
        await context.Out.WriteLineAsync(pending.Count == 0 ? "The new key is the console's key." : "Run 'instance rotate-key' again when those servers can be reached; the old key keeps working for them.");
        return pending.Count == 0 ? CliExit.Ok : CliExit.Failure;
    }

    // ---- user ----

    private static Noun UserNoun(Func<string, string?>? environment) => new("user", "The people who can sign in to the console",
    [
        new Verb("list", "The users: name, role, whether enabled, and how many ways each can sign in", EffectClass.OfflineOnly, "[--repo <console directory>]",
            (args, context) => UserListAsync(args, context, environment),
            DataSchemas.Object([("users", DataSchemas.ListOf(DataSchemas.Object(
                [("id", DataSchemas.String), ("name", DataSchemas.String), ("role", DataSchemas.String), ("enabled", DataSchemas.Boolean), ("credentials", DataSchemas.Integer)],
                "id", "name", "role", "enabled", "credentials")))], "users")),
        new Verb("invite", "Print an invitation: for a new user, or for an existing one who has lost their way in", EffectClass.RepoFilesOnly, "[--role admin|viewer] [--user <id>] [--url <address>] [--repo <console directory>]",
            (args, context) => UserInviteAsync(args, context, environment),
            DataSchemas.Object([("url", DataSchemas.String), ("expires", DataSchemas.String), ("role", DataSchemas.String)], "url", "expires", "role")),
    ]);

    /// <summary>Opens the state database the way the server does. It is made if there is none, so an invitation can be printed before the first start.</summary>
    private static StateDatabase OpenState(CliArgs parsed, Func<string, string?>? environment) => ConsoleState.Open(DirectoryOf(parsed, environment), ConsoleState.SettingsFrom(environment), environment);

    private static async Task<int> UserListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync("Usage: user list [--repo <console directory>]"); return CliExit.Usage; }
        var database = OpenState(parsed, environment);
        var users = new UserStore(database);
        var all = users.List();
        if (all.Count == 0) await context.Out.WriteLineAsync("No user yet. Start the console and redeem the invitation it writes, or run: user invite");
        foreach (var user in all)
            await context.Out.WriteLineAsync($"{user.DisplayName}  {user.Role}  {(user.Enabled ? "enabled" : "disabled")}  {users.CredentialsOf(user.Id).Count} way(s) to sign in  id {user.Id}");
        context.Data["users"] = all.Select(u => new Dictionary<string, object?>
        {
            ["id"] = u.Id, ["name"] = u.DisplayName, ["role"] = u.Role.ToString(), ["enabled"] = u.Enabled, ["credentials"] = users.CredentialsOf(u.Id).Count,
        }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> UserInviteAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "role", "user", "url" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync("Usage: user invite [--role admin|viewer] [--user <id>] [--url <address>] [--repo <console directory>]"); return CliExit.Usage; }
        if (!Enum.TryParse<UserRole>(parsed.Get("role") ?? nameof(UserRole.Admin), ignoreCase: true, out var role))
        {
            await context.Error.WriteLineAsync($"'{parsed.Get("role")}' is not a role. Use admin or viewer.");
            return CliExit.Usage;
        }
        var database = OpenState(parsed, environment);
        var forUser = parsed.Get("user");
        if (forUser is not null && new UserStore(database).Get(forUser) is null)
        {
            await context.Error.WriteLineAsync($"No user has the id '{forUser}'. 'user list' shows them.");
            return CliExit.Failure;
        }
        var minted = new InviteStore(database).Create(role, forUser, createdByUserId: null);
        // the code goes in the fragment, which a browser never sends to a server and an access log never records
        var url = $"{(parsed.Get("url") ?? ConsoleSettings.DefaultListen).TrimEnd('/')}/invite#{minted.Code}";
        await context.Out.WriteLineAsync($"An invitation for a {role}, valid until {minted.Invite.ExpiresAtUtc:u}:");
        await context.Out.WriteLineAsync($"    {url}");
        context.Data["url"] = url;
        context.Data["expires"] = minted.Invite.ExpiresAtUtc.UtcDateTime.ToString("O");
        context.Data["role"] = role.ToString();
        return CliExit.Ok;
    }
}
