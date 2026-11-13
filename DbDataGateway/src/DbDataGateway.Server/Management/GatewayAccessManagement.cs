using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The access rules' key, over the management API (plan 22a GP13, owner decision of 2026-11-12):
/// <list type="bullet">
/// <item><c>GET /api/v1/access/mask-key</c> (admin): whether the group has made its mask key yet. The value is never returned, here or anywhere.</item>
/// <item><c>POST /api/v1/access/mask-key/rotate</c> (admin): replaces the group's key for <c>mask: hash</c> with a new one. This deliberately changes every hash mask (a value masked before no longer matches the same value masked after), so the result cache of
/// this instance is cleared with it (the others' entries are keyed by the definition's version and age out with their time-to-live or the next clear); audited.</item>
/// </list>
/// </summary>
public sealed class GatewayAccessManagement(GroupMaskKey key, GatewayCache cache, IAuditLog? audit = null, TimeProvider? time = null)
{
    public const string Route = GatewayManagement.Prefix + "/access/mask-key";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static bool Handles(string route) => route == Route || route.StartsWith(Route + "/", StringComparison.Ordinal);

    public ManagementResult Handle(string method, string route, ManagementCaller caller)
    {
        switch ((method, route))
        {
            case ("GET", Route):
                return caller.Holds(ServerRole.Admin) ? ManagementResult.Ok(new JsonObject { ["exists"] = key.Exists() }) : Denied();
            case ("POST", Route + "/rotate"):
                if (!caller.Holds(ServerRole.Admin)) return Denied();
                key.Rotate();
                var cleared = cache.Invalidator.ClearByHand(null, caller.Token?.SubjectName ?? "local");
                audit?.Append(caller.Audit("access mask-key rotate", GroupMaskKey.SecretName, $"the key for hash masks was replaced; {cleared} cache entries cleared", _time.GetUtcNow()));
                return ManagementResult.Ok(new JsonObject { ["rotated"] = true, ["cleared"] = cleared });
            case (_, Route or Route + "/rotate"):
                return ManagementResult.Problem(405, "This call is not available with that method.");
            default:
                return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    private static ManagementResult Denied() => ManagementResult.Problem(403, "This call needs the admin role.");
}
