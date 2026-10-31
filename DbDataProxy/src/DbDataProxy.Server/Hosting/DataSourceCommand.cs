using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Hosting.Local;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>dbdataproxy datasource adopt &lt;name&gt; &lt;client&gt;</c> (plan 13a, T13): hands a data source made by hand, or by another client, to a paired server client, with the same call as
/// <c>POST /api/v1/data-sources/{name}/adopt</c>, made over the local channel of the running proxy on this machine. The operating system decided who may reach the channel, so the caller counts as a person with
/// the admin role (<c>OwnerScope.AuthorizeAdopt</c>) and the audit line names the operating-system user. The client is named as <c>server clients</c> lists it, by id or by name; the proxy has the last word
/// (a source that carries <c>extensions.proxy</c> is refused, as through the console).
/// </summary>
public static class DataSourceCommand
{
    public const string Usage = "<name> <client id or name> [--repo <path>]";
    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();

    public static async Task<int> AdoptAsync(string[] args, CliContext context, Func<string[], PairingFiles> files)
    {
        var parsed = CliArgs.Parse(args, NoFlags, new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0)
        {
            await context.Error.WriteLineAsync("Unknown or incomplete option: " + string.Join(", ", parsed.Unknown));
            return CliExit.Usage;
        }
        if (parsed.Words.Count != 2)
        {
            await context.Error.WriteLineAsync("Usage: datasource adopt " + Usage);
            return CliExit.Usage;
        }
        var (name, wanted) = (parsed.Words[0], parsed.Words[1]);

        // The client is looked up where 'server clients' reads, so a name means the same in both.
        var all = files(args).Clients().All();
        var matches = all.Where(c => c.Id == wanted).ToList();
        if (matches.Count == 0) matches = all.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            await context.Error.WriteLineAsync($"No paired client is called '{wanted}'. 'server clients' lists them.");
            return CliExit.Failure;
        }
        if (matches.Count > 1)
        {
            await context.Error.WriteLineAsync($"{matches.Count} clients are called '{wanted}'; name one by id: {string.Join(", ", matches.Select(c => c.Id))}.");
            return CliExit.Failure;
        }
        var client = matches[0];
        if (client.Kind != ClientKind.Server)
        {
            await context.Error.WriteLineAsync($"'{client.Name}' ({client.Id}) is paired as a {client.Kind.ToString().ToLowerInvariant()}, not as a server client; only a Build server (or another server client) can take a data source over.");
            return CliExit.Failure;
        }

        var dataDirectory = parsed.Get("repo") is { } repo ? Path.GetFullPath(repo) : ProxyConfigFile.ResolveDataDirectory();
        var management = ProxyConfiguration.LoadOptions(dataDirectory).Management;
        if (!management.LocalChannel)
        {
            await context.Error.WriteLineAsync("The local channel is off, so the command line cannot reach the running proxy. Set `management: { localChannel: true }` in server.yaml and restart the proxy; or adopt from the console.");
            return CliExit.Failure;
        }

        var message = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["method"] = "POST",
            ["path"] = $"/api/v1/data-sources/{Uri.EscapeDataString(name)}/adopt",
            ["body"] = new JsonObject { ["client"] = client.Id }.ToJsonString(),
        });
        JsonNode? answer;
        try
        {
            await using var channel = await LocalChannelClient.ConnectAsync(new LocalChannelOptions("dbdataproxy")
            {
                Directory = string.IsNullOrWhiteSpace(management.LocalChannelDirectory) ? null : management.LocalChannelDirectory,
            }, ct: context.Cancellation);
            answer = JsonNode.Parse(await channel.SendAsync(message, context.Cancellation));
        }
        catch (UnauthorizedAccessException)
        {
            await context.Error.WriteLineAsync("The operating system does not let this user reach the proxy's local channel; run the command as the proxy's account or as an administrator.");
            return CliExit.Failure;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or JsonException)
        {
            await context.Error.WriteLineAsync($"The running proxy did not answer on its local channel ({ex.Message}). Is `dbdataproxy server run` running with the local channel on?");
            return CliExit.Failure;
        }

        var status = (int?)answer?["status"] ?? 0;
        var body = answer?["body"];
        if (status != 200)
        {
            await context.Error.WriteLineAsync($"The proxy refused ({status}): {(string?)body?["error"] ?? body?.ToJsonString() ?? "no reason given"}");
            return CliExit.Failure;
        }

        var adopted = (bool?)body?["adopted"] ?? false;
        context.Data["name"] = name;
        context.Data["client"] = client.Id;
        context.Data["adopted"] = adopted;
        if ((string?)body?["previous_owner"] is { } previous) context.Data["previous_owner"] = previous;
        var text = new StringBuilder();
        text.Append(adopted
            ? $"Adopted '{name}': {client.Name} ({client.Id}) manages it now and can change and remove it."
            : $"'{name}' is already managed by {client.Name} ({client.Id}); nothing changed.");
        if (adopted && (string?)body?["previous_owner"] is { } owner) text.Append($" It was {owner}'s.");
        await context.Out.WriteLineAsync(text.ToString());
        return CliExit.Ok;
    }
}
