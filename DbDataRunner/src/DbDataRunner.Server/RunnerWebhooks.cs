using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Runner;
using DbDataKit.Runner.Triggers;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DbDataRunner.Server;

/// <summary>
/// The runner's webhook endpoint (plan 28a R13): <c>POST /api/v1/webhooks/{package}/{channel}/{source}</c>. It is the runner's end of a webhook; the public end is the serving layer (plan 28 section 7), which checks the
/// signature itself and calls this with its own credential, or a sender may call it directly with a shared secret. Two ways in:
/// <list type="bullet">
/// <item><b>A client</b> with a management token whose roles include operator (or administrator) or publisher: the caller is trusted to have checked the signature (the serving layer's call), and the body is delivered as verified.</item>
/// <item><b>A signature</b>: no token, and an <c>X-Signature: sha256=&lt;hex&gt;</c> header, the HMAC-SHA256 of the body with the secret the task's webhook trigger names (<c>secret:</c> in the manifest, looked up in the runner's secret store, for
/// example the variable <c>DBDATA_SECRET_SHOP_HOOK</c> for <c>shop_hook</c>). A signature that does not hold is answered <b>401</b>, and the body is kept as an inbox record with <c>signature_ok</c> false that no run can take.</item>
/// </list>
/// The body (up to 1 MiB) becomes an inbox item of every task that has a webhook trigger for the source, and the task is queued once (<see cref="WebhookReceiver"/>): 202 with the items. 404 when no task listens for the source.
/// Every receipt is in the audit log (<c>runner.webhook.receive</c>: who, which source, the signature result, the tasks, the size) and <b>never the body</b>.
/// <para>
/// It is mapped before the token check (<see cref="ManagementAuthentication.UseManagementAuthentication"/>) because the signed way has no token; it checks a token itself when one is sent.
/// </para>
/// </summary>
public static partial class RunnerWebhooks
{
    public const string ReceiveVerb = "runner.webhook.receive";
    public const string SignatureHeader = "X-Signature";
    public const int MaxBodyBytes = 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceRule();

