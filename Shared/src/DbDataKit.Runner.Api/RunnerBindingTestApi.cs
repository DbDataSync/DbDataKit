using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// <c>POST {prefix}/bindings/{channel}/integration/{name}/test</c>, body <c>{"slot": "source", "role": "default"}</c> (UI08): tries the binding of one connection slot the way a run would use it, and says how far it got. Administrator, audited.
/// The steps are: the slot is bound in the channel (<c>binding</c>); the connection it names is one this runner has and its document loads (<c>connection</c>); every secret the login names resolves (<c>login</c>); and, when the host gives a
/// <see cref="ConnectTest"/>, the connection opens (<c>connect</c>). The answer is <c>{slot, bound_to, ok, step, message, connected}</c>: <c>step</c> is the one that failed or, when <c>ok</c>, the last that was tried, and <c>connected</c> says whether a
/// connection was really opened. The message names the slot, the connection and the reference, never a value: the login the resolver reveals is handed to the connect test and dropped, and is in no answer, audit line or log.
/// </summary>
public static class RunnerBindingTestApi
{
    public const string Verb = "runner.binding.test";

    /// <summary>Opens a connection with the grant and says nothing but why it failed (null: it opened). The grant holds the secrets; an implementation never returns or logs them.</summary>
    public delegate Task<string?> ConnectTest(ConnectionGrant grant, CancellationToken ct);

    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<ISlotResolver?> slots, string prefix, RunnerCallerResolver callerOf, ConnectTest? connect = null, string serviceId = RunnerNames.ServiceId)
    {
        var audit = new AuditLog(state, serviceId);
        var publisher = new DbDataKit.State.DefinitionPublisher(state, RunnerPackages.Kinds(), new PayloadStore(state), "runner");

        app.MapPost($"{prefix.TrimEnd('/')}/bindings/{{channel}}/integration/{{name}}/test", async (string channel, string name, HttpContext context) =>
        {
            var caller = callerOf(context);
            var target = $"{name}/{channel}";
            var decision = caller.Authorize(ServerRole.Admin);
            if (!decision.Allowed)
            {
                Record(audit, caller, target, AuditEvent.Refused);
                return Results.Json(new { error = "This needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            }

            JsonObject? body;
            try { body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { body = null; }
            var slot = (string?)body?["slot"];
            var role = (string?)body?["role"] ?? "default";
            if (string.IsNullOrWhiteSpace(slot) || role is not ("default" or "read" or "write"))
                return Results.Json(new { error = "The body is {\"slot\": \"<slot>\", \"role\": \"default|read|write\"}." }, statusCode: StatusCodes.Status400BadRequest);

            var bound = publisher.Bindings(channel, RunnerPackages.Kind, name).FirstOrDefault(b => b.Slot == slot)?.BoundTo;
            JsonObject Answer(bool ok, string step, string message, bool connected = false) => new() { ["slot"] = slot, ["bound_to"] = bound, ["ok"] = ok, ["step"] = step, ["message"] = message, ["connected"] = connected };
            if (bound is null)
            {
                Record(audit, caller, $"{target}/{slot}", AuditEvent.Ok, "binding");
                return Results.Json(Answer(false, "binding", $"The slot '{slot}' is not bound in channel '{channel}'."));
            }

            var resolver = slots();
            if (resolver is null) return Results.Json(new { error = "This runner is not ready to resolve a connection yet." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            JsonObject result;
            try
            {
                var grant = await resolver.ResolveAsync(slot, bound, role, context.RequestAborted);
                var failure = connect is null ? null : await connect(grant, context.RequestAborted);
                result = failure is not null ? Answer(false, "connect", failure)
                    : connect is null ? Answer(true, "login", $"The slot '{slot}' is bound to '{bound}', which this runner has, and its login resolves. No connection was opened.")
                    : Answer(true, "connect", $"The slot '{slot}' opened a connection through '{bound}'.", connected: true);
            }
            catch (SlotResolutionException ex)
            {
                // the exception names the slot, the connection and the reference (never a value)
                result = Answer(false, ex.Message.Contains("does not know", StringComparison.Ordinal) || ex.Message.Contains("cannot be read", StringComparison.Ordinal) ? "connection" : "login", ex.Message);
            }

            Record(audit, caller, $"{target}/{slot}", (bool)result["ok"]! ? AuditEvent.Ok : AuditEvent.Failed, (string)result["step"]!);
            return Results.Json(result);
        });
    }

    private static void Record(AuditLog audit, RunnerCaller caller, string target, string outcome, string? step = null) =>
        audit.Record(new AuditEvent("person", caller.Actor.Id, Verb, outcome, ViaKind: caller.Actor.ViaKind, ViaId: caller.Actor.ViaId, TargetKind: "binding", TargetId: target, Detail: step is null ? null : JsonSerializer.Serialize(new { step })));
}
