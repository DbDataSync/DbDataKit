using DbDataKit.Cli;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataConsole.Server;

/// <summary>
/// <c>dbdatakit identity run|client-add|client-list|user-list|invite|assign|map-group</c> (plan 13a U15): the identity home on its own, and the few things its operator does at the command line (register the consoles and apps that sign in
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
        // the rest are the shared administration verbs (the serving layer's home has the same ones, as dbdataproxy identity)
        .. IdentityVerbs.Admin(new IdentityAdminLocation("[--repo <identity directory>]", parsed => OpenState(parsed, environment), _ => IdentityHostSettings.DefaultListen)),
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
}
