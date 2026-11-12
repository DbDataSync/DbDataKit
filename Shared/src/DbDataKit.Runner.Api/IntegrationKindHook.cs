using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Api;

namespace DbDataKit.Runner.Api;

/// <summary>
/// What the runner adds to the shared publish API for its <c>integration</c> kind (plan 30 PB02): the channel the host serves, the trigger-loop check, the task projection a served version replaces, the progress migration and the holds of an
/// activation (runs in flight, the instances' readiness, the sandbox run), and the posture of a binding that shares production's connection. The rules are <see cref="RunnerPublishing"/>'s; this is how <see cref="DefinitionsApi"/> reaches them.
/// A publisher does not browse (only an administrator and a viewer list), a refused registry rule answers 400 as the runner always did, and the runner's own <c>require_signatures</c> is what requires signatures.
/// </summary>
internal sealed class IntegrationKindHook(RunnerPublishing publishing) : IDefinitionKindHook
{
    public string Kind => RunnerPublishing.Kind;

    public bool PublisherMayRead => false;

    public bool? SignaturesRequired => publishing.RequireSignatures;

    public string? UnsignedRefusal(string name, SignatureVerdict verdict) => RunnerPublishing.UnsignedMessage(name, verdict);

    public int? StatusOf(PublishRefusal reason) => reason switch { PublishRefusal.UnknownKind => 404, _ => 400 };

    private static T Translate<T>(Func<T> work)
    {
        try { return work(); }
        catch (RunnerPostureConfirmationException ex)
        {
            throw new DefinitionsRefusalException(ex.Status, ex.Message)
            {
                Body = new JsonObject
                {
                    ["error"] = ex.Message, ["setting"] = PostureSetting.ProductionBindingOnNonProduction.Name,
                    ["needsConfirmation"] = new JsonArray([.. ex.Needs.Select(n => (JsonNode)new JsonObject { ["scope"] = n.Scope, ["slot"] = n.Slot, ["release"] = n.Release, ["connection"] = n.Connection, ["effect"] = n.Effect })]),
                },
            };
        }
        catch (RunnerPublishException ex) { throw new DefinitionsRefusalException(ex.Status, ex.Message); }
    }

    private static void Translate(Action work) => Translate<object?>(() => { work(); return null; });

    public void CheckChannel(string channel) => Translate(() => publishing.RequireChannelServed(channel));

    public void BeforePublish(PublishHookContext context) => Translate(() => publishing.CheckPublish(context.Name, context.Channel, context.Manifest));

