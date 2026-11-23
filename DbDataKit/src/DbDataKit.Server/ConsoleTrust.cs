using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>What one pass over a server did about the console's publisher key.</summary>
/// <param name="Done">True when nothing is left to do on this server: the key is trusted for every kind, or the server is not a Runner or a Gateway.</param>
/// <param name="Added">The kinds the key was added for now.</param>
/// <param name="Already">The kinds that already trusted it.</param>
/// <param name="Revoked">The kinds where an administrator revoked it; the console does not add it again.</param>
/// <param name="Problem">Why the pass could not finish (the server did not answer, or refused), in words for the log and the audit; null when it finished.</param>
public sealed record PublisherTrust(bool Done, IReadOnlyList<string> Added, IReadOnlyList<string> Already, IReadOnlyList<string> Revoked, string? Problem)
{
    public static PublisherTrust NotATarget { get; } = new(true, [], [], [], null);

    public static PublisherTrust Failed(string problem) => new(false, [], [], [], problem);
}

/// <summary>
/// Puts the console's publisher key (<see cref="ConsolePublisherKeyStore"/>) in the trust store of every Runner and Gateway group the console manages, so a
/// version the console signs verifies there. The console asks the server which kinds it takes (<c>GET /api/v1/trust</c>) and adds its key, labelled with its
/// own id, for each kind that does not list it (<c>PUT /api/v1/trust/{kind}/keys</c>), as itself with the administrator role the join gave it. A kind where an
/// administrator revoked the key is left so: the console never trusts itself again behind the group's back. Instances of one group share the trust store, so
/// the second instance finds the key there already.
/// Design: docs/install.md#the-consoles-publisher-key
/// </summary>
public sealed class ConsoleTrust(ConsoleInstances instances, ConsoleRelay relay, ConsoleKeyStore identity, ConsolePublisherKeyStore keys, AuditLog audit, ILogger<ConsoleTrust> log)
{
    /// <summary>The audit verb of a key the console added to a group, or a group that refused it.</summary>
    public const string TrustVerb = "server.trust";

    /// <summary>The kind a Runner of a release before <c>GET /api/v1/trust</c> takes: it answers that call with 404, and has only this kind.</summary>
    private const string RunnerKind = "integration";

    /// <summary>The label the key carries in a group's trust store: which console it is.</summary>
    public static string LabelFor(string consoleId) => $"{ConsoleInstances.ClientName} {consoleId}";

    /// <summary>Makes sure <paramref name="instance"/> trusts the console's publisher key for every kind it takes.</summary>
    public async Task<PublisherTrust> EnsureAsync(RegisteredInstance instance, CancellationToken ct = default)
    {
        var status = await instances.StatusAsync(instance, ct);
        if (!status.Reachable) return Failed(instance, $"{instance.Name} {status.Problem ?? "did not answer"}");
        if (status.Product is not (ServerProducts.Runner or ServerProducts.Gateway)) return PublisherTrust.NotATarget;

        var key = await keys.GetOrCreateAsync(ct);
        var (listed, body) = await relay.CallAsConsoleAsync(instance, ServerRole.Admin, HttpMethod.Get, "/api/v1/trust", null, ct);
        IReadOnlyList<(string Kind, JsonArray Keys)> kinds;
        if (listed == 200 && body?["kinds"] is JsonArray all)
            kinds = [.. all.OfType<JsonObject>().Select(k => ((string?)k["kind"] ?? "", k["keys"] as JsonArray ?? [])).Where(k => k.Item1.Length > 0)];
        else if (listed == 404 && status.Product == ServerProducts.Runner)
        {
            var (one, oneBody) = await relay.CallAsConsoleAsync(instance, ServerRole.Admin, HttpMethod.Get, $"/api/v1/trust/{RunnerKind}", null, ct);
            if (one != 200) return Failed(instance, Said(instance, one, oneBody, $"GET /api/v1/trust/{RunnerKind}"));
            kinds = [(RunnerKind, oneBody?["keys"] as JsonArray ?? [])];
        }
        else return Failed(instance, Said(instance, listed, body, "GET /api/v1/trust"));

        List<string> added = [], already = [], revoked = [];
        var label = LabelFor((await identity.GetOrCreateAsync(ct)).Id);
        foreach (var (kind, held) in kinds)
        {
            var mine = held.OfType<JsonObject>().FirstOrDefault(k => (string?)k["key_id"] == key.Fingerprint);
            if (mine is not null)
            {
                (mine["revoked"]?.GetValue<bool>() == true ? revoked : already).Add(kind);
                continue;
            }

            var (put, putBody) = await relay.CallAsConsoleAsync(instance, ServerRole.Admin, HttpMethod.Put, $"/api/v1/trust/{Uri.EscapeDataString(kind)}/keys", new { public_key = Url.Encode(key.PublicKey), label }, ct);
            if (put != 200)
            {
                var problem = Said(instance, put, putBody, $"PUT /api/v1/trust/{kind}/keys");
                Record(instance, status.Product, key.Fingerprint, added, revoked, problem);
                return new PublisherTrust(false, added, already, revoked, problem);
            }
            added.Add(kind);
        }

        if (added.Count > 0 || revoked.Count > 0) Record(instance, status.Product, key.Fingerprint, added, revoked, null);
        return new PublisherTrust(true, added, already, revoked, null);
    }

