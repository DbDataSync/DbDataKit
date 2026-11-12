using System.Globalization;
using DbDataBuild.Cli.Server;
using DbDataBuild.Core;
using DbDataKit.Cli;

namespace DbDataBuild.Cli;

/// <summary>
/// <c>dbdatabuild semantic describe|query|explain|members</c> as commands of the command tree (plan 14a SL22, remainder): the same four verbs, documents and closed <c>data</c> schema as the server command line's
/// <c>semantic</c> noun (<see cref="BuildSemantic"/>, the shared <c>SemanticNoun</c>), so <c>ui mcp</c> and the terminal catalog offer them to an agent that is writing a view over a published model. Nothing is
/// written twice: the command builds the verb's own arguments and runs the verb, then moves the verb's values into the document's <c>data</c>.
/// The Gateway is one the Build server is paired with (<c>--gateway</c>, read from the server's directory, <c>--server-dir</c> when it is not the default one) or <c>--address</c>; the credential is the personal access
/// token in <c>DBDATAKIT_GATEWAY_TOKEN</c>, never an argument, and the statement runs as the person it names. The management API's tool route does not offer these commands (<see cref="Server.BuildManagementApi"/>): a
/// token in the server's environment must not be usable by whoever may call a tool, and <c>--address</c> would let a caller make the server connect anywhere.
/// </summary>
internal static class SemanticCommand
{
    public const string Group = "semantic";

    /// <summary>True for the commands of this group, which the management API's tool route does not offer.</summary>
    public static bool IsSemantic(string commandName) => commandName.StartsWith(Group + " ", StringComparison.Ordinal);

    public sealed record Connection(string? Gateway, string? Address, int? FlightPort, string? CaFile, bool Insecure, string? ServerDirectory);

    public static int Run(CommandSpec spec, string verb, string model, string? attribute, string? sql, string? request, int? maxRows, string? search, int? limit, Connection connection, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: a Gateway's Flight SQL listener (not a target)");
        var args = new List<string>();
        void Option(string name, string? value) { if (value is not null) { args.Add("--" + name); args.Add(value); } }
        args.Add(model);
        if (attribute is not null) args.Add(attribute);
        Option("sql", sql);
        Option("request", request);
        Option("max-rows", maxRows?.ToString(CultureInfo.InvariantCulture));
        Option("search", search);
        Option("limit", limit?.ToString(CultureInfo.InvariantCulture));
        Option("gateway", connection.Gateway);
        Option("address", connection.Address);
        Option("flight-port", connection.FlightPort?.ToString(CultureInfo.InvariantCulture));
        Option("ca-file", connection.CaFile);
        Option("repo", connection.ServerDirectory);
        if (connection.Insecure) args.Add("--insecure");

        var context = new CliContext(output, error, TextReader.Null);
        var exit = BuildSemantic.Noun().Verbs.Single(v => v.Name == verb).Run([.. args], context).GetAwaiter().GetResult();
        foreach (var (key, value) in context.Data) output.Payload(key, value);
        return exit switch { CliExit.Ok => CliApp.ExitOk, CliExit.Usage => CliApp.ExitUsage, _ => CliApp.ExitFindings };
    }
}