    public void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response)
    {
        if (result.Mode == PublishMode.Serve) publishing.RebuildProjection(context.Name, context.Channel);
        response["tasks"] = new JsonArray([.. RunnerPublishing.TasksOf(context.Manifest).Select(t => (JsonNode)JsonValue.Create(t)!)]);
    }

    public async Task<DefinitionsResult?> ActAsync(LifecycleHookContext context, CancellationToken ct)
    {
        var actor = ActorOf(context.Caller);
        var (name, channel) = (context.Name, context.Channel);
        try
        {
            switch (context.Act)
            {
                case "activate":
                    if (!Options(context.Body, out var options, out var problem)) return Bad(problem!);
                    return Activation(name, channel, await publishing.ActivateAsync(name, channel, context.ExpectedVersion, actor, options, ct).ConfigureAwait(false));
                case "switch":
                    if (context.Version is null) return Bad("switch names the version: {\"channel\": \"prod\", \"version\": 3}.");
                    if (!Options(context.Body, out var switchOptions, out problem)) return Bad(problem!);
                    return Activation(name, channel, await publishing.SwitchAsync(name, channel, context.Version.Value, context.ExpectedVersion, actor, switchOptions, ct).ConfigureAwait(false));
                case "rollback":
                    if (!Options(context.Body, out var rollbackOptions, out problem)) return Bad(problem!);
                    return Activation(name, channel, await publishing.RollbackAsync(name, channel, context.ExpectedVersion, actor, rollbackOptions, ct).ConfigureAwait(false));
                default:
                    var retired = publishing.Retire(name, channel, context.ExpectedVersion, actor);
                    return new DefinitionsResult(200, new JsonObject { ["kind"] = Kind, ["name"] = name, ["channel"] = channel, ["retired"] = retired });
            }
        }
        catch (RunnerPublishException ex) { return new DefinitionsResult(ex.Status, Error(ex.Message)); }
    }

    private static DefinitionsResult Bad(string message) => new(400, Error(message));

    private static JsonObject Error(string message) => new() { ["error"] = message, ["message"] = message };

    private static RunnerActor ActorOf(DefinitionsCaller caller) => caller.Context is RunnerCaller known ? known.Actor : new RunnerActor(caller.Actor, null, "api", caller.ClientId);

    public void BeforeBind(BindingHookContext context)
    {
        List<string>? confirm = null;
        if (context.Body["confirm"] is JsonArray confirmed)
        {
            if (confirmed.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _))) throw new DefinitionsRefusalException(400, "confirm is the list of scopes the answer to a refused bind named.");
            confirm = [.. confirmed.Select(x => (string)x!)];
        }

        Translate(() => publishing.CheckBind(context.Name, context.Channel, context.Slots, confirm));
    }

    public void AfterBind(BindingHookContext context) =>
        publishing.ReconcilePosture(context.Name, ActorOf(context.Caller), context.Body["note"] is JsonValue n && n.TryGetValue<string>(out var note) && !string.IsNullOrWhiteSpace(note) ? note : null);

    public void BeforeUnbind(BindingHookContext context) => Translate(() => publishing.CheckUnbind(context.Name, context.Channel, context.Slots.Keys.Single()));

    public DefinitionsResult? AfterUnbind(BindingHookContext context, bool removed)
    {
        var slot = context.Slots.Keys.Single();
        if (!removed) return new DefinitionsResult(404, Error($"The slot '{slot}' of '{context.Name}' is not bound in channel '{context.Channel}'."));
        publishing.ReconcilePosture(context.Name, ActorOf(context.Caller), null);
        return new DefinitionsResult(200, new JsonObject { ["name"] = context.Name, ["channel"] = context.Channel, ["slot"] = slot, ["unbound"] = true, ["removed"] = true });
    }

    public JsonObject? List() => publishing.List();

    public JsonObject? Describe(string name) => publishing.Describe(name);

    /// <summary>The administrator's words of an activation: <c>confirm_losses</c> with the list echoed in <c>losses</c>, <c>anyway</c> and its <c>reason</c>.</summary>
    private static bool Options(JsonObject json, out ActivateOptions options, out string? problem)
    {
        options = new ActivateOptions();
        problem = null;
        var confirm = false;
        var anyway = false;
        if (json["confirm_losses"] is { } c && !(c is JsonValue cv && cv.TryGetValue(out confirm))) { problem = "confirm_losses is true or false."; return false; }
        if (json["anyway"] is { } a && !(a is JsonValue av && av.TryGetValue(out anyway))) { problem = "anyway is true or false."; return false; }
        List<string>? losses = null;
        if (json["losses"] is { } l)
        {
            if (l is not JsonArray list || list.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _))) { problem = "losses is the list of texts the activation named."; return false; }
            losses = [.. list.Select(x => (string)x!)];
        }

        options = new ActivateOptions(confirm, losses, anyway, json["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) && !string.IsNullOrWhiteSpace(reason) ? reason : null);
        return true;
    }

    /// <summary>200 when the version moved; 202 when the activation is still waiting for the runs in flight (the busy tasks are named; ask again).</summary>
    private static DefinitionsResult Activation(string name, string channel, DbDataKit.Runner.Staging.ActivationOutcome outcome) =>
        new(outcome.Activated ? 200 : 202, new JsonObject
        {
            ["name"] = name, ["channel"] = channel, ["active"] = outcome.Version, ["state"] = outcome.State.ToString().ToLowerInvariant(), ["message"] = outcome.Message,
            ["losses"] = new JsonArray([.. outcome.Losses.Select(l => (JsonNode)JsonValue.Create(l)!)]),
            ["busy_tasks"] = new JsonArray([.. outcome.BusyTasks.Select(t => (JsonNode)JsonValue.Create(t)!)]),
        });
}
