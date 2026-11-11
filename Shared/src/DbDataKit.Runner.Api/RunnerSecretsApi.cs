using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The calls through which a product hands a runner a login it will run with (plan 28c E20d, E19), on the management API of <c>dbdatarunner</c>:
/// <list type="bullet">
/// <item><c>PUT /api/v1/secrets/{name}</c>, body <c>{"value": "..."}</c>: stores the value under the name (<c>dbdata:&lt;product&gt;:&lt;kind&gt;:&lt;name&gt;[:&lt;part&gt;]</c>) in the runner's encrypted store. Administrator.</item>
/// <item><c>GET /api/v1/secrets[?prefix=]</c>: the names the store holds, never a value. Administrator. <c>DELETE /api/v1/secrets/{name}</c>: removes one. Administrator.</item>
/// </list>
/// A value is read from the body, handed to the store and nowhere else: it is not in the audit, a log line, an answer or an error. The audit says who stored or removed which name and, for a refused call, why. A runner that cannot open
/// its store (no key it may create, a file it may not write) answers 503 saying so, and stores nothing. The names are the product's own references (<c>dbdata:sync:connection:&lt;name&gt;:run</c>), which a connection of the runner
/// names in its <c>auth</c>, so a runner holding the value runs the replication with no step on its machine.
/// </summary>
public static class RunnerSecretsApi
{
    public const string SetVerb = "runner.secret.set";
    public const string DeleteVerb = "runner.secret.delete";
    public const string TargetKind = "secret";

    /// <summary>The longest a value may be: a login, not a file.</summary>
    public const int MaxValueChars = 16 * 1024;

    /// <summary>Opens the runner's store when a call needs it; throws (and says why) when the runner cannot store a secret.</summary>
    public delegate ISecretStore OpenStore();

    /// <summary>What the store holds with the date each was written when it says (names and dates, never values); the overview asks it. The default lists the names with no date.</summary>
    public delegate Task<IReadOnlyList<SecretHeld>> ListHeld(ISecretStore store, CancellationToken ct);

    public static async Task<IReadOnlyList<SecretHeld>> NamesOnly(ISecretStore store, CancellationToken ct)
    {
        var held = new List<SecretHeld>();
        await foreach (var name in store.ListAsync("", ct)) held.Add(new SecretHeld(name, null));
        return held;
    }

    /// <param name="connections">The connection inventory of this runner (<c>GET /api/v1/connections</c>), which <c>GET {prefix}/secrets/overview</c> reads the references from; null: the overview names no reference.</param>
    /// <param name="listHeld">What the store holds, with dates; null: the names only.</param>
    public static void Map(IEndpointRouteBuilder app, StateDatabase state, OpenStore store, string prefix, RunnerCallerResolver callerOf, string serviceId = RunnerNames.ServiceId, Func<JsonObject>? connections = null, ListHeld? listHeld = null)
    {
        var root = prefix.TrimEnd('/');
        var audit = new AuditLog(state, serviceId);

        IResult? Deny(HttpContext context, string verb, string target)
        {
            var caller = callerOf(context);
            var decision = caller.Authorize(ServerRole.Admin);
            if (decision.Allowed) return null;
            Record(audit, verb, caller.Actor, target, AuditEvent.Refused, new { status = 403, reason = decision.Reason ?? "the role is not enough" });
            return Error(403, "This needs the admin role.");
        }

        app.MapPut($"{root}/secrets/{{name}}", async (string name, HttpContext context) =>
        {
            if (Deny(context, SetVerb, name) is { } denied) return denied;
            if (!SecretName.TryParse(name, out var parsed)) return Error(400, $"'{name}' is not a secret name; expected 'dbdata:<product>:<kind>:<name>[:<part>]'.");
            JsonObject? body;
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var text = await reader.ReadToEndAsync(context.RequestAborted);
                if (text.Length > MaxValueChars * 2) return Error(413, "The body is larger than a login can be.");
                body = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }

            if (body?["value"] is not JsonValue v || !v.TryGetValue<string>(out var value) || value.Length == 0 || value.Length > MaxValueChars)
                return Error(400, $"The body is {{\"value\": \"...\"}}, a value of 1 to {MaxValueChars} characters.");
            ISecretStore opened;
            try { opened = store(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error(503, $"This runner cannot store a secret: {ex.Message}");
            }

            try
            {
                using var secret = SecretValue.From(value);
                await opened.SetAsync(parsed, secret, context.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error(503, $"This runner cannot store a secret: {ex.Message}");
            }

            Record(audit, SetVerb, callerOf(context).Actor, parsed.ToString(), AuditEvent.Ok, new { length = value.Length });
            return Results.Json(new JsonObject { ["name"] = parsed.ToString() });
        });

        // UI06: what the store holds against what the connections bound on this runner name: missing, present, outside and unused; names, references and dates only
        app.MapGet($"{root}/secrets/overview", async (HttpContext context) =>
        {
            if (!callerOf(context).Authorize(ServerRole.Admin).Allowed) return Error(403, "This needs the admin role.");
            IReadOnlyList<SecretHeld> held;
            try { held = await (listHeld ?? NamesOnly)(store(), context.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Error(503, $"This runner cannot read its secrets: {ex.Message}"); }
            var uses = new List<ReferenceUse>();
            foreach (var connection in (connections?.Invoke()?["connections"] as JsonArray) ?? [])
            {
                if (connection is not JsonObject c) continue;
                var users = (c["used_by"] as JsonArray)?.Select(u => (string?)u?["name"]).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct().ToList() ?? [];
                foreach (var key in new[] { "secret_ref", "run_secret_ref" })
                    if (SecretRef.TryParse((string?)c[key], out var reference))
                        uses.AddRange((users.Count == 0 ? [(string)c["name"]!] : users).Select(u => new ReferenceUse(u, reference)));
            }
            return Results.Json(SecretsOverview.Build("dbdatarunner", "runner-store", held, uses));
        });

        app.MapGet($"{root}/secrets", async (HttpContext context) =>
        {
            var caller = callerOf(context);
            if (!caller.Authorize(ServerRole.Admin).Allowed) return Error(403, "This needs the admin role.");
            ISecretStore opened;
            try { opened = store(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Error(503, $"This runner cannot store a secret: {ex.Message}"); }
            var names = new JsonArray();
            await foreach (var name in opened.ListAsync(context.Request.Query["prefix"].ToString(), context.RequestAborted))
                names.Add(name.ToString());
            return Results.Json(new JsonObject { ["names"] = names });
        });

        app.MapDelete($"{root}/secrets/{{name}}", async (string name, HttpContext context) =>
        {
            if (Deny(context, DeleteVerb, name) is { } denied) return denied;
            if (!SecretName.TryParse(name, out var parsed)) return Error(400, $"'{name}' is not a secret name; expected 'dbdata:<product>:<kind>:<name>[:<part>]'.");
            ISecretStore opened;
            try { opened = store(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Error(503, $"This runner cannot store a secret: {ex.Message}"); }
            await opened.DeleteAsync(parsed, context.RequestAborted);
            Record(audit, DeleteVerb, callerOf(context).Actor, parsed.ToString(), AuditEvent.Ok, null);
            return Results.Json(new JsonObject { ["name"] = parsed.ToString() });
        });
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static void Record(AuditLog audit, string verb, RunnerActor actor, string target, string outcome, object? detail) =>
        audit.Record(new AuditEvent("person", actor.Id, verb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: TargetKind, TargetId: target,
            Detail: detail is null ? null : JsonSerializer.Serialize(detail)));
}
