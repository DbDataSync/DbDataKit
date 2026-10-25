using System.Net.Http;
using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.State;
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
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "listen", "public-url", "web-root" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--repo <console directory>] [--listen https://<name>:<port>] [--public-url https://<name>:<port>]");
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

        var settings = new ConsoleSettings(DirectoryOf(parsed, environment), listen, publicUrl);
        Directory.CreateDirectory(settings.Directory);
        var app = ConsoleHost.Build(settings, ConsoleHost.Certificate(settings.Directory), line => context.Error.WriteLine(line), parsed.Get("web-root"));
        await context.Out.WriteLineAsync($"The console is on {listen} (people open it at {settings.Public}). The first administrator redeems the invitation in {Path.Combine(settings.Directory, "FIRST-RUN.txt")}.");
        // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
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
    private static StateDatabase OpenState(CliArgs parsed, Func<string, string?>? environment) => ConsoleState.Open(DirectoryOf(parsed, environment));

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
