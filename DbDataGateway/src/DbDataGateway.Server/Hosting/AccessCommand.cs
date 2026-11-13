using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Hosting.Local;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>dbdatagateway access mask-key status|rotate</c> (plan 22a GP13, owner decision 2026-11-12): the key of the hash mask of a column rule is the group's, made on first use. <c>rotate</c> replaces it, which deliberately changes every hash
/// (<c>POST /api/v1/access/mask-key/rotate</c>, audited, the result cache cleared), made over the local channel of the running gateway on this machine: the operating system decided who may reach the channel, so the caller counts as an administrator
/// and the audit line names the operating-system user. The key's value is never shown.
/// </summary>
public static class AccessCommand
{
    private const string Where = "[--repo <data directory>]";

    public static Noun Noun() => new("access", "The access rules' key (the hash mask of a column rule)",
    [
        new Verb("mask-key-status", "Has the group made its key for hash masks yet (the value is never shown); the running gateway must have its local channel on", EffectClass.OfflineOnly, Where,
            (args, context) => CallAsync("GET", "/api/v1/access/mask-key", args, context), DataSchemas.Object([("exists", DataSchemas.Boolean)], "exists")),
        new Verb("mask-key-rotate", "Replace the group's key for hash masks with a new one: every hash mask changes, and the result cache is cleared; audited", EffectClass.TargetWrites, Where,
            (args, context) => CallAsync("POST", "/api/v1/access/mask-key/rotate", args, context), DataSchemas.Object([("rotated", DataSchemas.Boolean), ("cleared", DataSchemas.Integer)], "rotated", "cleared")),
    ]);

    private static async Task<int> CallAsync(string method, string path, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: access mask-key-{(method == "GET" ? "status" : "rotate")} {Where}");
            return CliExit.Usage;
        }
        string dataDirectory;
        ManagementOptions management;
        try
        {
            dataDirectory = parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : GatewayConfigFile.ResolveDataDirectory();
            management = GatewayConfiguration.LoadOptions(dataDirectory).Management;
        }
        catch (GatewayConfigException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }
        if (!management.LocalChannel)
        {
            await context.Error.WriteLineAsync("The local channel is off, so the command line cannot reach the running gateway. Set `management: { localChannel: true }` in server.yaml and restart the gateway.");
            return CliExit.Failure;
        }
        var message = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["method"] = method, ["path"] = path });
        JsonNode? answer;
        try
        {
            await using var channel = await LocalChannelClient.ConnectAsync(new LocalChannelOptions("dbdatagateway") { Directory = string.IsNullOrWhiteSpace(management.LocalChannelDirectory) ? null : management.LocalChannelDirectory }, ct: context.Cancellation);
            answer = JsonNode.Parse(await channel.SendAsync(message, context.Cancellation));
        }
        catch (UnauthorizedAccessException)
        {
            await context.Error.WriteLineAsync("The operating system does not let this user reach the gateway's local channel; run the command as the gateway's account or as an administrator.");
            return CliExit.Failure;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or JsonException)
        {
            await context.Error.WriteLineAsync($"The running gateway did not answer on its local channel ({ex.Message}). Is `dbdatagateway server run` running with the local channel on?");
            return CliExit.Failure;
        }
        var status = (int?)answer?["status"] ?? 0;
        var body = answer?["body"];
        if (status != 200)
        {
            await context.Error.WriteLineAsync($"The gateway refused ({status}): {(string?)body?["error"] ?? body?.ToJsonString() ?? "no reason given"}");
            return CliExit.Failure;
        }
        if (method == "GET")
        {
            var exists = (bool?)body?["exists"] ?? false;
            context.Data["exists"] = exists;
            await context.Out.WriteLineAsync(exists ? "The group has its key for hash masks." : "The group has no key for hash masks yet; it is made the first time a hash mask is answered.");
        }
        else
        {
            var cleared = (int?)body?["cleared"] ?? 0;
            context.Data["rotated"] = true;
            context.Data["cleared"] = cleared;
            await context.Out.WriteLineAsync($"The group's key for hash masks was replaced: every hash mask is different now. {cleared} cache entries were cleared on this instance; other instances use the new key within a minute.");
        }
        return CliExit.Ok;
    }
}
