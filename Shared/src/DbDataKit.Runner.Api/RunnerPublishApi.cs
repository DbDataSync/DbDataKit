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
/// <item><c>GET /api/v1/definitions/integration[/{name}]</c>; <c>GET</c> and <c>PUT /api/v1/settings[/require_signatures]</c>; <c>GET /api/v1/posture-choices</c> (the loosened defaults with their effect and the recorded choices) and <c>PUT /api/v1/posture-choices</c> (an administrator chooses with <c>confirm</c> or revokes, plan 28a R19).</item>
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

    /// <summary>The caller as the shared API sees it: a paired client's token carries its id, which the owner rules read; a person signed in to a console (no token) is an administrator or reads, and has no publisher role to use.</summary>
    private static DbDataKit.State.Api.DefinitionsCaller Shared(RunnerCaller caller) =>
        new(caller.Actor.Id, caller.Roles.HasFlag(ServerRole.Admin), caller.Token is not null && caller.Roles.HasFlag(ServerRole.Publisher), ServerRoles.Allows(caller.Roles, ServerRole.Viewer), caller.Token is { } token ? OwnerScope.Stamp(token) : null) { Context = caller };

    /// <summary>
    /// Hands a call to the shared API (<see cref="RunnerPublishing.Api"/>), which holds the roles, the owner rules and, for <c>integration</c>, the runner's rules as a kind hook (<see cref="IntegrationKindHook"/>); <paramref name="route"/> is relative to <c>/api/v1</c>.
    /// A life-cycle call may name its channel in the query.
    /// </summary>
    private static async Task<IResult> Forward(RunnerPublishing publishing, HttpContext context, RunnerCaller caller, string method, string route, bool hasBody = true, bool optionalBody = false)
    {
        string? text = null;
        if (hasBody)
        {
            if (context.Request.ContentLength is > MaxJsonBytes) return Error(413, $"The body is larger than {MaxJsonBytes / (1024 * 1024)} MB.");
            using var reader = new StreamReader(context.Request.Body);
            text = await reader.ReadToEndAsync(context.RequestAborted);
            if (text.Length > MaxJsonBytes) return Error(413, $"The body is larger than {MaxJsonBytes / (1024 * 1024)} MB.");
            if (string.IsNullOrWhiteSpace(text) && !optionalBody) return Error(400, "The body is a JSON object.");
            if (optionalBody && Text(context.Request.Query["channel"]) is { } queried)
            {
                JsonObject? json = null;
                try { json = string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject; }
                catch (System.Text.Json.JsonException) { }
                if (json is not null && json["channel"] is null)
                {
                    json["channel"] = queried;
                    text = json.ToJsonString();
                }
            }
        }

        var result = await publishing.Api.HandleAsync(method, route, text, Shared(caller), context.RequestAborted);
        return Results.Json(result.Body, statusCode: result.Status);
    }

    /// <summary>A route handler that answers with the result of <paramref name="handler"/>: a lambda of only the context that returns a task of a result would be taken for a request delegate, which drops the result and answers an empty 200 (ASP0016).</summary>
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;

    private static string Esc(string part) => Uri.EscapeDataString(part);

    private const string Integration = RunnerPublishing.Kind;

    /// <summary>
    /// The calls that upload, publish, bind and set (<c>HEAD</c> and <c>PUT artifacts</c>, <c>PUT definitions</c>, <c>PUT bindings</c>, the trust store's keys, <c>PUT settings/require_signatures</c>) under <paramref name="prefix"/>, for whoever <paramref name="callerOf"/> says is calling.
    /// A paired client with a token keeps the owner rules of <see cref="OwnerScope"/>; a person signed in to a console (no token) is an administrator, who is not limited by owner, or reads, and a reader publishes nothing. The calls are the shared API's
    /// (<see cref="DbDataKit.State.Api.DefinitionsApi"/>) with the runner's rules as the hook of the <c>integration</c> kind.
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
            var caller = callerOf(context);
            if (context.Request.ContentLength is { } length && length > publishing.MaxArtifactBytes)
                return Error(413, $"The file named {sha256} is {length} bytes, more than the cap of {publishing.MaxArtifactBytes}.");
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = publishing.MaxArtifactBytes;
            var media = context.Request.ContentType is { Length: > 0 } type && !type.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) ? type : null;
            try
            {
                var stored = await publishing.Api.StoreArtifactAsync(sha256, context.Request.Body, media, Shared(caller), context.RequestAborted);
                return Results.Json(stored.Body, statusCode: stored.Status);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == 413) { return Error(413, $"The file named {sha256} is larger than the cap of {publishing.MaxArtifactBytes} bytes."); }
        }).DisableRequestTimeout();

        app.MapPut($"{root}/definitions/{Integration}/{{name}}", (string name, HttpContext context) =>
            Forward(publishing, context, callerOf(context), "PUT", $"definitions/{Integration}/{Esc(name)}"));

        app.MapPut($"{root}/bindings/{{channel}}/{Integration}/{{name}}", (string channel, string name, HttpContext context) =>
            Forward(publishing, context, callerOf(context), "PUT", $"bindings/{Esc(channel)}/{Integration}/{Esc(name)}"));

        app.MapDelete($"{root}/bindings/{{channel}}/{Integration}/{{name}}/{{slot}}", (string channel, string name, string slot, HttpContext context) =>
            Forward(publishing, context, callerOf(context), "DELETE", $"bindings/{Esc(channel)}/{Integration}/{Esc(name)}/{Esc(slot)}", hasBody: false));

        // the trust store: the publisher keys that sign integrations (an administrator adds, rotates by adding the new key and revoking the old, plan 30 PB03)
        app.MapPut($"{root}/trust/{Integration}/keys", Handler(context => Forward(publishing, context, callerOf(context), "PUT", $"trust/{Integration}/keys")));
        app.MapPost($"{root}/trust/{Integration}/{{keyId}}/revoke", (string keyId, HttpContext context) => Forward(publishing, context, callerOf(context), "POST", $"trust/{Integration}/{Esc(keyId)}/revoke", hasBody: false));

        app.MapPut($"{root}/settings/require_sandbox_run", async (HttpContext context) =>
        {
            if (Deny(callerOf(context), ServerRole.Admin, publishing, RunnerPublishing.SettingVerb, "setting", RunnerPublishing.RequireSandboxRunSetting) is { } denied) return denied;
            var body = await ReadBodyAsync(context);
            if (body.Error is { } bad) return bad;
            if (body.Json!["value"] is not JsonValue v || !v.TryGetValue<bool>(out var value)) return Error(400, "The body is {\"value\": true} or {\"value\": false}.");
            publishing.SetRequireSandboxRun(value, callerOf(context).Actor);
            return Results.Json(new JsonObject { [RunnerPublishing.RequireSandboxRunSetting] = value });
        });

        // the posture (plan 28a R19): a default loosened on purpose is a recorded choice with the effect shown. PUT {"setting", "scope" (a package or "*"), "confirm": true, "note"} chooses; {"setting", "scope", "revoke": true} takes it back. Administrator, audited by the guard (runner.posture.choose / .revoke).
        app.MapPut($"{root}/posture-choices", async (HttpContext context) =>
        {
            var caller = callerOf(context);
            if (Deny(caller, ServerRole.Admin, publishing, PostureGuard.ChooseVerb, PostureGuard.SettingKind, "-") is { } denied) return denied;
            var body = await ReadBodyAsync(context);
            if (body.Error is { } bad) return bad;
            if (Text(body.Json!, "setting") is not { } settingName || PostureSetting.Find(settingName) is not { } setting)
                return Error(400, $"setting is one of: {string.Join(", ", PostureSetting.All.Select(x => x.Name))}.");
            if (Text(body.Json!, "scope") is not { } scope || (scope != RunnerModule.AllPackages && !PackageManifestValidator.NameRule.IsMatch(scope)))
                return Error(400, $"scope is a package name or '{RunnerModule.AllPackages}' for every package.");
            if (setting == PostureSetting.ProductionBindingOnNonProduction)
                return Error(400, $"'{setting.Name}' is confirmed where the binding is made (a bind that shares a production connection names it), not here.");
            var revoke = body.Json!["revoke"] is JsonValue r && r.TryGetValue<bool>(out var wanted) && wanted;
            var note = Text(body.Json!, "note");
            try
            {
                if (revoke)
                    return publishing.Posture.Revoke(setting, scope, caller.Actor.Id, note)
                        ? Results.Json(new JsonObject { ["setting"] = setting.Name, ["scope"] = scope, ["revoked"] = true })
                        : Error(404, $"There is no choice of '{setting.Name}' for '{scope}'.");
                var confirmed = body.Json!["confirm"] is JsonValue c && c.TryGetValue<bool>(out var yes) && yes;
                var choice = publishing.Posture.Choose(setting, scope, caller.Actor.Id, note, confirmed);
                return Results.Json(Choice(choice));
            }
            catch (PostureRefusedException ex) { return Error(409, ex.Message); }
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
    /// The read and life-cycle calls (list and describe the packages, <c>GET settings</c>, <c>GET trust</c>, activate, switch, rollback, retire and prune) under <paramref name="prefix"/>, for whoever <paramref name="caller"/> says is calling. The calls that
    /// upload, publish, bind and set are for publishing clients and are not here.
    /// </summary>
    public static void MapReadAndLifecycle(IEndpointRouteBuilder app, RunnerPublishing publishing, string prefix, RunnerCallerResolver caller)
    {
        var root = prefix.TrimEnd('/');
        app.MapGet($"{root}/definitions/{Integration}", Handler(context => Forward(publishing, context, caller(context), "GET", $"definitions/{Integration}", hasBody: false)));

        app.MapGet($"{root}/definitions/{Integration}/{{name}}", (string name, HttpContext context) => Forward(publishing, context, caller(context), "GET", $"definitions/{Integration}/{Esc(name)}", hasBody: false));

        app.MapGet($"{root}/trust/{Integration}", Handler(context => Forward(publishing, context, caller(context), "GET", $"trust/{Integration}", hasBody: false)));

        app.MapPost($"{root}/definitions/{Integration}/{{name}}/{{act}}", (string name, string act, HttpContext context) =>
            Forward(publishing, context, caller(context), "POST", $"definitions/{Integration}/{Esc(name)}/{Esc(act)}", optionalBody: true));

        app.MapGet($"{root}/posture-choices", (HttpContext context) =>
        {
            if (Deny(caller(context), ServerRole.Viewer) is { } denied) return denied;
            return Results.Json(new JsonObject
            {
                ["all_packages"] = RunnerModule.AllPackages,
                ["settings"] = new JsonArray([.. PostureSetting.All.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["default"] = x.Default, ["effect"] = x.Effect })]),
                ["choices"] = new JsonArray([.. publishing.Posture.Choices.List().Select(Choice)]),
            });
        });

        app.MapGet($"{root}/settings", (HttpContext context) =>
            Deny(caller(context), ServerRole.Viewer) is { } denied ? denied
                : Results.Json(new JsonObject { [RunnerPublishing.RequireSignaturesSetting] = publishing.RequireSignatures, [RunnerPublishing.RequireSandboxRunSetting] = publishing.RequireSandboxRun, ["max_artifact_bytes"] = publishing.MaxArtifactBytes }));
    }

    // ---- bodies

    private static JsonNode Choice(PostureChoice c) => new JsonObject { ["setting"] = c.Setting, ["scope"] = c.Scope, ["chosen_by"] = c.ChosenBy, ["chosen_at"] = c.ChosenAt.UtcDateTime.ToString("O"), ["note"] = c.Note };

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
}
