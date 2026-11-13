using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The access rules over the management API (plan 22a GP13, GP15; owner decision of 2026-11-12):
/// <list type="bullet">
/// <item><c>GET /api/v1/access/rules</c> (viewer, administrators see more): the definitions that have rules. A viewer gets names and kinds, so a person can ask about a model; an administrator gets the rules, the state of each rules table, the warnings and the mask key's state.</item>
/// <item><c>GET /api/v1/access/explain?subject=&amp;kind=&amp;model=[&amp;dataset=][&amp;row=column=value...]</c>: why a person or group can or cannot read (<see cref="GatewayAccessInspector"/>). An administrator asks about anyone; any other caller only about themselves, so nobody learns the rules rows
/// of another person: what the answer lists are the rows of the rules tables that name the person asked about and the groups they are in, never the table.</item>
/// <item><c>GET /api/v1/access/coverage?model=[&amp;dataset=]</c> (admin): the values of the filtered datasets that no rule row covers (they are values of the data, so an administrator's).</item>
/// <item><c>GET /api/v1/access/mask-key</c> (admin): whether the group has made its mask key yet. The value is never returned, here or anywhere.</item>
/// <item><c>POST /api/v1/access/mask-key/rotate</c> (admin): replaces the group's key for <c>mask: hash</c> with a new one. This deliberately changes every hash mask (a value masked before no longer matches the same value masked after), so the result cache of
/// this instance is cleared with it (the others' entries are keyed by the definition's version and age out with their time-to-live or the next clear); audited.</item>
/// </list>
/// </summary>
public sealed class GatewayAccessManagement(GroupMaskKey key, GatewayCache cache, IAuditLog? audit = null, TimeProvider? time = null, GatewayAccessInspector? inspector = null)
{
    public const string Route = GatewayManagement.Prefix + "/access/mask-key";
    public const string Prefix = GatewayManagement.Prefix + "/access";
    public const string RulesRoute = Prefix + "/rules";
    public const string ExplainRoute = Prefix + "/explain";
    public const string CoverageRoute = Prefix + "/coverage";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static bool Handles(string route) => route == Route || route.StartsWith(Route + "/", StringComparison.Ordinal) || route is RulesRoute or ExplainRoute or CoverageRoute;

    public ManagementResult Handle(string method, string route, ManagementCaller caller, System.Collections.Specialized.NameValueCollection? query = null)
    {
        switch ((method, route))
        {
            case ("GET", RulesRoute):
                if (inspector is null || !caller.Holds(ServerRole.Viewer)) return inspector is null ? ManagementResult.Problem(404, "There is no such call.") : Denied(ServerRole.Viewer);
                var admin = caller.Holds(ServerRole.Admin);
                var rules = inspector.Rules(full: admin);
                if (admin) rules["mask_key"] = new JsonObject { ["exists"] = key.Exists() };
                return ManagementResult.Ok(rules);
            case ("GET", ExplainRoute):
                return Inspect(caller, query, explain: true);
            case ("GET", CoverageRoute):
                return Inspect(caller, query, explain: false);
            case (_, RulesRoute or ExplainRoute or CoverageRoute):
                return ManagementResult.Problem(405, "This call is not available with that method.");
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

    private ManagementResult Inspect(ManagementCaller caller, System.Collections.Specialized.NameValueCollection? query, bool explain)
    {
        if (inspector is null) return ManagementResult.Problem(404, "There is no such call.");
        if (!caller.Holds(ServerRole.Viewer)) return Denied(ServerRole.Viewer);
        query ??= [];
        var model = query["model"];
        if (string.IsNullOrWhiteSpace(model)) return ManagementResult.Problem(400, "Say which model: `model=<name>`.");
        var dataset = string.IsNullOrWhiteSpace(query["dataset"]) ? null : query["dataset"];
        var admin = caller.Holds(ServerRole.Admin);
        try
        {
            if (!explain)
            {
                if (!admin) return Denied(ServerRole.Admin);
                var coverage = inspector.Coverage(model, dataset);
                Audit(caller, "access coverage", $"{model}{(dataset is null ? "" : "/" + dataset)}");
                return ManagementResult.Ok(coverage);
            }

            var subject = query["subject"];
            if (string.IsNullOrWhiteSpace(subject)) return ManagementResult.Problem(400, "Say whose access to explain: `subject=<person or group>`.");
            var kind = string.IsNullOrWhiteSpace(query["kind"]) ? null : query["kind"];
            if (!admin)
            {
                // a person asks about themselves, and only themselves: the console's token names them; the explanation is made for the person it names
                var token = caller.Token;
                if (token is null || kind == "group" || !GatewayAccessInspector.IsPerson(subject, token.SubjectId, token.SubjectName))
                    return ManagementResult.Problem(403, "Only an administrator can explain the access of someone else; ask about yourself.");
                subject = token.SubjectId;
                kind = "person";
            }

            var row = GatewayAccessInspector.ParseRow(query.GetValues("row") ?? []);
            var explanation = inspector.Explain(subject, kind, model, dataset, row);
            if (admin) Audit(caller, "access explain", $"{subject} on {model}{(dataset is null ? "" : "/" + dataset)}");
            return ManagementResult.Ok(explanation);
        }
        catch (AccessInspectorException ex) { return ManagementResult.Problem(ex.Status, ex.Message); }
    }

    private void Audit(ManagementCaller caller, string command, string target) => audit?.Append(caller.Audit(command, target, "read", _time.GetUtcNow()));

    private static ManagementResult Denied(ServerRole role = ServerRole.Admin) => ManagementResult.Problem(403, $"This call needs the {role.ToString().ToLowerInvariant()} role.");
}
