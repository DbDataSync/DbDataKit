using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataKit.Views;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The calls that are the same for every serving kind (plan 30 PB02 and PB05), on the management API:
/// <code>
/// GET  /api/v1/definitions/{kind}/{name}          the versions of a definition and what each channel serves (active, previous, staged)      (viewer)
/// POST /api/v1/definitions/{kind}/{name}/prune    {keep: n | versions: [..], dry_run?: true}: removes versions, or says what it would       (administrator)
/// </code>
/// A prune never removes a version a channel serves, has staged or served before, nor one another version pins; without <c>dry_run: false</c> it is a dry run. Kinds: the serving kinds of <see cref="ServingKinds"/> and the views'
/// <c>dashboard</c> and <c>report</c>.
/// </summary>
public sealed class GatewayDefinitionsManagement(StateDatabase state, IAuditLog? audit = null, TimeProvider? clock = null)
{
    public const string Prefix = GatewayManagement.Prefix + "/definitions/";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DefinitionPublisher _publisher = new(state, null, null, "serving");
    private readonly DefinitionStore _store = new(state);

    public static bool Handles(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);

    private static IReadOnlyList<string> Kinds => [.. ServingKinds.All.All.Select(k => k.Name), .. ViewDefinitions.Kinds];

    private static ManagementResult Problem(int status, string code, string message) =>
        new(status, new JsonObject { ["error"] = message, ["code"] = code, ["message"] = message });

    public ManagementResult Handle(string method, string route, string? body, ManagementCaller caller)
    {
        var parts = route[Prefix.Length..].Split('/');
        if (parts.Length is < 2 or > 3) return Problem(404, "not_found", "There is no such call.");
        var kind = Uri.UnescapeDataString(parts[0]);
        var name = Uri.UnescapeDataString(parts[1]);
        if (!Kinds.Contains(kind)) return Problem(404, "not_found", $"There is no kind '{kind}'; the kinds are {string.Join(", ", Kinds.Order(StringComparer.Ordinal))}.");
        if (parts.Length == 2) return method == "GET" ? Describe(kind, name, caller) : Problem(405, "invalid", "A definition is read with GET.");
        if (parts[2] != "prune") return Problem(404, "not_found", "There is no such call.");
        return method == "POST" ? Prune(kind, name, body, caller) : Problem(405, "invalid", "Prune is a POST.");
    }

    private ManagementResult Describe(string kind, string name, ManagementCaller caller)
    {
        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "denied", "This needs the viewer role.");
        var versions = _store.Versions(kind, name);
        if (versions.Count == 0) return Problem(404, "not_found", $"There is no {kind} '{name}'.");
        var heads = new JsonArray();
        foreach (var head in _publisher.HeadsOf(kind, name))
            heads.Add(new JsonObject { ["channel"] = head.Channel, ["active"] = head.Active, ["previous"] = head.Previous, ["staged"] = head.Staged });
        return new ManagementResult(200, new JsonObject
        {
            ["kind"] = kind, ["name"] = name, ["channels"] = heads,
            ["versions"] = new JsonArray([.. versions.OrderByDescending(v => v.Version).Select(v => (JsonNode)new JsonObject
            {
                ["version"] = v.Version, ["published_by"] = v.PublishedBy, ["published_at"] = v.PublishedAt.UtcDateTime.ToString("O"), ["note"] = v.Note, ["format"] = v.Format,
                ["depends_on"] = new JsonArray([.. v.DependsOn.Select(d => (JsonNode)JsonValue.Create(d)!)]),
            })]),
        });
    }

    private string Who(ManagementCaller caller) { var entry = caller.Audit("", null, "", _clock.GetUtcNow()); return entry.UserName ?? entry.UserId; }

    private ManagementResult Prune(string kind, string name, string? body, ManagementCaller caller)
    {
        if (!caller.Holds(ServerRole.Admin)) return Problem(403, "denied", "Pruning versions needs the administrator role.");
        JsonObject json;
        try { json = string.IsNullOrWhiteSpace(body) ? [] : JsonNode.Parse(body) as JsonObject ?? throw new FormatException(); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { return Problem(400, "invalid", "The body is a JSON object: {\"keep\": 5, \"dry_run\": true}."); }
        int? keep = null;
        if (json["keep"] is { } k)
        {
            if (k is not JsonValue kv || !kv.TryGetValue<int>(out var n) || n < 0) return Problem(400, "invalid", "keep is a number of versions, 0 or more.");
            keep = n;
        }
        List<int>? versions = null;
        if (json["versions"] is { } list)
        {
            if (list is not JsonArray array) return Problem(400, "invalid", "versions is a list of version numbers.");
            versions = [];
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<int>(out var number) && number > 0) versions.Add(number);
                else return Problem(400, "invalid", "versions is a list of version numbers.");
            }
        }
        if ((keep is null) == (versions is null)) return Problem(400, "invalid", "Say either keep (how many of the newest versions stay) or versions (which to remove). A call that does not say dry_run: false is a dry run.");
        var dryRun = json["dry_run"] is JsonValue flag && flag.TryGetValue<bool>(out var asked) ? asked : true;
        try
        {
            var plan = _publisher.Prune(kind, name, Who(caller), keep, versions, dryRun);
            if (!dryRun) audit?.Append(caller.Audit($"{kind} prune", name, $"removed {(plan.Remove.Count == 0 ? "none" : string.Join(", ", plan.Remove))}", _clock.GetUtcNow()));
            return new ManagementResult(200, new JsonObject
            {
                ["kind"] = kind, ["name"] = name, ["dry_run"] = !plan.Done,
                ["remove"] = new JsonArray([.. plan.Remove.Select(v => (JsonNode)JsonValue.Create(v)!)]),
                ["retained"] = new JsonArray([.. plan.Retained.Select(r => (JsonNode)new JsonObject { ["version"] = r.Version, ["reason"] = r.Reason })]),
                ["files"] = new JsonArray([.. plan.Artifacts.Select(a => (JsonNode)JsonValue.Create(a)!)]),
            });
        }
        catch (ArgumentException ex) { return Problem(400, "invalid", ex.Message); }
        catch (DefinitionNotFoundException ex) { return Problem(404, "not_found", ex.Message); }
    }
}
