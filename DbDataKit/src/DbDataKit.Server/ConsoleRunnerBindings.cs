using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Connections;
using DbDataKit.State;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server;

/// <summary>A secret reference of a connection the runner cannot resolve (never a value).</summary>
public sealed record RunnerSecretProblem(string Path, string Reference, string? Problem);

/// <summary>A connection definition the console put on a runner: the version both sides compute, and the references the runner cannot resolve.</summary>
public sealed record ConnectionSent(string Connection, string Version, IReadOnlyList<RunnerSecretProblem> Unresolved);

/// <summary>What a call to a runner answered, as the console tells it: the status, the words, and for a bind that needs the administrator's confirmation, the scopes.</summary>
public sealed record RunnerStep(int Status, string? Error, IReadOnlyList<UnconfirmedBinding> Needs, IReadOnlyList<string> Diagnostics)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// How the console hands a runner what a binding needs (plan 27a IX23, <c>docs/channel-bindings.md</c>): the Runner is a separate server and does not read the console's <c>connections/</c> folder. When an administrator binds a runner slot to a console connection,
/// or sends it again, the console puts the connection's <b>definition with its secret references, never a value</b> on the runner, which keeps its own copy and resolves the references in its own store; then it binds the slot, and the runner decides whether a
/// non-production channel that uses production's connection needs the administrator's confirmation (409, recorded in the runner's own <c>posture_choice</c>). A runner is called through the relay as the signed-in person.
/// </summary>
public sealed class ConsoleRunnerBindings(ConsoleConnections connections, ConsoleInstances instances, ConsoleRelay relay)
{
    /// <summary>The version of a console connection as the runner compares it: the hash of the file's text, the same on both sides. Null when the console has no readable connection of that name.</summary>
    public string? VersionOf(string name) => TextOf(name) is { } yaml ? ConnectionCopyVersion.Of(yaml) : null;

    /// <summary>The text of a console connection's file, when it reads (a file that does not read is not sent).</summary>
    public string? TextOf(string name) => ConsoleConnections.IsName(name) && connections.Find(name) is not null ? File.ReadAllText(connections.FileOf(name)) : null;

    /// <summary>Puts the definition of the console connection <paramref name="name"/> on the runner <paramref name="server"/> (a registered server's id or name).</summary>
    public async Task<(RunnerStep Step, ConnectionSent? Sent)> SendAsync(string server, string name, string personId, string? personName, CancellationToken ct)
    {
        if (TextOf(name) is not { } yaml) return (new RunnerStep(404, $"The console has no connection '{name}' that reads, so there is nothing to send.", [], []), null);
        if (instances.Registry.Find(server) is not { } instance) return (new RunnerStep(404, $"No server '{server}' is registered.", [], []), null);
        if ((await instances.StatusAsync(instance, ct)).Product != ServerProducts.Runner) return (new RunnerStep(409, $"'{instance.Name}' is not a runner.", [], []), null);
        var (status, body) = await relay.CallAsync(instance, HttpMethod.Put, $"/api/v1/connection-copies/{Uri.EscapeDataString(name)}", new { definition = yaml }, ct);
        if (status != 200 || body is not JsonObject copy) return (new RunnerStep(status, body?["error"]?.GetValue<string>() ?? $"{instance.Name} did not accept the connection.", [], DiagnosticsOf(body)), null);
        var unresolved = (copy["secrets"] as JsonArray ?? []).OfType<JsonObject>().Where(s => s["resolves"]?.GetValue<bool>() == false)
            .Select(s => new RunnerSecretProblem(s["path"]?.GetValue<string>() ?? "", s["reference"]?.GetValue<string>() ?? "", s["problem"]?.GetValue<string>())).ToList();
        return (new RunnerStep(200, null, [], []), new ConnectionSent(name, copy["version"]?.GetValue<string>() ?? "", unresolved));
    }

    /// <summary>Binds one slot of a package in one channel of the runner to a connection.</summary>
    public async Task<RunnerStep> BindAsync(string server, string package, string channel, string slot, string connection, IReadOnlyCollection<string>? confirm, string? note, string personId, string? personName, CancellationToken ct)
    {
        if (instances.Registry.Find(server) is not { } instance) return new RunnerStep(404, $"No server '{server}' is registered.", [], []);
        var (status, body) = await relay.CallAsync(instance, HttpMethod.Put, $"/api/v1/bindings/{Uri.EscapeDataString(channel)}/integration/{Uri.EscapeDataString(package)}",
            new { slots = new Dictionary<string, string> { [slot] = connection }, confirm, note }, ct);
        if (status == 409 && body?["needsConfirmation"] is JsonArray needs)
            return new RunnerStep(409, body["error"]?.GetValue<string>(), [.. needs.OfType<JsonObject>().Select(n => new UnconfirmedBinding(n["scope"]?.GetValue<string>() ?? "", "runner", n["slot"]?.GetValue<string>() ?? "", n["release"]?.GetValue<string>() ?? "", n["connection"]?.GetValue<string>() ?? "", n["effect"]?.GetValue<string>() ?? ""))], []);
        return new RunnerStep(status, status == 200 ? null : body?["error"]?.GetValue<string>() ?? $"{instance.Name} did not bind the slot.", [], []);
    }

    /// <summary>Empties one slot's binding in one channel of the runner (the runner refuses while the version it serves declares the slot). A slot that was not bound is already empty.</summary>
    public async Task<RunnerStep> UnbindAsync(string server, string package, string channel, string slot, string personId, string? personName, CancellationToken ct)
    {
        if (instances.Registry.Find(server) is not { } instance) return new RunnerStep(404, $"No server '{server}' is registered.", [], []);
        var (status, body) = await relay.CallAsync(instance, HttpMethod.Delete, $"/api/v1/bindings/{Uri.EscapeDataString(channel)}/integration/{Uri.EscapeDataString(package)}/{Uri.EscapeDataString(slot)}", null, ct);
        return status is 200 or 404 ? new RunnerStep(200, null, [], []) : new RunnerStep(status, body?["error"]?.GetValue<string>() ?? $"{instance.Name} did not unbind the slot.", [], []);
    }

    private static IReadOnlyList<string> DiagnosticsOf(JsonNode? body) => body?["diagnostics"] is JsonArray list ? [.. list.Select(d => d?.GetValue<string>() ?? "")] : [];
}
