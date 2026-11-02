using DbDataKit.Apps;
using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>dbdataproxy apps ...</c> (plan 26, AP05e): the Store servers the apps host forwards its apps' data calls to. <c>store-pair</c> pairs the host with a Store by the code its administrator made (<c>dbdatastore server pair</c>), as a console
/// is paired: the Store then knows the host's key and the ceiling it may grant there, and the host pins the Store's certificate. The pairing is kept in the data directory and is used by the running host at once.
/// </summary>
public static class AppsCommand
{
    private const string Where = "[--repo <data directory>]";

    public static Noun Noun() => new("apps", "The apps host this proxy serves: the stores it forwards app data to",
    [
        new Verb("store-pair", "Pair the apps host with a Store server, using the code its administrator made", EffectClass.RepoFilesOnly, $"--url <https://store:port> --code <pairing code> [--name <label>] {Where}", PairAsync,
            DataSchemas.Object([("id", DataSchemas.String), ("url", DataSchemas.String), ("fingerprint", DataSchemas.String)], "id", "url", "fingerprint")),
        new Verb("store-list", "The Store servers the apps host is paired with; the id is what an app's data calls name", EffectClass.OfflineOnly, Where, ListAsync,
            DataSchemas.Object([("stores", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("url", DataSchemas.String), ("name", DataSchemas.String), ("fingerprint", DataSchemas.String)], "id", "url", "name", "fingerprint")))], "stores")),
        new Verb("store-remove", "Stop forwarding to a Store server (unpair the host there too, with the Store's own command)", EffectClass.RepoFilesOnly, $"--id <store id> {Where}", RemoveAsync, DataSchemas.None),
    ]);

    private static ProxyOptions OptionsOf(CliArgs parsed)
    {
        var directory = parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null;
        var options = ProxyConfiguration.LoadOptions(directory);
        if (directory is not null) options.DataDirectory = directory;
        return options;
    }

    private static async Task<int> PairAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url", "code", "name", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || !Uri.TryCreate(parsed.Get("url"), UriKind.Absolute, out var url) || parsed.Get("code") is not { Length: > 0 } code)
        {
            await context.Error.WriteLineAsync($"Usage: apps store-pair --url <https://store:port> --code <pairing code> [--name <label>] {Where}");
            return CliExit.Usage;
        }
        var options = OptionsOf(parsed);
        try
        {
            var key = await ProxyAppsModule.SigningKeyAsync(options, default);
            var paired = await PairingClient.PairAsync(url, code, new ConsoleIdentity(ProxyAppsModule.TokenIssuer, key), ClientKind.Server, "DbDataProxy apps host");
            ProxyAppsModule.StoresOf(options).Save(new AppStore(paired.InstanceId, url, paired.CertificateFingerprint, parsed.Get("name") ?? paired.Name));
            await context.Out.WriteLineAsync($"Paired with {paired.Name} ({paired.InstanceId}); the host may be given up to {string.Join(", ", ServerRoles.Names(paired.Ceiling))} there.");
            context.Data["id"] = paired.InstanceId;
            context.Data["url"] = url.AbsoluteUri;
            context.Data["fingerprint"] = paired.CertificateFingerprint;
            return CliExit.Ok;
        }
        catch (PairingFailedException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> ListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var stores = ProxyAppsModule.StoresOf(OptionsOf(parsed)).All();
        foreach (var s in stores) await context.Out.WriteLineAsync($"{s.Id}  {s.Url}  {s.Name}  {s.Fingerprint}");
        if (stores.Count == 0) await context.Out.WriteLineAsync("No Store server is paired. Pair one with: apps store-pair --url <https://store:port> --code <pairing code>");
        context.Data["stores"] = stores.Select(s => new Dictionary<string, object?> { ["id"] = s.Id, ["url"] = s.Url.AbsoluteUri, ["name"] = s.Name, ["fingerprint"] = s.Fingerprint }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> RemoveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "id", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("id") is not { Length: > 0 } id)
        {
            await context.Error.WriteLineAsync($"Usage: apps store-remove --id <store id> {Where}");
            return CliExit.Usage;
        }
        if (!ProxyAppsModule.StoresOf(OptionsOf(parsed)).Remove(id))
        {
            await context.Error.WriteLineAsync($"No paired Store server has the id {id}.");
            return CliExit.Failure;
        }
        await context.Out.WriteLineAsync($"No longer forwarding to {id}.");
        return CliExit.Ok;
    }
}
