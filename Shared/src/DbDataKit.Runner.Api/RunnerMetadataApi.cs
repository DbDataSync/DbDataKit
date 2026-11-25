using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// <c>POST {prefix}/metadata/{channel}/{task}</c> with <c>{"package": "crm-sync", "slot": "crm", "inputs": {...}, "on_behalf_of": "Ada"}</c>: starts a task of the built-in package <c>dbdatakit-metadata</c> (<c>discover</c> or <c>describe</c>) that
/// reads the database the channel binds to the package's slot, for the person named. Who may: a console, for a person it signed in (an operator, as starting any task); a call that names no person, and a client that is not a console, are
/// refused. Any channel the runner serves, production included: the screen that asks warns first.
/// <list type="bullet">
/// <item>The package must declare the slot in the channel (in the version it serves or the one staged there), and the channel must bind it: 404 and 409 otherwise, naming what is missing. A package this runner holds no version of yet
/// (a draft that was never published, whose slot was bound before its first version) is read through whatever slot the channel binds for it.</item>
/// <item>The login is the connection's read login when it has one, else its default one (<c>role</c> in the run's inputs); the slot is resolved through the package's binding when the run starts, so the production-binding rule holds.</item>
/// <item>The built-in package is installed in the channel from the files beside the program (<see cref="BuiltInPackages"/>) the first time, and again when this runner's files differ from what the channel serves.</item>
/// <item>The answer is 202 with the run's id; the result is the run's step named after the task, keyed by the run id (<c>GET .../tasks/dbdatakit-metadata/{channel}/{task}/steps/{task}?key={run id}</c>, then its payload for <c>discover</c>).</item>
/// </list>
/// A start is audited as any run is (<c>runner.task.run</c>, with the person); a refusal is audited as <c>runner.metadata.read</c>, never with the query or another input's value.
/// <para>
/// <c>GET {prefix}/metadata/channels?package=crm-sync</c> (a viewer): what a screen offers before a read of a package's slot, whether or not this runner holds a version of the package: the channels this runner serves, the bindings it holds
/// for the package in each, and whether it holds a version (<c>{"package", "holds_version", "channels": ["dev", "prod"], "bindings": [{"channel", "slot", "bound_to"}]}</c>).
/// </para>
/// Design: docs/authoring-metadata.md#the-built-in-package-dbdatakit-metadata
/// </summary>
public static class RunnerMetadataApi
{
    public const string Verb = "runner.metadata.read";

