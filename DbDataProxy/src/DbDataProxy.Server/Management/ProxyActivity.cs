using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;

namespace DbDataProxy.Server.Management;

/// <summary>
/// What the proxy is doing now, through the management API (plan 13, P06): the sessions that are signed in, the queries that are running (and a way to cancel one), the limits of each data source with how many slots are in use,
/// and the login throttle (and a way to let a locked-out client try again). Nothing here is a secret: a session is named by the front of its token's hash, which is not a token, and a statement's text is shown only when the proxy
/// is set to log SQL.
/// </summary>
public sealed class ProxyActivity(SessionStore sessions, ActiveQueries queries, QueryLimits limits, LoginThrottle throttle, Func<ProxyOptions> options, IAuditLog audit, DbDataKit.Monitoring.INotificationFeed? notifications = null, RecentFailures? failures = null, string instanceId = "", string version = "", TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ManagementResult Handle(string method, string[] rest, string? client, string? user, ManagementCaller caller, long? sinceId = null)
    {
        switch (rest)
        {
            case ["overview"] when method == "GET": return ManagementResult.Ok(Overview());
            case ["sessions"] when method == "GET": return ManagementResult.Ok(Sessions());
            case ["sessions", var id] when method == "DELETE":
            {
                var ended = sessions.RevokeById(Uri.UnescapeDataString(id));
                Record(caller, "session revoke", id, ended ? "revoked" : "none");
                return ended ? ManagementResult.Ok(new JsonObject { ["id"] = id, ["revoked"] = true }) : ManagementResult.Problem(404, "There is no such session.");
            }
            case ["queries"] when method == "GET": return ManagementResult.Ok(Queries());
            case ["queries", var id, "cancel"] when method == "POST":
            {
                var cancelled = queries.Cancel(Uri.UnescapeDataString(id));
                Record(caller, "query cancel", id, cancelled ? "cancelled" : "none");
                return cancelled ? ManagementResult.Ok(new JsonObject { ["id"] = id, ["cancelled"] = true }) : ManagementResult.Problem(404, "There is no such running query; it may have ended.");
            }
            case ["limits"] when method == "GET": return ManagementResult.Ok(Limits());
            case ["notifications"] when method == "GET":
                return notifications is null ? ManagementResult.Problem(404, "There is no such call.") : ManagementResult.Ok(Notifications(sinceId));
            case ["throttle"] when method == "DELETE":
            {
                if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(user)) return ManagementResult.Problem(400, "Name the client and the user: ?client=...&user=...");
                var cleared = throttle.Clear(client, user);
                Record(caller, "throttle clear", $"{user}@{client}", cleared ? "cleared" : "none");
                return cleared ? ManagementResult.Ok(new JsonObject { ["cleared"] = true }) : ManagementResult.Problem(404, "The throttle holds nothing against that client and user.");
            }
            default: return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    private JsonObject Notifications(long? sinceId) => new()
    {
        ["notifications"] = new JsonArray([.. notifications!.List(sinceId).Select(n => (JsonNode)new JsonObject
        {
            ["id"] = n.Id, ["kind"] = n.Kind, ["created"] = n.CreatedAtUtc, ["message"] = n.Message,
        })]),
    };

    private readonly DateTimeOffset _started = (clock ?? TimeProvider.System).GetUtcNow();

    /// <summary>Everything a first screen needs in one call: who this is, how long it has been up, and how busy and how troubled it is.</summary>
    private JsonObject Overview()
    {
        var o = options();
        var now = _clock.GetUtcNow();
        var gates = limits.Gates.Values;
        return new JsonObject
        {
            ["product"] = "dbdataproxy", ["version"] = version, ["instance_id"] = instanceId,
            ["started"] = _started, ["uptime_seconds"] = (long)(now - _started).TotalSeconds,
            ["listen"] = $"{o.Listen.Address}:{o.Listen.Port}",
            ["data_sources"] = o.DataSources.Count, ["users"] = o.Users.Count(u => u.AuthType == UserAuthType.Password), ["windows_accounts"] = o.Users.Count(u => u.AuthType == UserAuthType.Windows),
            ["pass_through"] = o.Auth.AllowPassThrough, ["jwt"] = o.Auth.Jwt.Enabled,
            ["sessions"] = sessions.List().Count, ["running_queries"] = queries.List().Count,
            ["waiting_for_a_slot"] = gates.Sum(g => g.Waiting), ["refused_for_want_of_a_slot_total"] = gates.Sum(g => g.Timeouts),
            ["locked_out_clients"] = throttle.List().Count(t => t.LockedUntil is not null),
            ["failures_last_hour"] = failures?.Newest(200).Count(f => now - f.At <= TimeSpan.FromHours(1)) ?? 0,
        };
    }

    private JsonObject Sessions() => new()
    {
        ["sessions"] = new JsonArray([.. sessions.List().Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id, ["user"] = s.User, ["method"] = s.Method, ["data_sources"] = new JsonArray([.. s.DataSources.Select(d => (JsonNode)d)]),
            ["last_used"] = s.LastUsed, ["expires"] = s.ExpiresAt,
        })]),
    };

    private JsonObject Queries() => new()
    {
        ["queries"] = new JsonArray([.. queries.List().Select(q => (JsonNode)new JsonObject
        {
            ["id"] = q.Id, ["user"] = q.User, ["data_source"] = q.DataSource, ["kind"] = q.Kind, ["started"] = q.StartedAt,
            ["running_seconds"] = Math.Round((_clock.GetUtcNow() - q.StartedAt).TotalSeconds, 1), ["sql"] = q.Sql,
        })]),
    };

    private JsonObject Limits()
    {
        var o = options();
        var gates = limits.Gates;
        var sources = new JsonArray();
        foreach (var (name, source) in o.DataSources.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            gates.TryGetValue(name, out var gate);
            sources.Add(new JsonObject
            {
                ["data_source"] = name,
                ["max_concurrent_queries"] = source.MaxConcurrentQueries, ["max_concurrent_queries_per_user"] = source.MaxConcurrentQueriesPerUser, ["acquire_timeout_seconds"] = source.AcquireTimeoutSeconds,
                ["limited"] = source.MaxConcurrentQueries > 0 || source.MaxConcurrentQueriesPerUser > 0,
                ["active"] = gate?.Active ?? 0, ["waiting"] = gate?.Waiting ?? 0, ["entered_total"] = gate?.Entered ?? 0, ["refused_total"] = gate?.Timeouts ?? 0,
            });
        }
        return new JsonObject
        {
            ["data_sources"] = sources,
            ["login_throttle"] = new JsonObject
            {
                ["max_failed_logins"] = o.Auth.MaxFailedLogins, ["lockout_minutes"] = o.Auth.LockoutMinutes,
                ["held"] = new JsonArray([.. throttle.List().Select(t => (JsonNode)new JsonObject { ["client"] = t.Client, ["user"] = t.User, ["failures"] = t.Failures, ["locked_until"] = t.LockedUntil })]),
            },
            ["hosting"] = new JsonObject { ["max_concurrent_requests"] = o.Hosting.MaxConcurrentRequests, ["drain_seconds"] = o.Hosting.DrainSeconds },
        };
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) => audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));
}
