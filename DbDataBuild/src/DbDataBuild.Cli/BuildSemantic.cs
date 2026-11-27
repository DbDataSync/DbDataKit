using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;

namespace DbDataBuild.Cli;

/// <summary>
/// The <c>semantic</c> verbs in Build (plan 14a SL22): <c>dbdatabuild semantic describe|query|explain|members &lt;model&gt; --address &lt;Flight address&gt;</c>, the same four verbs and documents the Gateway has (the shared
/// <see cref="SemanticNoun"/>), so an agent in a Build project asks what a published model offers, runs a request and reads the plan before it writes a view over it. The credential is a personal access token or access token of the
/// Gateway's identity home in <c>DBDATAKIT_GATEWAY_TOKEN</c>, never a flag, and the statement runs as the person it names. <c>--gateway</c> named a Gateway the Build server was paired with; the server was deleted (plan 33 SC23), so it answers
/// with the sentence that says to give <c>--address</c>, and a semantic model is published from the Workspace (a Gateway target of a build project).
/// </summary>
internal static class BuildSemantic
{
    public const string TokenVariable = "DBDATAKIT_GATEWAY_TOKEN";
    public const int DefaultFlightPort = 7821;
    public static readonly string[] Options = ["gateway", "address", "flight-port", "ca-file", "repo"];
    public static readonly string[] Flags = ["insecure"];
    public const string Usage = "--address URL [--ca-file PEM] [--insecure]   (the token is " + TokenVariable + ")";

    /// <summary>How a session is opened; the tests put a stand-in session here (the command is the shared noun, the connection is the only part that is Build's).</summary>
    internal static SemanticConnect Connect { get; set; } = ConnectAsync;

    public static Noun Noun() => SemanticNoun.Create((arguments, context) => Connect(arguments, context), Options, Flags, Usage);

    public static Task<ISemanticSession> ConnectAsync(CliArgs arguments, CliContext context)
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException($"Name the person to run as: their token goes in the {TokenVariable} environment variable (a personal access token of the Gateway's identity home), never in an argument.");
        var address = AddressOf(arguments);
        return Task.FromResult<ISemanticSession>(new FlightSemanticSession(address, token.Trim(), arguments.Has("insecure"), arguments.Get("ca-file")));
    }

    /// <summary>What <c>--gateway</c> answers now that the server whose pairings it named is gone.</summary>
    public const string GatewayGone = "--gateway named a Gateway the Build server was paired with, and the Build server was deleted (plan 33 SC23): give the Gateway's Flight address with --address, such as https://gateway.example.com:7821.";

    /// <summary>The Flight address: <c>--address</c> as given.</summary>
    internal static Uri AddressOf(CliArgs arguments)
    {
        if (arguments.Get("address") is { } given)
            return Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : throw new InvalidOperationException("--address is an http or https address, such as https://gateway.example.com:7821.");
        if (arguments.Get("gateway") is not null || arguments.Get("repo") is not null) throw new InvalidOperationException(GatewayGone);
        throw new InvalidOperationException("Name the Gateway's Flight address: --address URL, such as https://gateway.example.com:7821.");
    }
}
