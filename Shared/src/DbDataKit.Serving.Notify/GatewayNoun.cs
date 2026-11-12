using DbDataKit.Cli;
using DbDataKit.Web.Trust;

namespace DbDataKit.Serving.Notify;

/// <summary>
/// The <c>gateway</c> noun of a product's command line: <c>gateway add &lt;name&gt; --url &lt;https address&gt; --code &lt;pairing code&gt;</c> pairs this server with a serving group as a server client (the Gateway's administrator makes the code with
/// <c>dbdatagateway server pair --max-role publisher</c>), <c>gateway list</c> and <c>gateway remove &lt;name&gt;</c>. A paired server tells the group when it finishes writing a dataset (plan 24a UC13). The same three verbs, documents and schemas in every product
/// that has no pairing of its own for publishing (Sync, the Runner); Build and DbDataStore keep theirs (<c>server proxy</c>, <c>gateway</c>).
/// </summary>
public static class GatewayNoun
{
    /// <param name="pairings">The pairings file of the server the command line names.</param>
    /// <param name="identity">The server's own identity store (its signing key), as the pairing is made with.</param>
    /// <param name="clientName">How the Gateway lists this server (<c>DbDataSync server</c>).</param>
    /// <param name="where">The option that names the server's directory, as the product writes it in usage (<c>[--repo &lt;directory&gt;]</c>).</param>
    /// <param name="options">The option names the product's resolvers read (<c>repo</c>), so parsing accepts them.</param>
    public static Noun Create(Func<string[], GatewayPairingsFile> pairings, Func<string[], ConsoleKeyStore> identity, string clientName, string where, params string[] options)
    {
        var known = new HashSet<string>(options, StringComparer.Ordinal);

        async Task<int> Add(string[] args, CliContext context)
        {
            var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(known) { "url", "code" });
            if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("code") is not { Length: > 0 } code
                || !Uri.TryCreate(parsed.Get("url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                await context.Error.WriteLineAsync($"Usage: gateway add <name> --url <https address> --code <pairing code> {where}");
                return CliExit.Usage;
            }

            var name = parsed.Words[0];
            if (!GatewayPairingApi.IsValidName(name)) { await context.Error.WriteLineAsync($"'{name}' is not a name: lower-case letters, digits, '-' and '_'."); return CliExit.Usage; }
            try
            {
                var paired = await pairings(args).PairAsync(identity(args), name, url, code, clientName, context.Cancellation);
                await context.Out.WriteLineAsync($"Paired with {paired.Url} ({paired.InstanceId}) as {name}. This server now tells it when it finishes writing a dataset.");
                context.Data["name"] = name;
                context.Data["url"] = paired.Url;
                context.Data["instance_id"] = paired.InstanceId;
                return CliExit.Ok;
            }
            catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
            {
                await context.Error.WriteLineAsync(ex.Message);
                return CliExit.Failure;
            }
        }

        async Task<int> List(string[] args, CliContext context)
        {
            var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(known));
            if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: gateway list {where}"); return CliExit.Usage; }
            var all = pairings(args).All();
            foreach (var p in all) await context.Out.WriteLineAsync($"{p.Name}  {p.Url}  {p.InstanceId}");
            if (all.Count == 0) await context.Out.WriteLineAsync("This server is not paired with a serving group.");
            context.Data["gateways"] = all.Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["url"] = p.Url, ["instance_id"] = p.InstanceId }).ToList();
            return CliExit.Ok;
        }

        async Task<int> Remove(string[] args, CliContext context)
        {
            var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(known));
            if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) { await context.Error.WriteLineAsync($"Usage: gateway remove <name> {where}"); return CliExit.Usage; }
            if (!pairings(args).Remove(parsed.Words[0])) { await context.Error.WriteLineAsync($"This server is not paired with '{parsed.Words[0]}'."); return CliExit.Failure; }
            await context.Out.WriteLineAsync($"Forgot {parsed.Words[0]}.");
            context.Data["removed"] = parsed.Words[0];
            return CliExit.Ok;
        }

        return new Noun("gateway", "The serving groups this server tells when it finishes writing a dataset",
        [
            new Verb("add", "Pair this server with a serving group (a Gateway) as a server client, with a code its administrator made (dbdatagateway server pair --max-role publisher)", EffectClass.RepoFilesOnly,
                $"<name> --url <https address> --code <pairing code> {where}", Add,
                DataSchemas.Object([("name", DataSchemas.String), ("url", DataSchemas.String), ("instance_id", DataSchemas.String)], "name", "url", "instance_id")),
            new Verb("list", "The serving groups this server is paired with", EffectClass.OfflineOnly, where, List,
                DataSchemas.Object([("gateways", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("url", DataSchemas.String), ("instance_id", DataSchemas.String)], "name", "url", "instance_id")))], "gateways")),
            new Verb("remove", "Forget a serving group (it keeps its cache; this server stops telling it)", EffectClass.RepoFilesOnly, $"<name> {where}", Remove,
                DataSchemas.Object([("removed", DataSchemas.String)], "removed")),
        ]);
    }
}
