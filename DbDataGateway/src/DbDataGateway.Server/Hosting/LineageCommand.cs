using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Hosting.Local;
using DbDataKit.Lineage;
using DbDataKit.Lineage.Cli;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>dbdatagateway lineage graph|impact|provenance</c> (plan 18, G08): the Gateway's lineage (its data sources, the tables they serve with their run summary, the semantic models it serves), asked of the running gateway on this machine over its
/// local channel (<c>GET /api/v1/lineage</c>), as the operating system's user with every role, so the names of the readers are in it. <c>--repo</c> is the data directory whose local channel to ask, as the other local verbs take it.
/// </summary>
public static class LineageCommand
{
    private static readonly AsyncLocal<string?> Repo = new();

    public static Noun Noun()
    {
        var inner = LineageNoun.Create(AskAsync);
        return new Noun(inner.Name, inner.Summary, [.. inner.Verbs.Select(verb => verb with { Usage = verb.Usage + " [--repo <path>]", Run = (args, context) => RunAsync(verb, args, context) })]);
    }

    private static async Task<int> RunAsync(Verb verb, string[] args, CliContext context)
    {
        string? repo = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Length) repo = args[++i];
            else rest.Add(args[i]);
        }
        Repo.Value = repo;
        try { return await verb.Run([.. rest], context); }
        catch (InvalidOperationException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    /// <summary>The graph the running gateway answers; a gateway that cannot be reached, or refuses, is an exception saying why (the verb reports it).</summary>
    private static async Task<LineageGraph> AskAsync(CancellationToken cancellation)
    {
        var dataDirectory = Repo.Value is { } repo ? Path.GetFullPath(repo) : GatewayConfigFile.ResolveDataDirectory();
        var management = GatewayConfiguration.LoadOptions(dataDirectory).Management;
        if (!management.LocalChannel) throw new InvalidOperationException("The local channel is off, so the command line cannot reach the running gateway. Set `management: { localChannel: true }` in server.yaml and restart the gateway; or read the lineage in the console.");
        var message = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["method"] = "GET", ["path"] = "/api/v1/lineage" });
        JsonNode? answer;
        try
        {
            await using var channel = await LocalChannelClient.ConnectAsync(new LocalChannelOptions("dbdatagateway") { Directory = string.IsNullOrWhiteSpace(management.LocalChannelDirectory) ? null : management.LocalChannelDirectory }, ct: cancellation);
            answer = JsonNode.Parse(await channel.SendAsync(message, cancellation));
        }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("The operating system does not let this user reach the gateway's local channel; run the command as the gateway's account or as an administrator."); }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or JsonException)
        {
            throw new InvalidOperationException($"The running gateway did not answer on its local channel ({ex.Message}). Is `dbdatagateway server run` running with the local channel on?");
        }
        var status = (int?)answer?["status"] ?? 0;
        if (status != 200) throw new InvalidOperationException($"The gateway refused ({status}): {(string?)answer?["body"]?["error"] ?? "no reason given"}");
        return LineageJson.Read(answer?["body"]);
    }
}