    /// <summary>Maps the endpoint. Call it before <c>UseManagementAuthentication</c>.</summary>
    public static void Map(IApplicationBuilder app, StateDatabase state, Func<RunnerEngine> engine, ConsoleTokenValidator tokens, ISecretResolver secrets) =>
        app.Use(async (context, next) =>
        {
            var parts = context.Request.Path.Value?.Split('/');
            if (parts is not ["", "api", "v1", "webhooks", var package, var channel, var source])
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "POST";
                return;
            }

            await Receive(context, state, engine(), tokens, secrets, package, channel, source).ConfigureAwait(false);
        });

    private static async Task Receive(HttpContext context, StateDatabase state, RunnerEngine engine, ConsoleTokenValidator tokens, ISecretResolver secrets, string package, string channel, string source)
    {
        if (TaskRef.Problem(package, channel, "task") is { } nameProblem) { await Json(context, StatusCodes.Status400BadRequest, new { error = nameProblem }); return; }
        if (!SourceRule().IsMatch(source)) { await Json(context, StatusCodes.Status400BadRequest, new { error = $"'{source}' is not a webhook source name: letters, digits, '.', '_' and '-', up to 128." }); return; }

        var audit = new AuditLog(state, RunnerState.ServiceId);
        var target = $"{package}/{channel}/{source}";
        var tokenHeader = context.Request.Headers[ManagementAuthentication.TokenHeader].FirstOrDefault();
        ValidatedToken? token = null;
        if (!string.IsNullOrEmpty(tokenHeader))
        {
            var result = tokens.Validate(tokenHeader);
            if (!result.Ok)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;      // no reason given, as everywhere on the management API
                return;
            }

            token = result.Token;
            var roles = ManagementAuthorization.Effective(token!);
            if (token!.Scope is not null || (!ManagementAuthorization.Authorize(token, ServerRole.Operator).Allowed && !roles.HasFlag(ServerRole.Publisher)))
            {
                Record(audit, token, source, target, AuditEvent.Refused, new { reason = "the role is not enough: a webhook is delivered by an operator, an administrator or a publisher" });
                await Json(context, StatusCodes.Status403Forbidden, new { error = "This needs the operator or publisher role." });
                return;
            }
        }

        if (context.Request.ContentLength is > MaxBodyBytes) { await Json(context, StatusCodes.Status413PayloadTooLarge, new { error = $"The body is larger than {MaxBodyBytes} bytes." }); return; }
        var body = await ReadBody(context.Request.Body, context.RequestAborted).ConfigureAwait(false);
        if (body is null) { await Json(context, StatusCodes.Status413PayloadTooLarge, new { error = $"The body is larger than {MaxBodyBytes} bytes." }); return; }

        var receiver = new WebhookReceiver(state, engine.Control?.Triggers);
        var subscribers = receiver.Subscribers(package, channel, source);
        if (subscribers.Count == 0)
        {
            await Json(context, StatusCodes.Status404NotFound, new { error = $"No task of {package} on channel '{channel}' has a webhook trigger for the source '{source}'." });
            return;
        }

        // who verified the body: a trusted client says it did; otherwise each task's own secret does
        Func<TaskRef, bool> signatureOk;
        string signature;
        if (token is not null)
        {
            signatureOk = _ => true;
            signature = "trusted client";
        }
        else
        {
            var header = context.Request.Headers[SignatureHeader].FirstOrDefault();
            var declared = DeclaredSecrets(state, subscribers, source);
            if (string.IsNullOrEmpty(header) || declared.Values.All(s => s is null))
            {
                // nothing to check a signature against, or none sent: this is not a signed webhook, and it is not kept
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var verdicts = new Dictionary<TaskRef, bool>();
            foreach (var (task, secretName) in declared) verdicts[task] = secretName is not null && await Verify(secrets, secretName, header, body, context.RequestAborted).ConfigureAwait(false);
            signatureOk = task => verdicts.GetValueOrDefault(task);
            signature = verdicts.Values.Any(v => v) ? "ok" : "invalid";
        }

        var receipt = await receiver.ReceiveAsync(package, channel, source, signatureOk, body, context.RequestAborted).ConfigureAwait(false);
        var accepted = receipt.Deliveries.Count(d => d.SignatureOk);
        Record(audit, token, source, target, accepted > 0 ? AuditEvent.Ok : AuditEvent.Refused,
            new { signature, bytes = body.Length, tasks = receipt.Deliveries.Select(d => d.Task.Task).ToArray(), accepted, refused = receipt.Deliveries.Count - accepted });

        if (accepted == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await Json(context, StatusCodes.Status202Accepted, new JsonObject
        {
            ["package"] = package,
            ["channel"] = channel,
            ["source"] = source,
            ["signature"] = signature,
            ["deliveries"] = new JsonArray(receipt.Deliveries.Select(d => (JsonNode)new JsonObject
            {
                ["task"] = d.Task.Task, ["item_id"] = d.ItemId.ToString(), ["signature_ok"] = d.SignatureOk, ["queued"] = d.Queued,
            }).ToArray()),
        });
    }

    /// <summary>For each subscribing task, the name of the secret its webhook trigger for the source names, or null when it names none.</summary>
    private static Dictionary<TaskRef, string?> DeclaredSecrets(StateDatabase state, IReadOnlyList<TaskRef> subscribers, string source)
    {
        var projections = new TaskProjections(state);
        var declared = new Dictionary<TaskRef, string?>();
        foreach (var task in subscribers)
        {
            string? secret = null;
            if (projections.Get(task.Package, task.Channel, task.Task) is { } row)
                secret = DbDataKit.Runner.Scheduler.ProjectionDocuments.ParseTriggers(row.Triggers).FirstOrDefault(t => t.Kind == TriggerKind.Webhook && t.Source == source && t.Secret is not null)?.Secret;
            declared[task] = secret;
        }

        return declared;
    }

    /// <summary>True when the header is <c>sha256=</c> and the HMAC-SHA256 of the body under the named secret (compared in constant time). A secret that cannot be found is no match, and not an error to the sender.</summary>
    private static async Task<bool> Verify(ISecretResolver secrets, string secretName, string header, byte[] body, CancellationToken ct)
    {
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given;
        try { given = Convert.FromHexString(header.AsSpan(prefix.Length).Trim()); }
        catch (FormatException) { return false; }

        SecretValue value;
        try { value = await secrets.ResolveAsync(SecretRef.LooksLikeRef(secretName) ? SecretRef.Parse(secretName) : new SecretRef(SecretRefKind.Secret, secretName), ct).ConfigureAwait(false); }
        catch (SecretNotFoundException) { return false; }

        using (value)
        {
            var key = Encoding.UTF8.GetBytes(value.RevealString());
            try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), given); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    /// <summary>The body, or null when it is longer than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBody(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void Record(AuditLog audit, ValidatedToken? token, string source, string target, string outcome, object detail) =>
        audit.Record(token is null
            ? new AuditEvent("webhook", $"webhook:{source}", ReceiveVerb, outcome, ViaKind: "api", TargetKind: "webhook", TargetId: target, Detail: JsonSerializer.Serialize(detail))
            : new AuditEvent("person", token.SubjectId, ReceiveVerb, outcome, ViaKind: token.Client.Kind == ClientKind.Console ? "console" : "api", ViaId: token.Client.Id, TargetKind: "webhook", TargetId: target, Detail: JsonSerializer.Serialize(detail)));

    private static Task Json(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body), context.RequestAborted);
    }
}
