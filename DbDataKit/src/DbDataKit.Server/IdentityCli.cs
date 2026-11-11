using DbDataKit.Cli;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataKit.Server;

/// <summary>
/// <c>dbdatakit identity run|pair|clients|unpair</c> and the shared administration verbs (plan 13a U15, U21): the identity home on its own, the pairing a console administers it through (its screens, through the relay), and what an
/// operator does at the command line when no console answers. <c>--repo</c> names the home's directory
/// (<see cref="IdentityHostSettings.DirectoryVariable"/> by default).
/// </summary>
public static class IdentityCli
{
    public static Noun Noun(Func<string, string?>? environment) => new("identity", "The identity home on its own: who people are and what they may do, for every console and app",
    [
        new Verb("run", "Run the identity home: the OpenID Connect provider and the sign-in, with its own state, behind the firewall if you like", EffectClass.RepoFilesOnly,
            "[--repo <identity directory>] [--listen https://<name>:<port>] [--public-url https://<name>:<port>] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]",
            (args, context) => RunAsync(args, context, environment), DataSchemas.None),
        // a console pairs with the home like any server and administers it through its screens (13a U21): pair, clients, unpair
        .. PairingVerbs.Create(args => new PairingFiles(Path.Combine(DirectoryOf(CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" }), environment), "server.yaml"))),
        // the rest are the shared administration verbs (the serving layer's home has the same ones, as dbdatagateway identity)
        .. IdentityVerbs.Admin(new IdentityAdminLocation("[--repo <identity directory>]", parsed => OpenState(parsed, environment), _ => IdentityHostSettings.DefaultListen)),
    ]);

    private static string DirectoryOf(CliArgs parsed, Func<string, string?>? environment) =>
        Path.GetFullPath(parsed.Get("repo") ?? IdentityHostSettings.DefaultDirectory(environment));

    private static StateDatabase OpenState(CliArgs parsed, Func<string, string?>? environment) =>
        IdentityHostState.Open(DirectoryOf(parsed, environment), IdentityHostSettings.StateFrom(environment), environment);

    private static async Task<int> RunAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "listen", "public-url", "web-root", "state-engine", "state-connection" });
        if (parsed.Get("web-root") is not null)
        {
            // a clean break, said so: the home no longer serves the console's screens (13a U21)
            await context.Error.WriteLineAsync("--web-root is gone: the identity home serves no web app of its own any more. Pair a console with it (dbdatakit identity pair) and administer it from the console's screens.");
            return CliExit.Usage;
        }
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: identity run [--repo <identity directory>] [--listen https://<name>:<port>] [--public-url https://<name>:<port>] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Get("listen") ?? IdentityHostSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://localhost:8482.");
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
        var app = IdentityHost.Build(settings, ConsoleHost.Certificate(settings.Directory), line => context.Error.WriteLine(line));
        await context.Out.WriteLineAsync($"The identity home is on {listen}; its issuer is {settings.Issuer}. The first administrator redeems the invitation in {Path.Combine(settings.Directory, "FIRST-RUN.txt")}; pair a console to administer it: dbdatakit identity pair --max-role admin.");
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
    }
}
