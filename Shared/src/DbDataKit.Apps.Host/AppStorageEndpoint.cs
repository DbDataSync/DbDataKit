using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Apps;

/// <summary>
/// The storage call of the shell page (plan 26, AP09): one JSON body <c>{op: get|set|delete|list, scope: user|instance, key, value, ifVersion, prefix}</c>. The person is the signed-in session's, never the body's, so one person cannot name another's
/// <c>user</c> storage, and the instance is the path's, so instances do not share. An app that declares no storage in its manifest gets a 403.
/// </summary>
public static class AppStorageEndpoint
{
    private const long Megabyte = 1024 * 1024;
    private const int MaximumBody = 300 * 1024;

    public static async Task HandleAsync(HttpContext context, AppStorage storage, ResolvedInstance resolved, AppSession session)
    {
        JsonObject? request;
        try
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            if (body.Length > MaximumBody) { await Fail(context, 413, "value", "The request is too large."); return; }
            request = JsonNode.Parse(body.ToArray()) as JsonObject;
        }
        catch (JsonException) { request = null; }
        if (request is null) { await Fail(context, 400, "request", "The body is a JSON object."); return; }

        var op = (string?)request["op"];
        var scope = (string?)request["scope"] ?? "";
        if (!AppStorage.IsScope(scope)) { await Fail(context, 400, "scope", "The scope is `user` or `instance`."); return; }
        var use = resolved.Manifest.StorageUse;
        if (!(scope == AppStorage.User ? use.User : use.Instance)) { await Fail(context, 403, "undeclared", $"The app's manifest does not declare `storage.{scope}`."); return; }

        var name = resolved.Instance.Name;
        var person = session.Identity.UserId;
        var limits = new AppStorageLimits(QuotaBytes: Math.Min(use.QuotaMb * Megabyte, new AppStorageLimits().QuotaBytes));
        var key = (string?)request["key"] ?? "";
        try
        {
            switch (op)
            {
                case "get":
                    await Json(context, 200, storage.Get(name, scope, person, key) is { } found ? new JsonObject { ["found"] = true, ["value"] = JsonNode.Parse(found.Json), ["version"] = found.Version } : new JsonObject { ["found"] = false });
                    break;
                case "set":
                    var version = storage.Set(name, scope, person, key, request["value"]?.ToJsonString() ?? "null", limits, request["ifVersion"] is { } v ? (int)v : null);
                    await Json(context, 200, new JsonObject { ["version"] = version });
                    break;
                case "delete":
                    await Json(context, 200, new JsonObject { ["deleted"] = storage.Delete(name, scope, person, key) });
                    break;
                case "list":
                    await Json(context, 200, new JsonObject { ["items"] = new JsonArray(storage.List(name, scope, person, (string?)request["prefix"] ?? "").Select(i => (JsonNode)new JsonObject { ["key"] = i.Key, ["value"] = JsonNode.Parse(i.Json), ["version"] = i.Version }).ToArray()) });
                    break;
                default:
                    await Fail(context, 400, "op", "The operation is get, set, delete or list.");
                    break;
            }
        }
        catch (AppStorageException e)
        {
            await Fail(context, e.Kind switch { "conflict" => 409, "quota" => 413, _ => 400 }, e.Kind, e.Message);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException)
        {
            await Fail(context, 400, "request", "A field has the wrong type.");
        }
    }

    private static async Task Json(HttpContext context, int status, JsonObject body)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(body.ToJsonString());
    }

    private static Task Fail(HttpContext context, int status, string kind, string message) => Json(context, status, new JsonObject { ["error"] = kind, ["message"] = message });
}
