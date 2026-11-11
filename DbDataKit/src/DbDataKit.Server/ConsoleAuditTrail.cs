using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>
/// What the console records in the install's audit (UI05), as the one middleware that sees every call: a sign-in, an invitation made and the person who redeemed it, a person's roles changed, a server paired, confirmed, rejected,
/// linked or forgotten, any other change made through the console's own API, and every <b>write</b> a person relays to a server (who, which server, the method and the path, the status). Reads are not recorded.
/// <para>
/// Never a value: only the method, the path without its query, the status and the names the route carries; the body of a request is not read. An entry is written after the call has been answered, and a failure to write it is logged and
/// never fails the call. The entries have no age limit until an administrator sets one (the <c>audit</c> module's rule). Calls that only look at a thing with a POST (a check, a dry run, diagnostics as you type)
/// are not writes and are not recorded (<see cref="ReadingSegments"/>).
/// </para>
/// </summary>
public static partial class ConsoleAuditTrail
{
    /// <summary>The last segment of a POST that asks a question and changes nothing, so a screen that asks as the person types does not fill the audit.</summary>
    public static readonly IReadOnlySet<string> ReadingSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "check", "diagnostics", "validate", "preview", "sample", "dry-run", "dryrun", "test", "query", "search", "lint", "explain",
    };

    /// <summary>What a call is, as the audit says it: <paramref name="Verb"/> is <c>noun.verb</c>, <paramref name="Target"/> the id the route names.</summary>
    public sealed record Rule(string Verb, string? TargetKind, string? Target, bool FromResponse = false);

    /// <summary>The rule for a call, or null when it is not recorded.</summary>
    public static Rule? Describe(string method, string path)
    {
        path = path.Length > 1 ? path.TrimEnd('/') : path;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return null;

        if (path.StartsWith(ConsoleRelay.Prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path[(ConsoleRelay.Prefix.Length + 1)..].Split('/', 2);
            if (parts.Length < 2 || parts[0].Length == 0) return null;
            if (HttpMethods.IsPost(method) && ReadingSegments.Contains(parts[1].Split('/')[^1])) return null;
            return new Rule("relay.write", "server", Uri.UnescapeDataString(parts[0]));
        }

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        // the state's own maintenance entries are written by the state endpoints (state.maintenance)
        if (path.StartsWith("/api/v1/state", StringComparison.OrdinalIgnoreCase)) return null;

        if (HttpMethods.IsPost(method))
        {
            if (path.Equals("/api/invites", StringComparison.OrdinalIgnoreCase)) return new Rule("invite.create", null, null);
            if (path.Equals("/api/invites/complete-registration", StringComparison.OrdinalIgnoreCase)) return new Rule("invite.redeem", "user", null, FromResponse: true);
            if (path.Equals("/api/auth/passkey/complete", StringComparison.OrdinalIgnoreCase)) return new Rule("signin.passkey", "user", null, FromResponse: true);
            if (path.Equals("/api/auth/windows", StringComparison.OrdinalIgnoreCase)) return new Rule("signin.windows", "user", null, FromResponse: true);
            if (path.Equals("/api/instances", StringComparison.OrdinalIgnoreCase)) return new Rule("server.pair", "server", null, FromResponse: true);
            if (path.Equals("/api/instances/link", StringComparison.OrdinalIgnoreCase)) return new Rule("server.link", null, null);
            if (PendingConfirm().Match(path) is { Success: true } confirm) return new Rule("server.confirm", "server", Uri.UnescapeDataString(confirm.Groups[1].Value));
        }

        // the sign-in ceremonies' first halves, sign-out and the invitation's first half change nothing worth a line
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/invites/begin-registration", StringComparison.OrdinalIgnoreCase)) return null;

        if (HttpMethods.IsPut(method) && UserOf().Match(path) is { Success: true } user) return new Rule("user.update", "user", Uri.UnescapeDataString(user.Groups[1].Value));
        if (HttpMethods.IsDelete(method) && UserCredential().Match(path) is { Success: true } credential) return new Rule("user.credential_remove", "user", Uri.UnescapeDataString(credential.Groups[1].Value));
        if (HttpMethods.IsDelete(method) && PendingServer().Match(path) is { Success: true } reject) return new Rule("server.reject", "server", Uri.UnescapeDataString(reject.Groups[1].Value));
        if (HttpMethods.IsDelete(method) && InstanceOf().Match(path) is { Success: true } forget) return new Rule("server.forget", "server", Uri.UnescapeDataString(forget.Groups[1].Value));

        // any other change made through the console's own API (state connections, a copy of a connection, an upgrade, the runner's settings)
        if (HttpMethods.IsPost(method) && ReadingSegments.Contains(path.Split('/')[^1])) return null;
        return new Rule("console.write", "path", path);
    }

    /// <summary>ok for a call that went through, refused for a refusal of who they are or what they may do, failed for anything else.</summary>
    public static string OutcomeOf(int status) => status switch { < 400 => AuditEvent.Ok, 401 or 403 => AuditEvent.Refused, _ => AuditEvent.Failed };

    /// <summary>Records the calls <see cref="Describe"/> names. Placed after authentication, so the person is known and a refused call is seen.</summary>
    public static IApplicationBuilder UseConsoleAuditTrail(this IApplicationBuilder app, AuthProduct auth) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var rule = Describe(context.Request.Method, path);
            if (rule is null) { await next(); return; }

            var services = context.RequestServices;
            // the name a server had when it was forgotten is read before it goes
            var serverName = rule.TargetKind == "server" && rule.Target is { } id ? services.GetService<InstanceRegistry>()?.Find(id)?.Name : null;

            Stream? original = null;
            MemoryStream? buffer = null;
            if (rule.FromResponse)
            {
                // the answer of these routes is small and says who the person is, or which server it is, which the request does not
                original = context.Response.Body;
                buffer = new MemoryStream();
                context.Response.Body = buffer;
            }
            JsonDocument? answer = null;
            try { await next(); }
            finally
            {
                if (buffer is not null)
                {
                    context.Response.Body = original!;
                    try { answer = buffer.Length == 0 ? null : JsonDocument.Parse(buffer.ToArray()); } catch (JsonException) { /* an answer that is not JSON names nobody */ }
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original!, CancellationToken.None);
                }
                using (answer) Record(context, services, auth, rule, serverName, answer?.RootElement, path);
            }
        });

    private static string? Text(JsonElement? root, string name)
    {
        if (root is not { ValueKind: JsonValueKind.Object } element) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
        return null;
    }

    private static void Record(HttpContext context, IServiceProvider services, AuthProduct auth, Rule rule, string? serverName, JsonElement? answer, string path)
    {
        try
        {
            var status = context.Response.StatusCode;
            var signedIn = context.User.FindFirst(auth.UserIdClaim)?.Value;
            var answeredUser = Text(answer, "userId");
            var actor = signedIn ?? answeredUser;
            // a sign-in or redemption that did not sign anyone in is not a line a person could fill with guesses; a call the policy closed to nobody is the policy's answer, not an act
            if (actor is null && (rule.FromResponse || status == StatusCodes.Status401Unauthorized)) return;
            var target = rule.Target ?? (rule.FromResponse ? (rule.TargetKind == "server" ? Text(answer, "id") : answeredUser) : null);
            var server = rule.Verb == "relay.write" && rule.Target is { } sid ? services.GetService<InstanceRegistry>()?.Find(sid)?.Name : serverName;
            var detail = new Dictionary<string, object?> { ["method"] = context.Request.Method, ["path"] = Truncate(path, 300), ["status"] = status };
            if (server is not null) detail["serverName"] = server;
            if (rule.FromResponse && Text(answer, "displayName") is { } name) detail["name"] = name;
            services.GetRequiredService<AuditLog>().Record(new AuditEvent("person", actor ?? "anonymous", rule.Verb, OutcomeOf(status), ViaKind: "console",
                TargetKind: target is null ? null : rule.TargetKind, TargetId: target is null ? null : Truncate(target, 300), Detail: JsonSerializer.Serialize(detail)));
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("DbDataKit.Server.Audit").LogWarning("The console could not record {Verb} in the audit: {Reason}", rule.Verb, ex.Message);
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    [GeneratedRegex(@"^/api/instances/pending/([^/]+)/confirm$", RegexOptions.IgnoreCase)] private static partial Regex PendingConfirm();
    [GeneratedRegex(@"^/api/instances/pending/([^/]+)$", RegexOptions.IgnoreCase)] private static partial Regex PendingServer();
    [GeneratedRegex(@"^/api/instances/([^/]+)$", RegexOptions.IgnoreCase)] private static partial Regex InstanceOf();
    [GeneratedRegex(@"^/api/users/([^/]+)$", RegexOptions.IgnoreCase)] private static partial Regex UserOf();
    [GeneratedRegex(@"^/api/users/([^/]+)/credentials/[^/]+$", RegexOptions.IgnoreCase)] private static partial Regex UserCredential();
}
