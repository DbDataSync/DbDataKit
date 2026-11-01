using System.Collections.Concurrent;
using DbDataKit.Lineage;

namespace DbDataConsole.Server;

/// <summary>What one server said about its lineage when it was asked: its graph, what it left out, or why it gave none.</summary>
public sealed record ServerLineageAnswer(string Server, string InstanceId, string? Product, LineageGraph? Graph, IReadOnlyList<string> Problems, string? Error, DateTimeOffset At);

/// <summary>The graphs of every server, joined (plan 18, G09).</summary>
public sealed record JoinedLineage(LineageGraph Graph, IReadOnlyList<ServerLineageAnswer> Servers, string? JoinError);

/// <summary>
/// The suite's lineage as the console sees it: each server that keeps one (a Build server, a Sync server) is asked for its design graph through the relay, as the signed-in person, and the graphs are joined by identity, so a
/// table Sync writes and a Build model reads is one node and the flow runs through it. An answer is kept for <see cref="Lifetime"/> and asked again after that or when a refresh is asked for. A server that does not answer, or
/// answers something that is not a graph, is named and left out; the others still show. Graphs that cannot be joined (together they would make data flow in a circle) are said, and the answers are still returned.
/// </summary>
public sealed class ConsoleLineage(ConsoleInstances instances, ConsoleRelay relay, TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ServerLineageAnswer> _answers = new(StringComparer.Ordinal);

    /// <summary>The path of a product's lineage on its management API, or null for a product that keeps none (yet).</summary>
    public static string? PathOf(string? product) => product switch { "dbdatabuild" => "/api/v1/lineage", "dbdatasync" => "/api/lineage", _ => null };

    public async Task<JoinedLineage> JoinedAsync(bool refresh, CancellationToken ct)
    {
        var statuses = await instances.StatusOfAllAsync(ct);
        var answers = await Task.WhenAll(statuses.Select(status => AnswerAsync(status, refresh, ct)));
        var kept = answers.Where(a => a is not null).Select(a => a!).OrderBy(a => a.Server, StringComparer.Ordinal).ToList();
        try { return new JoinedLineage(LineageGraph.Merge(kept.Where(a => a.Graph is not null).Select(a => a.Graph!)), kept, null); }
        catch (LineageGraphException ex) { return new JoinedLineage(LineageGraph.Empty, kept, $"The servers' graphs cannot be joined: {ex.Message}"); }
    }

    private async Task<ServerLineageAnswer?> AnswerAsync(InstanceStatus status, bool refresh, CancellationToken ct)
    {
        var instance = status.Instance;
        if (!status.Reachable) return new(instance.Name, instance.InstanceId, status.Product, null, [], status.Problem ?? "It did not answer.", _clock.GetUtcNow());
        if (PathOf(status.Product) is not { } path) return null;                      // a Proxy or a Store has no lineage of its own yet: not part of this
        if (!refresh && _answers.TryGetValue(instance.InstanceId, out var known) && _clock.GetUtcNow() - known.At < Lifetime) return known;

        var (code, body) = await relay.CallAsync(instance, HttpMethod.Get, path, null, ct);
        ServerLineageAnswer answer;
        if (code != 200) answer = new(instance.Name, instance.InstanceId, status.Product, null, [], body?["error"]?.GetValue<string>() ?? $"It answered {code}.", _clock.GetUtcNow());
        else
        {
            try
            {
                var problems = body?["problems"] is System.Text.Json.Nodes.JsonArray list ? list.Select(p => p?.GetValue<string>() ?? "").ToList() : [];
                answer = new(instance.Name, instance.InstanceId, status.Product, LineageJson.Read(body), problems, null, _clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is LineageGraphException or InvalidOperationException or FormatException)
            {
                answer = new(instance.Name, instance.InstanceId, status.Product, null, [], $"It sent something that is not a lineage graph: {ex.Message}", _clock.GetUtcNow());
            }
        }
        // only an answer is kept: a server that was down is asked again next time
        if (answer.Error is null) _answers[instance.InstanceId] = answer; else _answers.TryRemove(instance.InstanceId, out _);
        return answer;
    }
}
