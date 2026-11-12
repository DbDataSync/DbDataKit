using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Runner.Connections;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The connections the console hands a runner (plan 27a IX23, <c>docs/channel-bindings.md</c> "The runner: what it keeps and how the console hands it a connection"): the Runner is a separate server and never reads the console's <c>connections/</c> folder,
/// so when an administrator binds a runner slot to a console connection (or sends it again) the console puts the connection's <b>definition with its secret references, never a value</b> here, and the runner keeps its own copy (<c>connection_copy</c>).
/// <list type="bullet">
/// <item><c>GET {prefix}/connection-copies</c> (viewer): every copy with its <c>version</c> and its <c>definition</c> (the text with secret references, never a value; the console compares and adopts it, IX25) (the hash both sides compute, <see cref="ConnectionCopyVersion"/>), kind, who sent it and when, which package slots use it, whether a file of the runner's own directory
/// shadows it, and for each secret reference whether it <b>resolves on this runner</b> (never a value).</item>
/// <item><c>PUT {prefix}/connection-copies/{name}</c> (administrator), body <c>{"definition": "&lt;connection yaml&gt;"}</c>: validated by the shared strict loader (a value written as itself is refused, without being echoed), kept, audited with the name and version only.</item>
/// <item><c>DELETE {prefix}/connection-copies/{name}</c> (administrator): removes the copy; a slot bound to the name then fails naming it.</item>
/// </list>
/// </summary>
public static class RunnerConnectionCopyApi
{
    public const string PutVerb = RunnerConnectionService.PutVerb;
    public const string DeleteVerb = RunnerConnectionService.DeleteVerb;

    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerHost?> host, string prefix, RunnerCallerResolver callerOf, string serviceId = RunnerNames.ServiceId)
    {
        var root = prefix.TrimEnd('/');
        var service = new RunnerConnectionService(state, serviceId);
        var publisher = new DefinitionPublisher(state, RunnerPackages.Kinds(), new PayloadStore(state), "runner");

        app.MapGet($"{root}/connection-copies", async (HttpContext context) =>
        {
            if (!callerOf(context).Authorize(ServerRole.Viewer).Allowed) return Error(403, "This needs the viewer role.");
            var uses = UsesOf(publisher);
            var items = new JsonArray();
            foreach (var copy in service.Copies.List())
                items.Add(await ViewAsync(copy, host()?.Resolver, host()?.ConnectionsDirectory, uses, context.RequestAborted));
            return Results.Json(new JsonObject { ["copies"] = items });
        });

        app.MapPut($"{root}/connection-copies/{{name}}", async (string name, HttpContext context) =>
        {
            var caller = callerOf(context);
            if (!caller.Authorize(ServerRole.Admin).Allowed)
            {
                service.RefuseRole(PutVerb, caller.Actor, name);
                return Error(403, "This needs the admin role.");
            }

            JsonObject? body;
            try { body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { body = null; }
            var definition = body?["definition"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
            var result = service.Put(name, definition, caller.Actor);
            if (!result.Ok) return Results.Json(new { error = result.Error, diagnostics = result.Diagnostics }, statusCode: result.Status);
            return Results.Json(await ViewAsync(result.Copy!, host()?.Resolver, host()?.ConnectionsDirectory, UsesOf(publisher), context.RequestAborted));
        });

        app.MapDelete($"{root}/connection-copies/{{name}}", (string name, HttpContext context) =>
        {
            var caller = callerOf(context);
            if (!caller.Authorize(ServerRole.Admin).Allowed)
            {
                service.RefuseRole(DeleteVerb, caller.Actor, name);
                return Error(403, "This needs the admin role.");
            }

            return service.Delete(name, caller.Actor) ? Results.Json(new { name, removed = true }) : Error(404, $"This runner keeps no copy of a connection '{name}'.");
        });
    }

    private static Dictionary<string, List<JsonNode>> UsesOf(DefinitionPublisher publisher)
    {
        var uses = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        foreach (var b in publisher.BindingsOfKind(RunnerPackages.Kind))
        {
            if (!uses.TryGetValue(b.BoundTo, out var list)) uses[b.BoundTo] = list = [];
            list.Add(new JsonObject { ["package"] = b.Name, ["channel"] = b.Channel, ["slot"] = b.Slot });
        }

        return uses;
    }

    /// <summary>One copy as the screen reads it: its version, who sent it, which slots use it and whether every secret reference of it resolves <b>on this runner</b>.</summary>
    public static async Task<JsonObject> ViewAsync(ConnectionCopy copy, RunnerSlotResolver? resolver, string? directory, IReadOnlyDictionary<string, List<JsonNode>> uses, CancellationToken ct)
    {
        var secrets = new JsonArray();
        string? problem = null;
        var load = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(copy.Definition, $"the copy of the connection '{copy.Name}'");
        if (!load.Ok) problem = "The copy does not load: send it again from the console.";
        else if (resolver is not null)
            foreach (var check in await resolver.CheckReferencesAsync(load.Definition!, ct))
                secrets.Add(new JsonObject { ["path"] = check.Path, ["reference"] = check.Reference, ["resolves"] = check.Resolves, ["problem"] = check.Problem });
        // a file of the runner's own directory answers to the name first: the copy is then not what runs
        var shadowed = directory is not null && new[] { ".yaml", ".yml" }.Any(e => File.Exists(Path.Combine(directory, copy.Name + e)));
        return new JsonObject
        {
            ["name"] = copy.Name, ["version"] = copy.Version, ["kind"] = copy.Kind, ["sent_by"] = copy.SentBy, ["sent_at"] = copy.SentAt, ["problem"] = problem, ["definition"] = copy.Definition,
            ["shadowed_by_file"] = shadowed, ["secrets"] = secrets, ["used_by"] = new JsonArray([.. uses.GetValueOrDefault(copy.Name) ?? []]),
            ["unresolved"] = secrets.Count(s => !(bool)s!["resolves"]!),
        };
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
