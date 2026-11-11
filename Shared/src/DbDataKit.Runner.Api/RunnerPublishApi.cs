using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The publish API of a runner (plan 30 section 4, plan 28a R16), on the management API beside <see cref="RunnerApi"/>:
/// <list type="bullet">
/// <item><c>HEAD /api/v1/artifacts/{sha256}</c>: 200 when the file is stored, 404 when not. <c>PUT</c> uploads it, streamed; refused (400) when the hash of the bytes is not the one named, naming both; 413 past the size cap.</item>
/// <item><c>PUT /api/v1/definitions/integration/{name}</c>: publish. Body: <c>manifest</c> (package/1 YAML), <c>artifacts</c> (<c>path</c>, <c>sha256</c>, <c>size</c>, <c>media_type</c>), <c>depends_on</c>, <c>channel</c>, <c>mode</c> (<c>serve</c> or <c>stage</c>), <c>expected_version</c>, <c>note</c>, <c>signature</c>.</item>
/// <item><c>POST /api/v1/definitions/integration/{name}/activate|switch|rollback|retire</c>: body <c>channel</c> and <c>expected_version</c> (switch also <c>version</c>).</item>
/// <item><c>PUT /api/v1/bindings/{channel}/integration/{name}</c>: body <c>slots</c> (slot to what it is bound to), optional <c>expected_revision</c>.</item>
/// <item><c>GET /api/v1/definitions/integration[/{name}]</c>; <c>GET</c> and <c>PUT /api/v1/settings[/require_signatures]</c>.</item>
/// </list>
/// <b>Who.</b> A publisher (the role Publisher of a paired server client, with the owner rules of <see cref="OwnerScope"/>) uploads, publishes, stages and binds packages it created; it cannot change one another client or an administrator
/// made. A viewer reads. An administrator does everything and activates, switches, rolls back and retires. A refused call is audited (<see cref="RunnerPublishing.AuditRefused"/>) and answers 403.
/// </summary>
public static class RunnerPublishApi
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    /// <summary>Every call, for a host that pairs clients and reads their tokens (<c>dbdatarunner</c>).</summary>
    public static void Map(IEndpointRouteBuilder app, RunnerPublishing publishing)
    {
        MapReadAndLifecycle(app, publishing, "/api/v1", RunnerCallers.TokenResolver);
        MapWrite(app, publishing, "/api/v1", RunnerCallers.TokenResolver);
    }

    private static IResult? Deny(RunnerCaller caller, ServerRole needed, RunnerPublishing? publishing = null, string? verb = null, string? targetKind = null, string? targetId = null)
    {
        var decision = caller.Authorize(needed);
        if (decision.Allowed) return null;
        if (publishing is not null && verb is not null) publishing.AuditRefused(verb, caller.Actor, targetKind ?? "package", targetId ?? "-", decision.Reason ?? "the role is not enough");
        return Error(403, $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role.");
    }

    /// <summary>
    /// The calls that upload, publish, bind and set (<c>HEAD</c> and <c>PUT artifacts</c>, <c>PUT definitions</c>, <c>PUT bindings</c>, <c>PUT settings/require_signatures</c>) under <paramref name="prefix"/>, for whoever <paramref name="callerOf"/> says is calling.
    /// A paired client with a token keeps the owner rules of <see cref="OwnerScope"/>; a person signed in to a console (no token) is an administrator, who is not limited by owner, or reads, and a reader publishes nothing.
    /// </summary>
    public static void MapWrite(IEndpointRouteBuilder app, RunnerPublishing publishing, string prefix, RunnerCallerResolver callerOf)
    {
        var root = prefix.TrimEnd('/');
        app.MapMethods($"{root}/artifacts/{{sha256}}", ["HEAD"], (string sha256, HttpContext context) =>
        {
            var effective = callerOf(context).Roles;
            if (!effective.HasFlag(ServerRole.Publisher) && !ServerRoles.Allows(effective, ServerRole.Viewer)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return publishing.HasArtifact(sha256) ? Results.Ok() : Results.NotFound();
        });

        app.MapPut($"{root}/artifacts/{{sha256}}", async (string sha256, HttpContext context) =>
        {
            var token = callerOf(context);
            var actor = token.Actor;
            if (!RunnerPublishing.IsSha256(sha256)) return Error(400, $"'{sha256}' is not a SHA-256: write 64 hexadecimal digits.");
            if (Authorize(publishing, token, ObjectAction.Create, null, RunnerPublishing.UploadVerb, "artifact", sha256.ToLowerInvariant()) is { } denied) return denied;
            if (context.Request.ContentLength is { } length && length > publishing.MaxArtifactBytes)
                return Error(413, $"The file named {sha256} is {length} bytes, more than the cap of {publishing.MaxArtifactBytes}.");
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = publishing.MaxArtifactBytes;
            var media = context.Request.ContentType is { Length: > 0 } type && !type.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) ? type : null;
            try
            {
                var stored = await publishing.StoreArtifactAsync(sha256, context.Request.Body, media, actor, context.RequestAborted);
                return Results.Json(new JsonObject { ["sha256"] = stored.Sha256, ["size"] = stored.Size, ["already_stored"] = stored.Already });
            }
            catch (RunnerPublishException ex) { return Error(ex.Status, ex.Message); }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == 413) { return Error(413, $"The file named {sha256} is larger than the cap of {publishing.MaxArtifactBytes} bytes."); }
        }).DisableRequestTimeout();

        app.MapPut($"{root}/definitions/integration/{{name}}", async (string name, HttpContext context) =>
        {
            var token = callerOf(context);
            var actor = token.Actor;
            var body = await ReadBodyAsync(context);
            if (body.Error is { } bad) return bad;
            var input = ParsePublish(name, body.Json!, out var problem);
            if (input is null) return Error(400, problem!);

            var exists = publishing.Exists(name);
            var owned = new ManagedObject(publishing.Owner(name));
            if (Authorize(publishing, token, exists ? ObjectAction.Change : ObjectAction.Create, owned, RunnerPublishing.PublishVerb, "package", $"{name}/{input.Channel}") is { } denied) return denied;
            // the first publisher of a package owns it (an administrator who creates one leaves it to administrators)
            if (!exists && !IsAdmin(token) && !publishing.ClaimOwner(name, OwnerScope.Stamp(token.Token!)))
            {
                publishing.AuditRefused(RunnerPublishing.PublishVerb, actor, "package", $"{name}/{input.Channel}", "the package belongs to another client");
                return Error(403, $"The integration '{name}' belongs to another publisher.");
            }

            try
            {
                var outcome = publishing.Publish(input, actor);
                return Results.Json(new JsonObject
                {
                    ["kind"] = RunnerPublishing.Kind, ["name"] = outcome.Package, ["channel"] = outcome.Channel, ["version"] = outcome.Version, ["changed"] = outcome.Changed,
                    ["mode"] = outcome.Mode.ToString().ToLowerInvariant(), ["signature"] = outcome.Signature, ["tasks"] = new JsonArray([.. outcome.Tasks.Select(t => (JsonNode)JsonValue.Create(t)!)]),
                });
            }
            catch (RunnerPublishException ex) { return Error(ex.Status, ex.Message); }
        });

        app.MapPut($"{root}/bindings/{{channel}}/integration/{{name}}", async (string channel, string name, HttpContext context) =>
        {
            var token = callerOf(context);
            var body = await ReadBodyAsync(context);
            if (body.Error is { } bad) return bad;
            if (body.Json!["slots"] is not JsonObject slotsNode) return Error(400, "The body is {\"slots\": {\"<slot>\": \"<what it is bound to>\"}}.");
            var slots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (slot, value) in slotsNode)
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out var to)) return Error(400, $"The slot '{slot}' is bound to a text.");
                slots[slot] = to;
            }

            if (!Int(body.Json, "expected_revision", out var revision, out var problem)) return Error(400, problem!);
            // the slots of a package's first version are bound before it exists (a version that declares an unbound slot is refused): the binder then owns the name, as its first publisher would
            var exists = publishing.Exists(name);
            var owned = new ManagedObject(publishing.Owner(name));
            if (Authorize(publishing, token, exists || owned.ManagedBy is not null ? ObjectAction.Change : ObjectAction.Create, owned, RunnerPublishing.BindVerb, "package", $"{name}/{channel}") is { } denied) return denied;
            if (!exists && owned.ManagedBy is null && !IsAdmin(token) && !publishing.ClaimOwner(name, OwnerScope.Stamp(token.Token!)))
            {
                publishing.AuditRefused(RunnerPublishing.BindVerb, token.Actor, "package", $"{name}/{channel}", "the package belongs to another client");
                return Error(403, $"The integration '{name}' belongs to another publisher.");
            }

            List<string>? confirm = null;
            if (body.Json["confirm"] is JsonArray confirmed)
            {
                if (confirmed.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _))) return Error(400, "confirm is the list of scopes the answer to a refused bind named.");
                confirm = [.. confirmed.Select(x => (string)x!)];
            }

            try
            {
                var bindings = publishing.Bind(name, channel, slots, revision, token.Actor, confirm, Text(body.Json, "note"));
                return Results.Json(new JsonObject
                {
                    ["name"] = name, ["channel"] = channel,
                    ["bindings"] = new JsonArray([.. bindings.Select(b => (JsonNode)new JsonObject { ["slot"] = b.Slot, ["bound_to"] = b.BoundTo, ["revision"] = b.Revision })]),
                });
            }
            catch (RunnerPostureConfirmationException ex)
            {
                return Results.Json(new JsonObject
                {
                    ["error"] = ex.Message, ["setting"] = PostureSetting.ProductionBindingOnNonProduction.Name,
                    ["needsConfirmation"] = new JsonArray([.. ex.Needs.Select(n => (JsonNode)new JsonObject { ["scope"] = n.Scope, ["slot"] = n.Slot, ["release"] = n.Release, ["connection"] = n.Connection, ["effect"] = n.Effect })]),
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (RunnerPublishException ex) { return Error(ex.Status, ex.Message); }
        });

        app.MapPut($"{root}/settings/require_signatures", async (HttpContext context) =>
        {
            if (Deny(callerOf(context), ServerRole.Admin, publishing, RunnerPublishing.SettingVerb, "setting", RunnerPublishing.RequireSignaturesSetting) is { } denied) return denied;
            var body = await ReadBodyAsync(context);
            if (body.Error is { } bad) return bad;
            if (body.Json!["value"] is not JsonValue v || !v.TryGetValue<bool>(out var value)) return Error(400, "The body is {\"value\": true} or {\"value\": false}.");
            publishing.SetRequireSignatures(value, callerOf(context).Actor);
            return Results.Json(new JsonObject { [RunnerPublishing.RequireSignaturesSetting] = value });
        });
    }

    /// <summary>
    /// The read and life-cycle calls (list and describe the packages, <c>GET settings</c>, activate, switch, rollback and retire) under <paramref name="prefix"/>, for whoever <paramref name="caller"/> says is calling. The calls that
    /// upload, publish, bind and set are for publishing clients and are not here.
    /// </summary>
    public static void MapReadAndLifecycle(IEndpointRouteBuilder app, RunnerPublishing publishing, string prefix, RunnerCallerResolver caller)
    {
        var root = prefix.TrimEnd('/');
        app.MapGet($"{root}/definitions/integration", (HttpContext context) =>
            Deny(caller(context), ServerRole.Viewer) is { } denied ? denied : Results.Json(publishing.List()));

        app.MapGet($"{root}/definitions/integration/{{name}}", (string name, HttpContext context) =>
        {
            if (Deny(caller(context), ServerRole.Viewer) is { } denied) return denied;
            return publishing.Describe(name) is { } found ? Results.Json(found) : Error(404, $"There is no integration '{name}'.");
        });

        app.MapPost($"{root}/definitions/integration/{{name}}/{{act}}", async (string name, string act, HttpContext context) =>
        {
            if (act is not ("activate" or "switch" or "rollback" or "retire")) return Error(404, $"There is no call '{act}'; the life cycle is activate, switch, rollback and retire.");
            var callerOf = caller(context);
            var verb = act switch { "activate" => RunnerPublishing.ActivateVerb, "switch" => RunnerPublishing.SwitchVerb, "rollback" => RunnerPublishing.RollbackVerb, _ => RunnerPublishing.RetireVerb };
            var body = await ReadBodyAsync(context, optional: true);
            if (body.Error is { } bad) return bad;
            var json = body.Json ?? new JsonObject();
            var channel = Text(json, "channel") ?? Text(context.Request.Query["channel"]) ?? DefinitionStore.Production;
            if (Deny(callerOf, ServerRole.Admin, publishing, verb, "package", $"{name}/{channel}") is { } denied) return denied;
            if (!Int(json, "expected_version", out var expected, out var problem)) return Error(400, problem!);
            var actor = callerOf.Actor;
            try
            {
                switch (act)
                {
                    case "activate":
                        if (!Options(json, out var options, out problem)) return Error(400, problem!);
                        return Activation(name, channel, await publishing.ActivateAsync(name, channel, expected, actor, options, context.RequestAborted));
                    case "switch":
                        if (!Int(json, "version", out var version, out problem) || version is null) return Error(400, problem ?? "switch names the version: {\"channel\": \"prod\", \"version\": 3}.");
                        if (!Options(json, out var switchOptions, out problem)) return Error(400, problem!);
                        return Activation(name, channel, await publishing.SwitchAsync(name, channel, version.Value, expected, actor, switchOptions, context.RequestAborted));
                    case "rollback":
                        if (!Options(json, out var rollbackOptions, out problem)) return Error(400, problem!);
                        return Activation(name, channel, await publishing.RollbackAsync(name, channel, expected, actor, rollbackOptions, context.RequestAborted));
                    default:
                        var retired = publishing.Retire(name, channel, expected, actor);
                        return Results.Json(new JsonObject { ["name"] = name, ["channel"] = channel, ["retired"] = retired });
                }
            }
            catch (RunnerPublishException ex) { return Error(ex.Status, ex.Message); }
        });

        app.MapGet($"{root}/settings", (HttpContext context) =>
            Deny(caller(context), ServerRole.Viewer) is { } denied ? denied
                : Results.Json(new JsonObject { [RunnerPublishing.RequireSignaturesSetting] = publishing.RequireSignatures, ["max_artifact_bytes"] = publishing.MaxArtifactBytes }));
    }

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

        options = new ActivateOptions(confirm, losses, anyway, Text(json, "reason"));
        return true;
    }

    /// <summary>200 when the version moved; 202 when the activation is still waiting for the runs in flight (the busy tasks are named; ask again).</summary>
    private static IResult Activation(string name, string channel, DbDataKit.Runner.Staging.ActivationOutcome outcome)
    {
        var body = new JsonObject
        {
            ["name"] = name, ["channel"] = channel, ["active"] = outcome.Version, ["state"] = outcome.State.ToString().ToLowerInvariant(), ["message"] = outcome.Message,
            ["losses"] = new JsonArray([.. outcome.Losses.Select(l => (JsonNode)JsonValue.Create(l)!)]),
            ["busy_tasks"] = new JsonArray([.. outcome.BusyTasks.Select(t => (JsonNode)JsonValue.Create(t)!)]),
        };
        return Results.Json(body, statusCode: outcome.Activated ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
    }

    // ---- authorization

    private static bool IsAdmin(RunnerCaller caller) => caller.Roles.HasFlag(ServerRole.Admin);

    /// <summary>The owner rules of a publisher (create; change only what the client created); an administrator is not limited. Null when allowed, the 403 (audited) otherwise.</summary>
    private static IResult? Authorize(RunnerPublishing publishing, RunnerCaller caller, ObjectAction action, ManagedObject? target, string verb, string targetKind, string targetId)
    {
        // a client's token carries its id, which the owner rules read; a person signed in to a console has none and is an administrator (owner rules do not limit one) or has no right to publish
        var decision = caller.Token is { } token ? OwnerScope.Authorize(token, action, target)
            : caller.Roles.HasFlag(ServerRole.Admin) ? AuthorizationDecision.Allow(ServerRole.Admin, caller.Roles)
            : AuthorizationDecision.Deny(ServerRole.Publisher, caller.Roles, "publishing needs the publisher role (or admin) and the call holds neither");
        if (decision.Allowed) return null;
        publishing.AuditRefused(verb, caller.Actor, targetKind, targetId, decision.Reason ?? "the role is not enough");
        return Error(403, "This needs the publisher role, and for an existing package it must be one this client created, or the admin role.");
    }

    // ---- bodies

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private sealed record Body(JsonObject? Json, IResult? Error);

    private static async Task<Body> ReadBodyAsync(HttpContext context, bool optional = false)
    {
        if (context.Request.ContentLength is > MaxJsonBytes) return new Body(null, Error(413, $"The body is larger than {MaxJsonBytes / (1024 * 1024)} MB."));
        using var reader = new StreamReader(context.Request.Body);
        var text = await reader.ReadToEndAsync(context.RequestAborted);
        if (text.Length > MaxJsonBytes) return new Body(null, Error(413, $"The body is larger than {MaxJsonBytes / (1024 * 1024)} MB."));
        if (string.IsNullOrWhiteSpace(text)) return optional ? new Body(null, null) : new Body(null, Error(400, "The body is a JSON object."));
        try
        {
            return JsonNode.Parse(text) is JsonObject json ? new Body(json, null) : new Body(null, Error(400, "The body is a JSON object."));
        }
        catch (System.Text.Json.JsonException)
        {
            return new Body(null, Error(400, "The body is not JSON."));
        }
    }

    private static string? Text(JsonObject json, string key) => json[key] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? Text(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 0 || string.IsNullOrWhiteSpace(values[0]) ? null : values[0];

    private static bool Int(JsonObject json, string key, out int? value, out string? problem)
    {
        value = null;
        problem = null;
        if (json[key] is null) return true;
        if (json[key] is JsonValue v && v.TryGetValue<int>(out var number) && number >= 0)
        {
            value = number;
            return true;
        }

        problem = $"{key} is a whole number.";
        return false;
    }

    private static PublishInput? ParsePublish(string name, JsonObject json, out string? problem)
    {
        problem = null;
        if (Text(json, "manifest") is not { } manifest) { problem = "The body names the manifest: {\"manifest\": \"<package/1 YAML>\", \"artifacts\": [...], \"channel\": \"prod\", \"mode\": \"serve\"}."; return null; }
        var files = new List<PublishFile>();
        if (json["artifacts"] is { } artifacts)
        {
            if (artifacts is not JsonArray list) { problem = "artifacts is a list of {path, sha256, size, media_type}."; return null; }
            foreach (var item in list)
            {
                if (item is not JsonObject file || Text(file, "path") is not { } path || Text(file, "sha256") is not { } sha)
                {
                    problem = "Every artifact names its path and sha256.";
                    return null;
                }

                long size = 0;
                if (file["size"] is JsonValue s && !s.TryGetValue(out size)) { problem = $"The size of '{path}' is a whole number of bytes."; return null; }
                files.Add(new PublishFile(path, sha, size, Text(file, "media_type")));
            }
        }

        PublishMode? mode = null;
        if (Text(json, "mode") is { } modeText)
        {
            if (!Enum.TryParse<PublishMode>(modeText, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) { problem = $"'{modeText}' is not a mode: serve or stage."; return null; }
            mode = parsed;
        }

        List<string>? depends = null;
        if (json["depends_on"] is { } dependsNode)
        {
            if (dependsNode is not JsonArray list || list.Any(d => d is not JsonValue v || !v.TryGetValue<string>(out _))) { problem = "depends_on is a list of kind:name@version texts."; return null; }
            depends = [.. list.Select(d => (string)d!)];
        }

        if (!Int(json, "expected_version", out var expected, out problem)) return null;
        if (!Int(json, "format", out var format, out problem)) return null;
        return new PublishInput(name, manifest, files, Text(json, "channel") ?? DefinitionStore.Production, mode, depends, expected, Text(json, "note"), Text(json, "signature"), format);
    }
}
