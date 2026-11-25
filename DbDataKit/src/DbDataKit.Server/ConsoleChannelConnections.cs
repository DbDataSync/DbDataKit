using System.Text.Json.Nodes;
using DbDataKit.Deploy;

namespace DbDataKit.Server;

/// <summary>
/// The console's side of putting a connection on a Runner for a channel: a registered Runner reached as the signed-in person (<see cref="ConsolePromotions.LinkFor"/>), so the Runner checks that person's roles and records them in its
/// audit, and <see cref="RunnerChannelConnections"/> makes the calls (a login stored, a copy put, a slot bound). A screen that sets up a connection for development calls <see cref="SetUpAsync"/>; the Workspace server takes no part and
/// never sees a login.
/// Design: docs/channel-bindings.md#the-runner-what-it-keeps-and-how-the-console-hands-it-a-connection
/// </summary>
public sealed class ConsoleChannelConnections(ConsoleCatalogRead runners, ConsolePromotions promotions)
{
    /// <summary>The calls to the Runner <paramref name="runner"/> (a registered server's id or name) as <paramref name="person"/>, or the console's refusal when no such Runner is registered.</summary>
    public async Task<(RunnerChannelConnections? Calls, CatalogReadAnswer? Error)> OnRunnerAsync(string? runner, RelayPerson person, CancellationToken ct)
    {
        var (instance, problem) = await runners.RunnerAsync(runner, ct);
        if (problem is not null) return (null, problem);
        if (promotions.LinkFor(instance!.InstanceId, person) is not { } link) return (null, CatalogReadAnswer.Error(404, $"No server '{runner}' is registered."));
        return (new RunnerChannelConnections(link, new DeployTarget(instance.Name, instance.Url.ToString().TrimEnd('/'), instance.InstanceId, DeployProducts.Runner)), null);
    }

    /// <summary>
    /// Sets up <paramref name="request"/> on the Runner <paramref name="runner"/> as <paramref name="person"/>. The answer is 200 with the copy, the bind and the steps when the slot is bound; 409 with <c>needsConfirmation</c> when the
    /// Runner holds the bind for an administrator's confirmation (the login and the copy are on the Runner already); the console's or the Runner's refusal otherwise. No answer carries the login's value.
    /// </summary>
    public async Task<CatalogReadAnswer> SetUpAsync(string? runner, ChannelConnectionSetUp request, RelayPerson person, CancellationToken ct)
    {
        var (calls, problem) = await OnRunnerAsync(runner, person, ct);
        if (problem is not null) return problem;
        ChannelConnectionResult result;
        try { result = await calls!.SetUpAsync(request, null, ct); }
        catch (DeployException ex) { return CatalogReadAnswer.Error(ex.Status, ex.Message); }

        var answer = new JsonObject
        {
            ["runner"] = calls.Target.InstanceId, ["runner_name"] = calls.Target.Name, ["package"] = request.Package, ["channel"] = request.Channel, ["slot"] = request.Slot,
            ["connection"] = result.Copy.Name, ["version"] = result.Copy.Version, ["kind"] = result.Copy.Kind, ["login_secret"] = result.LoginSecret, ["bound"] = result.Bind.Bound,
            ["unresolved"] = new JsonArray([.. result.Copy.Unresolved.Select(s => (JsonNode?)new JsonObject { ["path"] = s.Path, ["reference"] = s.Reference, ["problem"] = s.Problem })]),
            ["steps"] = new JsonArray([.. result.Steps.Select(s => (JsonNode?)s)]),
        };
        if (result.Bind.Bound) return new(200, answer);
        answer["error"] = result.Bind.Error;
        answer["needsConfirmation"] = new JsonArray([.. result.Bind.Needs.Select(n => (JsonNode?)new JsonObject
        {
            ["scope"] = n.Scope, ["slot"] = n.Slot, ["release"] = n.Channel, ["connection"] = n.Connection, ["effect"] = n.Effect,
        })]);
        return new(409, answer);
    }
}
