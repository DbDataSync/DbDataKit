using System.Text.Json.Nodes;

namespace DbDataKit.Server;

/// <summary>What one server said about its checks when it was asked: its overview as it sent it, or why it gave none.</summary>
public sealed record ServerQualityAnswer(string Server, string InstanceId, string? Product, JsonObject? Overview, string? Error);

/// <summary>
/// The suite's data quality as the console sees it (plan 23a FQ17): each server that evaluates checks (a Sync server, a Build server) is asked for its results through the relay, as the signed-in person, and the answers are joined by dataset
/// identity, so a table that Sync writes and a Build model reads is one dataset with the checks of both. A server that does not answer is named and left out; the others still show. The history of one check and an evaluation on request
/// are the server's own calls (<c>/relay/{server}/api/...</c>): the console holds no results and decides no block.
/// </summary>
public sealed class ConsoleQuality(ConsoleInstances instances, ConsoleRelay relay)
{
    /// <summary>The path of a product's check results on its management API, or null for a product that keeps none (yet).</summary>
    public static string? PathOf(string? product) => product switch { ServerProducts.Build => "/api/v1/quality", ServerProducts.Sync => "/api/quality", _ => null };

    /// <summary>The latest result of every check in an overview as the lineage graph reads it (plan 23a FQ16): one <see cref="DbDataKit.Lineage.NodeCheck"/> per check and server.</summary>
    public static IReadOnlyList<DbDataKit.Lineage.NodeCheck> ChecksOf(JsonObject overview)
    {
        var all = new List<DbDataKit.Lineage.NodeCheck>();
        foreach (var dataset in overview["datasets"]?.AsArray() ?? [])
            foreach (var check in dataset?["checks"]?.AsArray() ?? [])
                if (dataset?["dataset"]?.GetValue<string>() is { } identity && check?["check_id"]?.GetValue<string>() is { } id && check["status"]?.GetValue<string>() is { } status)
                    all.Add(new DbDataKit.Lineage.NodeCheck(identity, id, status, check["blocking"]?.GetValue<bool>() ?? false, check["blocked_since"]?.GetValue<DateTimeOffset>()));
        return all;
    }

    /// <summary>The sample keys of a check's latest failed result as its server keeps them (the history route), for an alert; empty when the server does not answer or has none.</summary>
    public async Task<IReadOnlyList<string>> SampleKeysAsync(string serverId, string? product, string dataset, string checkId, CancellationToken ct)
    {
        if (instances.Registry.Find(serverId) is not { } instance || PathOf(product) is not { } path) return [];
        var (code, body) = await relay.CallAsync(instance, HttpMethod.Get, $"{path}/history?dataset={Uri.EscapeDataString(dataset)}&check={Uri.EscapeDataString(checkId)}", null, ct);
        if (code != 200 || body?["results"]?.AsArray() is not { } results) return [];
        var latest = results.FirstOrDefault(r => r?["status"]?.GetValue<string>() == "fail");
        return [.. (latest?["sample_keys"]?.AsArray() ?? []).Select(k => k?.GetValue<string>()).Where(k => k is not null).Select(k => k!)];
    }

    public async Task<JsonObject> OverviewAsync(CancellationToken ct)
    {
        var statuses = await instances.StatusOfAllAsync(ct);
        var answers = (await Task.WhenAll(statuses.Select(s => AnswerAsync(s, ct)))).Where(a => a is not null).Select(a => a!).OrderBy(a => a.Server, StringComparer.Ordinal).ToList();

        var datasets = new SortedDictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var answer in answers.Where(a => a.Overview is not null))
            foreach (var dataset in answer.Overview!["datasets"]?.AsArray() ?? [])
                foreach (var check in dataset!["checks"]!.AsArray())
                {
                    var copy = (JsonObject)check!.DeepClone();
                    copy["server"] = answer.Server;
                    copy["server_id"] = answer.InstanceId;
                    copy["product"] = answer.Product;
                    var identity = (string)dataset["dataset"]!;
                    if (!datasets.TryGetValue(identity, out var list)) datasets[identity] = list = [];
                    list.Add(copy);
                }

        return new JsonObject
        {
            ["datasets"] = new JsonArray(datasets.Select(d => (JsonNode)new JsonObject
            {
                ["dataset"] = d.Key,
                ["blocking"] = d.Value.Count(c => (bool)c["blocking"]!),
                ["checks"] = new JsonArray(d.Value.OrderBy(c => (string)c["check_id"]!, StringComparer.Ordinal).ThenBy(c => (string)c["server"]!, StringComparer.Ordinal).Select(c => (JsonNode)c).ToArray()),
            }).ToArray()),
            ["servers"] = new JsonArray(answers.Select(a => (JsonNode)new JsonObject
            {
                ["server"] = a.Server, ["server_id"] = a.InstanceId, ["product"] = a.Product, ["error"] = a.Error,
                ["checks"] = a.Overview?["datasets"]?.AsArray().Sum(d => d!["checks"]!.AsArray().Count),
                ["can_run"] = a.Overview is not null && (bool?)a.Overview["can_run"] == true, ["run_note"] = (string?)a.Overview?["run_note"],
            }).ToArray()),
        };
    }

    private async Task<ServerQualityAnswer?> AnswerAsync(InstanceStatus status, CancellationToken ct)
    {
        var instance = status.Instance;
        if (!status.Reachable) return new(instance.Name, instance.InstanceId, status.Product, null, status.Problem ?? "It did not answer.");
        if (PathOf(status.Product) is not { } path) return null;                      // a Gateway or a Store evaluates no checks yet: not part of this
        var (code, body) = await relay.CallAsync(instance, HttpMethod.Get, path, null, ct);
        if (code != 200 || body is not JsonObject overview) return new(instance.Name, instance.InstanceId, status.Product, null, (body as JsonObject)?["error"]?.GetValue<string>() ?? $"It answered {code}.");
        return new(instance.Name, instance.InstanceId, status.Product, overview, null);
    }
}