    private static string Said(RegisteredInstance instance, int status, JsonNode? body, string call) =>
        $"{instance.Name} answered {status} to {call}" + ((string?)body?["error"] is { } error ? $": {error}" : "");

    private PublisherTrust Failed(RegisteredInstance instance, string problem)
    {
        log.LogInformation("The console's publisher key is not trusted by {Server} yet: {Problem}", instance.Name, problem);
        return PublisherTrust.Failed(problem);
    }

    private void Record(RegisteredInstance instance, string? product, string keyId, IReadOnlyList<string> added, IReadOnlyList<string> revoked, string? problem)
    {
        if (problem is not null) log.LogWarning("The console's publisher key could not be trusted by {Server}: {Problem}", instance.Name, problem);
        audit.Record(new AuditEvent("console", "console", TrustVerb, problem is null ? AuditEvent.Ok : AuditEvent.Refused, TargetKind: "server", TargetId: instance.InstanceId,
            Detail: JsonSerializer.Serialize(new { server = instance.Name, product, key_id = keyId, added, revoked, error = problem })));
    }
}

/// <summary>
/// Runs <see cref="ConsoleTrust"/> over the servers the console manages: every registered server once after the console starts, a server that joined or
/// paired at once (<see cref="Request"/>), and a server that could not be reached again later, waiting longer each time up to <see cref="LongestWait"/>.
/// A server whose pass finished is not asked again until it joins or pairs again, or the console restarts.
/// </summary>
public sealed class ConsoleTrustService(InstanceRegistry registry, ConsoleTrust trust, ILogger<ConsoleTrustService> log, TimeProvider? clock = null) : BackgroundService
{
    /// <summary>How long after the console starts the first pass waits, so a start does nothing but start.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan[] Waits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), LongestWait];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Due, int Tries)> _due = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _done = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Asks for a pass over this server now: it joined or paired, so its trust store may not have the key, even if an earlier pass found it there.</summary>
    public void Request(string instanceId)
    {
        _done.TryRemove(instanceId, out _);
        _due[instanceId] = (_clock.GetUtcNow(), 0);
        _wake.Release();
    }

    /// <summary>One pass over every server that is due now; returns when the next one is due (null when none waits).</summary>
    public async Task<DateTimeOffset?> PassAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var instance in registry.All())
        {
            if (_done.ContainsKey(instance.InstanceId)) continue;
            var (due, tries) = _due.GetOrAdd(instance.InstanceId, _ => (now, 0));
            if (due > now) continue;
            PublisherTrust outcome;
            try { outcome = await trust.EnsureAsync(instance, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Trusting the console's publisher key on {Server} failed", instance.Name);
                outcome = PublisherTrust.Failed(ex.Message);
            }
            if (outcome.Done)
            {
                _done[instance.InstanceId] = true;
                _due.TryRemove(instance.InstanceId, out _);
            }
            else _due[instance.InstanceId] = (now + Waits[Math.Min(tries, Waits.Length - 1)], tries + 1);
        }
        return _due.IsEmpty ? null : _due.Values.Min(d => d.Due);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstWait, _clock, stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset? next;
            try { next = await PassAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            var wait = next is { } at ? at - _clock.GetUtcNow() : LongestWait;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            if (wait > LongestWait) wait = LongestWait;
            try { await _wake.WaitAsync(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
