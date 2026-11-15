using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Web.Trust;

/// <summary>
/// How a console hands an instance its group's key (plan 28c E20d), on the management API of any server whose group keeps values with a <see cref="GroupKey"/> that is not made on the instance:
/// <list type="bullet">
/// <item><c>PUT {prefix}/group/key</c>, body <c>{"key": "..."}</c> (administrator): the key is checked against the group's check value (the first key the group is given becomes its key) and kept in the instance's own store. 200
/// <c>{"secret", "already"}</c>; 400 for a text that is not a key; 409 for a key that is not the group's (nothing kept); 503 when the instance cannot keep a secret.</item>
/// <item><c>GET {prefix}/group/key</c> (viewer): <c>{"secret", "held", "group_has_key"}</c>: whether this instance holds the key and whether the group has one at all. Never the key.</item>
/// </list>
/// The key is read from the body and handed to the store, nowhere else: it is not in the audit, a log line, an answer or an error. Each call that changes something, and each refusal, is told to <c>audit</c> with the outcome and
/// words, never the key.
/// </summary>
public static class GroupKeyApi
{
    /// <summary>The longest body taken: a key is 44 characters of base64.</summary>
    private const int MaxBody = 4096;

    /// <summary>What the API tells the host's audit: the outcome (<c>ok</c>, <c>refused</c>, <c>failed</c>) and the words, never the key.</summary>
    public delegate void Audit(HttpContext context, string outcome, object detail);

    /// <param name="app">The server's endpoints.</param>
    /// <param name="prefix">The management API's prefix (<c>/api/v1</c>).</param>
    /// <param name="key">The group's key on this instance.</param>
    /// <param name="audit">Where each change and refusal is recorded.</param>
    /// <param name="allowed">Whether the caller holds the role (<see cref="ServerRole.Admin"/> to give the key, <see cref="ServerRole.Viewer"/> to read the status).</param>
    /// <param name="held">Whether this instance holds the key, without opening anything it does not have yet; null: <see cref="GroupKey.Held"/>.</param>
    public static void Map(IEndpointRouteBuilder app, string prefix, Func<GroupKey> key, Func<HttpContext, ServerRole, bool> allowed, Audit audit, Func<bool>? held = null)
    {
        var path = $"{prefix.TrimEnd('/')}/group/key";

        app.MapGet(path, (HttpContext context) =>
        {
            if (!allowed(context, ServerRole.Viewer)) return Error(403, "This needs the viewer role.");
            var group = key();
            return Results.Json(new JsonObject { ["secret"] = group.Secret, ["held"] = held?.Invoke() ?? group.Held, ["group_has_key"] = group.GroupHasKey });
        });

        app.MapPut(path, async (HttpContext context) =>
        {
            var group = key();
            if (!allowed(context, ServerRole.Admin))
            {
                audit(context, "refused", new { status = 403, reason = "the role is not enough" });
                return Error(403, "This needs the admin role.");
            }

            string? text = null;
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                if (body.Length <= MaxBody && JsonNode.Parse(body) is JsonObject json && json["key"] is JsonValue value && value.TryGetValue<string>(out var given)) text = given;
            }
            catch (JsonException) { }

            if (string.IsNullOrWhiteSpace(text))
            {
                audit(context, "refused", new { status = 400, reason = "no key in the body" });
                return Error(400, "The body is {\"key\": \"...\"}, the group's key as base64.");
            }

            try
            {
                var already = group.Join(text);
                audit(context, "ok", new { already });
                return Results.Json(new JsonObject { ["secret"] = group.Secret, ["already"] = already });
            }
            catch (GroupKeyException ex)
            {
                // a key that is not one, or not the group's: the words name the secret, never the key
                var notAKey = ex.Problem != GroupKeyProblem.WrongKey;
                audit(context, "refused", new { status = notAKey ? 400 : 409, reason = ex.Message });
                return Error(notAKey ? 400 : 409, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                audit(context, "failed", new { status = 503, reason = ex.GetType().Name });
                return Error(503, $"This instance cannot keep the group's key: {ex.Message}");
            }
        });
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
