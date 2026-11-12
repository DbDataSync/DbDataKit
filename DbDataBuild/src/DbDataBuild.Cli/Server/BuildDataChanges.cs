using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Secrets;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// Build's producer of <c>data.changed/1</c> (plan 24a UC13): when a refresh the server ran (from a screen, a call or its own schedule) ends, every load that finished is a dataset that changed, and the serving groups this server is paired with
/// (<c>build/proxies.json</c>, the pairing it publishes with) are told. The server signs as itself (<see cref="DataChangeNotifier"/>). The dataset is named as lineage names the model's table (<see cref="BuildLineage.IdentityOf"/>,
/// the connection's address from the logins the server stores), so it is the name the Gateway's graph holds for the table. A dry run, a load that failed or was skipped, and a server with no pairing tell no one; a failure to tell never changes the run.
/// </summary>
internal sealed class BuildDataChanges(IDataChangeNotifier notifier, string serverDirectory, Action<string>? log = null)
{
    public const string Product = "build";

    public IDataChangeNotifier Notifier => notifier;

    /// <summary>The pairings of <c>build/proxies.json</c> as the notifier reads them.</summary>
    internal sealed class FromProxies(ProxiesFile proxies) : IGatewayPairings
    {
        public IReadOnlyList<GatewayPairing> All() => [.. proxies.All().Select(p => new GatewayPairing(p.Name, p.Url, p.InstanceId, p.Fingerprint, p.PairedAt))];
    }

    public static BuildDataChanges Create(string serverDirectory, ConsoleKeyStore identity, Action<string>? line = null, DataChangeNotifierOptions? options = null) =>
        new(new DataChangeNotifier(Product, new FromProxies(new ProxiesFile(serverDirectory)), identity.GetOrCreateAsync, line is null ? null : new LineLogger(line), options), serverDirectory, line);

    /// <summary>The datasets a refresh changed, from the document <c>connection refresh --format json</c> printed (<paramref name="output"/>); empty for anything else.</summary>
    internal static IReadOnlyList<(string Connection, string Model)> LoadedModels(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        JsonObject? data;
        try { data = (JsonNode.Parse(output) as JsonObject)?["data"] as JsonObject; }
        catch (JsonException) { return []; }
        if (data is null || data["dry_run"] is JsonValue { } dry && dry.TryGetValue<bool>(out var isDry) && isDry) return [];
        if (data["connection"] is not JsonValue c || !c.TryGetValue<string>(out var connection) || connection.Length == 0 || data["results"] is not JsonArray results) return [];
        var loaded = new List<(string, string)>();
        foreach (var result in results.OfType<JsonObject>())
            if (result["state"] is JsonValue s && s.TryGetValue<string>(out var state) && state == "done" && result["model"] is JsonValue m && m.TryGetValue<string>(out var model) && model.Length > 0)
                loaded.Add((connection, model));
        return loaded;
    }

    /// <summary>A refresh of a project ended: tells the serving groups about the models it loaded. Never throws and never waits for the network.</summary>
    public async Task RefreshCompletedAsync(string projectRoot, string? runId, string? output, CancellationToken ct = default)
    {
        try
        {
            var loaded = LoadedModels(output);
            if (loaded.Count == 0) return;
            var context = ProjectContext.Load(projectRoot);
            var environment = await ServerLineage.EnvironmentAsync(serverDirectory, ct).ConfigureAwait(false);
            var identityOf = BuildLineage.IdentityOf(context.Config, environment);
            var changes = new List<DataChange>();
            foreach (var (connection, model) in loaded.Distinct())
            {
                var (schema, table) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(model);
                changes.Add(new DataChange(LineageIdentity.Dataset(identityOf(connection), schema, table).ToString(), runId));
            }

            notifier.Publish(changes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // the run is over and its result stands: not being able to name the datasets is a counted failure, not an error of the run
            log?.Invoke($"The serving groups were not told which datasets the refresh changed ({ex.GetType().Name}).");
        }
    }
}
