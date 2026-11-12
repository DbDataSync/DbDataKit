using DbDataBuild.Models;
using DbDataKit.Cli;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// <c>semantic publish &lt;project&gt; &lt;model&gt; --gateway &lt;paired name&gt;</c> and <c>semantic rollback &lt;model&gt; --gateway &lt;paired name&gt; [--to-version N]</c> on the server's command line (plan 14a SL30, was J12b): the
/// two calls of the console's Publish tab, from a shell. The verbs work on the server's directory as every <c>server</c> verb does (<c>server proxy add</c>, <c>server schedule</c>): whoever may read that directory is trusted with
/// the server's own pairing, so there is **no token for the Build side and none is read from the environment or a flag**. The credential the Gateway sees is the one the pairing made: the server's signing key, kept in its secret
/// store, pins the Gateway's certificate and signs each call as the person <c>cli:&lt;operating system user&gt;</c> with the Publisher role, so the Gateway applies its owner rules (this server may change only what it published) and its
/// audit names the person. A static token held in an environment variable could not work here anyway: management tokens are signed per call, expire in minutes and are checked for replay.
/// A model on the Gateway has versions, not channels, so there is no <c>--channel</c>.
/// </summary>
internal static class BuildSemanticPublish
{
    public const string PublishUsage = "<project> <model> --gateway <paired name> [--repo <server directory>]";
    public const string RollbackUsage = "<model> --gateway <paired name> [--to-version N] [--repo <server directory>]";

    private static readonly string PublishSchema = DataSchemas.Object(
    [
        ("gateway", DataSchemas.String), ("model", DataSchemas.String), ("version", DataSchemas.Integer), ("created", DataSchemas.Boolean),
        ("data_sources", DataSchemas.ListOf(DataSchemas.String)),
    ], "gateway", "model", "created", "data_sources");

    private static readonly string RollbackSchema = DataSchemas.Object(
    [
        ("gateway", DataSchemas.String), ("model", DataSchemas.String), ("version", DataSchemas.Integer), ("from_version", DataSchemas.Integer),
    ], "gateway", "model", "version", "from_version");

    public static Noun With(Noun noun) => noun.With(
        new Verb("publish", "Publish a semantic model of a hosted project, with the connections it reads, to a Gateway this server is paired with (the Publish tab's button)", EffectClass.RepoFilesOnly, PublishUsage,
            (args, context) => RunAsync(args, context, publish: true), PublishSchema),
        new Verb("rollback", "Serve the version before the current one of a published model again (or --to-version N); every version is kept", EffectClass.RepoFilesOnly, RollbackUsage,
            (args, context) => RunAsync(args, context, publish: false), RollbackSchema));

    /// <summary>The person a call is for: the operating system user, at the role the verb needs (the command line is the server's own directory, so the role is the server's own ceiling).</summary>
    internal static ValidatedToken Person()
    {
        var now = DateTimeOffset.UtcNow;
        var user = "cli:" + Environment.UserName;
        return new ValidatedToken(new PairedClient("build-cli", "dbdatabuild server semantic", [], ServerRole.Approver, ClientKind.Cli), user, Environment.UserName, false, ServerRole.Approver,
            new Dictionary<string, ServerRole>(), "", now, now.AddMinutes(1));
    }

    private static async Task<int> RunAsync(string[] args, CliContext context, bool publish)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "gateway", "repo", "to-version" });
        var words = publish ? 2 : 1;
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != words || parsed.Get("gateway") is not { Length: > 0 } gateway || (publish && parsed.Get("to-version") is not null))
        {
            await context.Error.WriteLineAsync($"Usage: semantic {(publish ? "publish " + PublishUsage : "rollback " + RollbackUsage)}");
            return CliExit.Usage;
        }
        int? toVersion = null;
        if (parsed.Get("to-version") is { } text)
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                await context.Error.WriteLineAsync("--to-version is a whole number of at least 1.");
                return CliExit.Usage;
            }
            toVersion = n;
        }

        var directory = ServerCli.DirectoryOf(args);
        try
        {
            var proxies = new ProxiesFile(directory);
            if (proxies.Find(gateway) is null)
            {
                await context.Error.WriteLineAsync($"'{gateway}' is not a Gateway this server is paired with (see 'server proxy list').");
                return CliExit.Failure;
            }
            var secrets = ServerSecrets.Open(directory);
            var publisher = new ProxyPublisher(proxies, new ConsoleKeyStore(secrets, secrets), secrets);
            var person = Person();

            if (!publish)
            {
                var back = await publisher.RollbackAsync(gateway, parsed.Words[0], toVersion, person, context.Cancellation);
                if (back.Status != 200) return await RefusedAsync(context, back);
                await context.Out.WriteLineAsync($"{parsed.Words[0]} on {gateway} serves version {(int?)back.Body["version"]} again (it was {(int?)back.Body["from_version"]}).");
                context.Data["gateway"] = gateway;
                context.Data["model"] = parsed.Words[0];
                context.Data["version"] = (int?)back.Body["version"];
                context.Data["from_version"] = (int?)back.Body["from_version"];
                return CliExit.Ok;
            }

            var projects = new ProjectsFile(directory);
            if (projects.Find(parsed.Words[0]) is not { } hosted)
            {
                await context.Error.WriteLineAsync($"This server hosts no project '{parsed.Words[0]}' (see 'server project list').");
                return CliExit.Failure;
            }
            var loaded = ProjectContext.Load(projects.RootOf(hosted));
            if (loaded.Semantic.Models.FirstOrDefault(m => m.Name == parsed.Words[1]) is not { } file)
            {
                await context.Error.WriteLineAsync($"The project has no semantic model '{parsed.Words[1]}'.");
                return CliExit.Failure;
            }
            var result = await publisher.PublishSemanticAsync(gateway, file, loaded.Config, person, context.Cancellation);
            if (result.Status != 200) return await RefusedAsync(context, result);
            var sources = result.Body["data_sources"]?.AsArray().Select(d => (string?)d ?? "").ToList() ?? [];
            await context.Out.WriteLineAsync($"Published {file.Name} to {gateway} as version {(int?)result.Body["version"]}{((bool?)result.Body["created"] == true ? " (new)" : "")}; connections sent: {(sources.Count == 0 ? "none" : string.Join(", ", sources))}.");
            context.Data["gateway"] = gateway;
            context.Data["model"] = file.Name;
            if ((int?)result.Body["version"] is { } version) context.Data["version"] = version;
            context.Data["created"] = (bool?)result.Body["created"] == true;
            context.Data["data_sources"] = sources;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> RefusedAsync(CliContext context, PublishResult result)
    {
        await context.Error.WriteLineAsync((string?)result.Body["error"] ?? $"The Gateway answered {result.Status}.");
        return CliExit.Failure;
    }
}