    /// <summary>The inputs each task takes from the caller, besides the package, the slot and the role the route sets.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> TaskInputs = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [MetadataPackage.Discover] = ["schemas", "pattern", "table"],
        [MetadataPackage.Describe] = ["sql"],
    };

    /// <param name="builtinDirectory">The folder that holds the built-in packages' files (<c>builtin/</c> beside the program).</param>
    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerHost> engine, string prefix, RunnerCallerResolver callerOf, string builtinDirectory, string serviceId = RunnerNames.ServiceId)
    {
        var root = prefix.TrimEnd('/');
        var audit = new AuditLog(state, serviceId);
        var installs = new BuiltInInstaller(state, Path.Combine(builtinDirectory, MetadataPackage.Name));

        app.MapGet($"{root}/metadata/channels", (HttpContext context) =>
        {
            if (!callerOf(context).Authorize(ServerRole.Viewer).Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            var package = context.Request.Query["package"].ToString();
            if (!PackageManifestValidator.NameRule.IsMatch(package)) return Results.Json(new { error = "Name the package: `package` (lower-case letters, digits, - and _)." }, statusCode: StatusCodes.Status400BadRequest);
            var publisher = new RunnerPublishing(state).Publisher;
            return Results.Json(new JsonObject
            {
                ["package"] = package,
                ["holds_version"] = new DefinitionStore(state).Versions(RunnerPublishing.Kind, package).Count > 0,
                ["channels"] = new JsonArray([.. engine().Channels.Order(StringComparer.Ordinal).Select(c => (JsonNode)JsonValue.Create(c))]),
                ["bindings"] = new JsonArray([.. publisher.BindingsOf(RunnerPublishing.Kind, package).Select(b => (JsonNode)new JsonObject { ["channel"] = b.Channel, ["slot"] = b.Slot, ["bound_to"] = b.BoundTo })]),
            });
        });

        app.MapPost($"{root}/metadata/{{channel}}/{{task}}", async (string channel, string task, HttpContext context) =>
        {
            var caller = callerOf(context);
            IResult Refuse(int status, string message, string? package = null, string? slot = null)
            {
                audit.Record(new AuditEvent("person", caller.Actor.Id, Verb, AuditEvent.Refused, ViaKind: caller.Actor.ViaKind, ViaId: caller.Actor.ViaId, TargetKind: "task",
                    TargetId: $"{MetadataPackage.Name}/{Cut(channel)}/{Cut(task)}", Detail: JsonSerializer.Serialize(new { status, package, slot, reason = message })));
                return Results.Json(new { error = message }, statusCode: status);
            }

            var decision = caller.Authorize(ServerRole.Operator);
            if (!decision.Allowed) return Refuse(403, "Reading a database's shape starts a task, so it needs the operator role.");
            if (caller.Token is not { Client.Kind: ClientKind.Console, IsConsole: false }) return Refuse(403, "A read of a database's shape is asked by a console, for a person it signed in.");
            if (!MetadataPackage.IsTask(task)) return Refuse(404, $"The built-in package has the tasks {string.Join(" and ", MetadataPackage.Tasks)}, not '{task}'.");
            if (!PackageManifestValidator.NameRule.IsMatch(channel) || !engine().Channels.Contains(channel)) return Refuse(404, $"This runner does not serve the channel '{channel}'.");

            JsonObject? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject; }
            catch (JsonException) { body = null; }
            if (body is null) return Refuse(400, "The body is {\"package\": ..., \"slot\": ..., \"inputs\": {...}, \"on_behalf_of\": ...}.");
            var package = Text(body, "package");
            var slot = Text(body, "slot");
            var person = Text(body, "on_behalf_of").Trim();
            if (!PackageManifestValidator.NameRule.IsMatch(package) || !PackageManifestValidator.NameRule.IsMatch(slot)) return Refuse(400, "Name the package and its slot: `package` and `slot`.");
            if (person.Length == 0) return Refuse(400, "Name the person the read is for: `on_behalf_of`. A read is recorded as the person who asked.", package, slot);
            if (person.Length > RunnerControl.MaxOnBehalfOf) return Refuse(400, $"on_behalf_of is the name of a person, text of at most {RunnerControl.MaxOnBehalfOf} characters.", package, slot);

            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (body["inputs"] is { } given)
            {
                if (given is not JsonObject map) return Refuse(400, "inputs is an object of names and values.", package, slot);
                foreach (var (name, value) in map)
                {
                    if (!TaskInputs[task].Contains(name)) return Refuse(400, $"The task {task} takes the inputs {string.Join(", ", TaskInputs[task])}, not '{name}'.", package, slot);
                    inputs[name] = value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";
                }
            }

            if (task == MetadataPackage.Describe && !inputs.ContainsKey("sql")) return Refuse(400, "Give the query to describe: the input `sql`.", package, slot);

            var publishing = new RunnerPublishing(state);
            if (!DeclaresSlot(state, publishing, package, channel, slot))
                return Refuse(404, $"The package {package} does not declare the slot '{slot}' in channel '{channel}' (neither the version it serves there nor the one staged).", package, slot);
            var bound = publishing.Publisher.Bindings(channel, RunnerPublishing.Kind, package).FirstOrDefault(b => b.Slot == slot)?.BoundTo;
            if (bound is null)
                return Refuse(409, $"The slot '{slot}' of {package} has no binding in channel '{channel}'. An administrator binds it on this runner's Packages page (Bindings) to a connection this runner has, then the read can be asked again.", package, slot);

            if (engine().Control is not { } control) return Results.Json(new { error = "The runner is starting; try again in a moment." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            try { installs.Ensure(channel, publishing); }
            catch (BuiltInMissingException ex) { return Refuse(503, ex.Message, package, slot); }
            catch (RunnerPublishException ex) { return Refuse(ex.Status == 403 ? 409 : ex.Status, $"The built-in package could not be put in channel '{channel}': {ex.Message}", package, slot); }

            inputs[MetadataPackage.PackageInput] = package;
            inputs[MetadataPackage.SlotInput] = slot;
            inputs[MetadataPackage.RoleInput] = RoleOf(engine().Resolver?.Known(bound));
            var reference = new TaskRef(MetadataPackage.Name, channel, task);
            RunNowResult queued;
            try { queued = control.RunNow(reference, inputs, caller.Actor, onBehalfOf: person); }
            catch (UnknownTaskException ex) { return Refuse(503, $"The built-in package is not served in channel '{channel}' yet: {ex.Message}", package, slot); }
            return queued.Queued
                ? Results.Json(new JsonObject
                {
                    ["run_id"] = queued.RunId!.Value.ToString("D"), ["task"] = reference.ToString(), ["package"] = package, ["slot"] = slot, ["channel"] = channel, ["step"] = task, ["key"] = queued.RunId!.Value.ToString("D"),
                }, statusCode: StatusCodes.Status202Accepted)
                : Results.Json(new { error = $"{reference} could not be queued." }, statusCode: StatusCodes.Status409Conflict);
        });
    }

    /// <summary>The login a read asks for: <c>read</c> (the connection's read login, else its default one, which the runner gives for a read request), or <c>default</c> for a connection with no login to give at all.</summary>
    internal static string RoleOf(ConnectionDefinition? connection) =>
        connection is not null && connection.LoginFor(LoginRole.Read) is null && connection.ConnectionStringSecret is null ? "default" : "read";

    /// <summary>
    /// Whether the version the channel serves, or the one staged there, declares the slot. A package this runner holds no version of declares nothing yet: a draft's slot is bound before its first version, and the binding alone says what
    /// is read, so any slot passes here and the binding check that follows decides.
    /// </summary>
    private static bool DeclaresSlot(StateDatabase state, RunnerPublishing publishing, string package, string channel, string slot)
    {
        var store = new DefinitionStore(state);
        if (store.Versions(RunnerPublishing.Kind, package).Count == 0) return true;
        foreach (var head in publishing.Publisher.HeadsOf(RunnerPublishing.Kind, package).Where(h => h.Channel == channel))
            foreach (var version in new[] { head.Active, head.Staged }.OfType<int>())
                if (store.Get(RunnerPublishing.Kind, package, version) is { } stored
                    && PackageManifestReader.Load(stored.Content, $"{package}@{version}") is { Ok: true, Manifest: { } manifest }
                    && manifest.Tasks.Any(t => t.Connections.Any(c => c.Name == slot)))
                    return true;
        return false;
    }

    private static string Text(JsonObject body, string name) => body[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";

    private static string Cut(string text) => text.Length > 64 ? text[..64] : text;
}

/// <summary>The runner was installed without the built-in package's files beside its program.</summary>
public sealed class BuiltInMissingException(string message) : InvalidOperationException(message);

/// <summary>
/// Puts the built-in metadata package in a channel: its files from the folder beside the program, its manifest from <see cref="BuiltInPackages.Metadata"/>, served at once. Nothing is published when the channel already serves exactly
/// these files and this manifest, so a read costs one look at the state. A runner of a group whose members run other versions installs its own when it is asked, and the channel serves the last one installed.
/// </summary>
internal sealed class BuiltInInstaller(StateDatabase state, string directory)
{
    private static readonly RunnerActor Actor = new("runner", "runner", "builtin");
    private readonly Lazy<IReadOnlyList<(string Path, string File, string Sha256, long Size)>> _files = new(() => Scan(directory));
    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);

    public void Ensure(string channel, RunnerPublishing publishing)
    {
        var files = _files.Value;
        var manifest = BuiltInPackages.Metadata();
        lock (_gates.GetOrAdd(channel, _ => new object()))
        {
            if (Serves(channel, publishing, manifest, files)) return;
            foreach (var file in files.Where(f => !publishing.HasArtifact(f.Sha256)))
            {
                using var stream = File.OpenRead(file.File);
                publishing.StoreArtifactAsync(file.Sha256, stream, null, Actor, audit: false).GetAwaiter().GetResult();
            }

            publishing.Publish(new PublishInput(MetadataPackage.Name, manifest, [.. files.Select(f => new PublishFile(f.Path, f.Sha256, f.Size, null))], channel, PublishMode.Serve,
                Note: "the built-in package of this runner's program"), Actor, RunnerPublishing.InstallVerb);
        }
    }

    private bool Serves(string channel, RunnerPublishing publishing, string manifest, IReadOnlyList<(string Path, string File, string Sha256, long Size)> files)
    {
        var active = publishing.Publisher.HeadsOf(RunnerPublishing.Kind, MetadataPackage.Name).FirstOrDefault(h => h.Channel == channel)?.Active;
        if (active is not { } version || new DefinitionStore(state).Get(RunnerPublishing.Kind, MetadataPackage.Name, version) is not { } stored || stored.Content != manifest) return false;
        var have = publishing.Publisher.Artifacts(RunnerPublishing.Kind, MetadataPackage.Name, version).Select(a => (a.Path, a.Sha256)).ToHashSet();
        return have.SetEquals(files.Select(f => (f.Path, f.Sha256)));
    }

    private static List<(string Path, string File, string Sha256, long Size)> Scan(string directory)
    {
        if (!File.Exists(Path.Combine(directory, MetadataPackage.Assembly)))
            throw new BuiltInMissingException($"This runner has no built-in package {MetadataPackage.Name} beside its program (expected {Path.Combine(directory, MetadataPackage.Assembly)}); install the runner again.");
        var files = new List<(string, string, string, long)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(path);
            files.Add((Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'), path, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length));
        }

        return files;
    }
}
