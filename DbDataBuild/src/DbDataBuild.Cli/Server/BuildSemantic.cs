using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The <c>semantic</c> noun in Build (plan 14a SL22): <c>dbdatabuild semantic describe|query|explain|members &lt;model&gt; --gateway &lt;paired name&gt;</c>, the same four verbs and documents the Gateway has (the shared
/// <see cref="SemanticNoun"/>), so an agent in a Build project asks what a published model offers, runs a request and reads the plan before it writes a view over it. The Gateway is one this server is paired with
/// (<c>server proxy add</c>): its host comes from the pairing, the Flight port is 8815 unless <c>--flight-port</c> or <c>--address</c> says another. The credential is a personal access token or access token of the Gateway's
/// identity home in <c>DBDATAKIT_GATEWAY_TOKEN</c>, never a flag, and the statement runs as the person it names.
/// </summary>
internal static class BuildSemantic
{
    public const string TokenVariable = "DBDATAKIT_GATEWAY_TOKEN";
    public const int DefaultFlightPort = 8815;
    public static readonly string[] Options = ["gateway", "address", "flight-port", "ca-file", "repo"];
    public static readonly string[] Flags = ["insecure"];
    public const string Usage = "(--gateway <paired name> [--flight-port N] | --address URL) [--ca-file PEM] [--insecure] [--repo <server directory>]   (the token is " + TokenVariable + ")";

    /// <summary>How a session is opened; the tests put a stand-in session here (the command is the shared noun, the connection is the only part that is Build's).</summary>
    internal static SemanticConnect Connect { get; set; } = ConnectAsync;

    public static Noun Noun() => SemanticNoun.Create((arguments, context) => Connect(arguments, context), Options, Flags, Usage);

    public static Task<ISemanticSession> ConnectAsync(CliArgs arguments, CliContext context)
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException($"Name the person to run as: their token goes in the {TokenVariable} environment variable (a personal access token of the Gateway's identity home), never in an argument.");
        var address = AddressOf(arguments, arguments.Get("repo") is { } repo ? Path.GetFullPath(repo) : null);
        return Task.FromResult<ISemanticSession>(new FlightSemanticSession(address, token.Trim(), arguments.Has("insecure"), arguments.Get("ca-file")));
    }

    /// <summary>The Flight address: <c>--address</c> as given, or the host of the paired Gateway's management address on the Flight port.</summary>
    internal static Uri AddressOf(CliArgs arguments, string? serverDirectory)
    {
        if (arguments.Get("address") is { } given)
            return Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : throw new InvalidOperationException("--address is an http or https address, such as https://gateway.example.com:8815.");
        if (arguments.Get("gateway") is not { } name) throw new InvalidOperationException("Name the Gateway: --gateway <name of a paired gateway> (see 'server proxy list'), or --address URL.");
        var proxy = new ProxiesFile(serverDirectory ?? ServerCli.DirectoryOf([])).Find(name)
            ?? throw new InvalidOperationException($"'{name}' is not a Gateway this server is paired with (see 'server proxy list').");
        var port = DefaultFlightPort;
        if (arguments.Get("flight-port") is { } text && (!int.TryParse(text, out port) || port is < 1 or > 65535)) throw new InvalidOperationException("--flight-port is a port number.");
        var management = new Uri(proxy.Url);
        return new Uri($"{management.Scheme}://{management.Host}:{port}");
    }
}
