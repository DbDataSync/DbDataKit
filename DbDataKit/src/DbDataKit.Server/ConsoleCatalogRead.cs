using System.Text.Json.Nodes;
using DbDataKit.Catalog;
using DbDataKit.Runner;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>What a screen asks to read: the Runner (a registered server's id or name), the channel, the project's connection, and optionally one schema or one table of it.</summary>
public sealed record CatalogReadBody(string? Runner, string? Channel, string? Connection, string? Schema = null, string? Table = null);

/// <summary>An answer of <see cref="ConsoleCatalogRead"/>: the status the console answers with and its JSON body.</summary>
public sealed record CatalogReadAnswer(int Status, JsonObject Body)
{
    public static CatalogReadAnswer Error(int status, string message) => new(status, new JsonObject { ["error"] = message });
}

/// <summary>
/// The console reads a database's shape for a project of a paired Workspace server, from a channel of a paired Runner, as the signed-in person: the Workspace server says whether the person may read the project and which package it
/// becomes on a Runner, and the console starts the Runner's built-in task <c>discover</c> for that package, the connection's slot and the channel (<c>POST /api/v1/metadata/{channel}/discover</c> with the person named). The Workspace
/// server is never in the path of the read: it holds no login and calls no Runner. The console waits up to <see cref="Wait"/> for the run; after that it answers the run, and the screen asks again with the run's id until it ends.
/// The answer of a finished read is the connection as a file of <c>catalog-cache/2</c> holding that one connection, recorded as read from the Runner and the channel; the screen compares it with the cache it holds and replaces the entry.
/// Design: docs/authoring-metadata.md#the-catalog-cache
/// </summary>
public sealed class ConsoleCatalogRead(ConsoleInstances instances, ConsoleRelay relay)
{
    /// <summary>The built-in package and its task, as the Runner names them.</summary>
    private const string BuiltIn = "dbdatakit-metadata";
    private const string Discover = "discover";

    /// <summary>How long a read is waited for before the console answers the run instead (the screen then follows it).</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the run is looked at while the console waits.</summary>
    public TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The channel that is production on a Runner: a read there goes ahead, and the screen warns first.</summary>
    public const string Production = "prod";

    /// <summary>
    /// The channels of the project's package on the Runner and whether each binds the connection's slot (<see cref="ConnectionSlots.NameOf"/>): what the channel picker lists. <c>default</c> is the first channel that is not production and
    /// binds the slot, or null. A package the Runner does not have yet answers no channel, with a sentence that says how to put it there.
    /// </summary>
    public async Task<CatalogReadAnswer> ChannelsAsync(string workspace, string project, string? runner, string? connection, CancellationToken ct)
    {
        var (package, refused) = await ProjectAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        var (target, problem) = await RunnerAsync(runner, ct);
        if (problem is not null) return problem;

        var slot = ConnectionSlots.NameOf(connection!);
        var (status, body) = await relay.CallAsync(target!, HttpMethod.Get, $"/api/v1/definitions/integration/{Uri.EscapeDataString(package!)}", null, ct);
        var answer = new JsonObject
        {
            ["package"] = package, ["connection"] = connection, ["slot"] = slot, ["runner"] = target!.InstanceId, ["runner_name"] = target.Name,
        };
        if (status == 404)
        {
            answer["channels"] = new JsonArray();
            answer["default"] = null;
            answer["note"] = $"{target.Name} has no package {package} yet. Publish a build of the draft to a channel of {target.Name} (staged is enough), then an administrator binds the slot '{slot}' in that channel on the Runner's Packages page (Bindings).";
            return new(200, answer);
        }
        if (status != 200 || body is not JsonObject described) return Passed(status, body, $"{target.Name} did not say which channels hold {package}.");

        var bound = (described["bindings"] as JsonArray ?? []).OfType<JsonObject>().Where(b => (string?)b["slot"] == slot)
            .GroupBy(b => (string?)b["channel"] ?? "").ToDictionary(g => g.Key, g => (string?)g.First()["bound_to"], StringComparer.Ordinal);
        var channels = (described["channels"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (string?)c["channel"]).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        answer["channels"] = new JsonArray([.. channels.Select(c => (JsonNode?)new JsonObject
        {
            ["channel"] = c, ["production"] = c == Production, ["bound"] = bound.ContainsKey(c), ["bound_to"] = bound.GetValueOrDefault(c),
        })]);
        answer["default"] = channels.FirstOrDefault(c => c != Production && bound.ContainsKey(c));
        if (!channels.Any(bound.ContainsKey))
            answer["note"] = $"No channel of {package} on {target.Name} binds the slot '{slot}'. An administrator binds it on the Runner's Packages page (Bindings), to a connection the Runner has, in the channel to read from.";
        return new(200, answer);
    }

    /// <summary>Starts the read and waits for it up to <see cref="Wait"/>: the finished read (200), the run still going (202), or the run's failure (422).</summary>
    public async Task<CatalogReadAnswer> StartAsync(string workspace, string project, CatalogReadBody body, string person, CancellationToken ct)
    {
        var (package, refused) = await ProjectAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        if (!PackageManifestValidator.NameRule.IsMatch(body.Channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel to read from: `channel` (lower-case letters, digits, - and _).");
        if (!string.IsNullOrWhiteSpace(body.Table) && string.IsNullOrWhiteSpace(body.Schema)) return CatalogReadAnswer.Error(400, "A table is read with its schema: give `schema` with `table`.");
        var (target, problem) = await RunnerAsync(body.Runner, ct);
        if (problem is not null) return problem;

        var inputs = new JsonObject();
        if (!string.IsNullOrWhiteSpace(body.Schema)) inputs["schemas"] = new JsonArray(body.Schema.Trim()).ToJsonString();
        if (!string.IsNullOrWhiteSpace(body.Table)) inputs["table"] = body.Table.Trim();
        var (status, started) = await relay.CallAsync(target!, HttpMethod.Post, $"/api/v1/metadata/{Uri.EscapeDataString(body.Channel!)}/{Discover}",
            new JsonObject { ["package"] = package, ["slot"] = ConnectionSlots.NameOf(body.Connection!), ["inputs"] = inputs, ["on_behalf_of"] = person }, ct);
        if (status != 202 || (string?)started?["run_id"] is not { } runText || !Guid.TryParse(runText, out var run))
            return Passed(status, started, $"{target!.Name} did not start the read.");

        var until = DateTimeOffset.UtcNow + Wait;
        while (true)
        {
            var answer = await OutcomeAsync(target!, body.Channel!, body.Connection!, run, ct);
            if (answer.Status != 202 || DateTimeOffset.UtcNow >= until) return answer;
            await Task.Delay(Poll, ct);
        }
    }

    /// <summary>The read the screen follows after the console stopped waiting: as <see cref="StartAsync"/> answers, for the run <paramref name="run"/>. The person must still have access to the project.</summary>
    public async Task<CatalogReadAnswer> FollowAsync(string workspace, string project, string? runner, string? channel, string? connection, string run, CancellationToken ct)
    {
        var (_, refused) = await ProjectAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        if (!PackageManifestValidator.NameRule.IsMatch(channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel the read was started in: `channel`.");
        if (!Guid.TryParse(run, out var id)) return CatalogReadAnswer.Error(400, $"'{run}' is not a run's id.");
        var (target, problem) = await RunnerAsync(runner, ct);
        if (problem is not null) return problem;
        return await OutcomeAsync(target!, channel!, connection!, id, ct);
    }

    /// <summary>Where the run is: still going (202), failed (422, with the Runner's reason), or finished, with the connection's entry read from the step <c>discover</c> the run kept (200).</summary>
    private async Task<CatalogReadAnswer> OutcomeAsync(RegisteredInstance target, string channel, string connection, Guid run, CancellationToken ct)
    {
        var id = run.ToString("D");
        JsonObject Base(string state) => new()
        {
            ["state"] = state, ["run_id"] = id, ["link"] = $"/servers/{Uri.EscapeDataString(target.InstanceId)}/runs?run={id}",
            ["runner"] = target.InstanceId, ["runner_name"] = target.Name, ["channel"] = channel, ["production"] = channel == Production, ["connection"] = connection,
        };

        var (status, record) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/runs/{id}", null, ct);
        // a run that is queued has no record yet
        if (status == 404) return new(202, Base("running"));
        if (status != 200 || record is not JsonObject found) return Passed(status, record, $"{target.Name} did not say how the read {id} went.");
        if ((string?)found["package"] != BuiltIn || (string?)found["channel"] != channel || (string?)found["task"] != Discover)
            return CatalogReadAnswer.Error(404, $"The run {id} is not a read of a database's shape in channel '{channel}'.");
        var outcome = (string?)found["outcome"] ?? "running";
        if (outcome == "running") return new(202, Base("running"));
        if (outcome != "succeeded")
        {
            var failed = Base("failed");
            failed["error"] = $"The read failed on {target.Name} ({outcome}): {(string?)found["failure"]?["message"] ?? "the run gave no reason"}";
            return new(422, failed);
        }

        var (stepStatus, steps) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/tasks/{BuiltIn}/{Uri.EscapeDataString(channel)}/{Discover}/steps/{Discover}?limit=1&key={id}", null, ct);
        if (stepStatus != 200) return Passed(stepStatus, steps, $"{target.Name} did not give the result of the read {id}.");
        if ((steps?["steps"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is not { } step || (string?)step["payload"] is not { } payload)
            return CatalogReadAnswer.Error(502, $"The read {id} finished on {target.Name} without a result.");
        var (payloadStatus, answer) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/tasks/{BuiltIn}/{Uri.EscapeDataString(channel)}/{Discover}/steps/{Discover}/payloads/{Uri.EscapeDataString(payload)}", null, ct);
        if (payloadStatus != 200) return Passed(payloadStatus, answer, $"{target.Name} did not give the result of the read {id}.");
        if (answer is not JsonObject shape) return CatalogReadAnswer.Error(502, $"The result of the read {id} is not a description of a connection.");

        var at = DateTimeOffset.TryParse((string?)step["completed"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var completed) ? completed : DateTimeOffset.UtcNow;
        var entry = CatalogCache.FromAnswer(shape, new CacheRead(target.Name, "runner", channel, CatalogCache.Stamp(at)));
        string text;
        try { text = CatalogCache.Write(CatalogCache.WithConnection(null, connection, entry, at)); }
        catch (InvalidDataException ex) { return CatalogReadAnswer.Error(502, $"The result of the read {id} cannot be a catalog cache: {ex.Message}"); }
        // the cache's strict reader, the one the draft's check runs: a value that looks like a secret or a row is refused here, before the screen ever holds it
        var load = CatalogCache.Load(text);
        if (!load.Ok)
            return CatalogReadAnswer.Error(502, $"The result of the read {id} is not a catalog the cache can hold: {string.Join(" ", load.Diagnostics.Select(d => d.Message))}");

        var done = Base("done");
        done["cache"] = text;
        done["tables"] = entry.Tables.Count;
        done["columns"] = entry.Tables.Sum(t => t.Columns.Count);
        done["read"] = new JsonObject { ["server"] = target.Name, ["product"] = "runner", ["channel"] = channel, ["at"] = CatalogCache.Stamp(at) };
        return new(200, done);
    }

    /// <summary>The project's package on a Runner, asked of the Workspace server as the signed-in person (which also says whether they may read the project); a refusal is the Workspace server's own words.</summary>
    internal async Task<(string? Package, CatalogReadAnswer? Error)> ProjectAsync(string workspace, string project, CancellationToken ct)
    {
        if (instances.Registry.Find(workspace) is not { } server) return (null, CatalogReadAnswer.Error(404, $"No server '{workspace}' is registered."));
        if (await relay.ProductOfAsync(server, ct) != ServerProducts.Workspace) return (null, CatalogReadAnswer.Error(409, $"'{server.Name}' is not a Workspace server."));
        var (status, body) = await relay.CallAsync(server, HttpMethod.Get, $"/api/v1/projects/{Uri.EscapeDataString(project)}", null, ct);
        if (status == 404) return (null, CatalogReadAnswer.Error(404, $"{server.Name} has no project '{project}'."));
        if (status != 200 || body is not JsonObject found) return (null, Passed(status, body, $"{server.Name} did not say whether you may read the project '{project}'."));
        if ((string?)found["package"] is not { Length: > 0 } package)
            return (null, CatalogReadAnswer.Error(409, $"The project '{project}' is a {(string?)found["kind"] ?? "kind of"} project, which makes no package of tasks on a Runner, so its connections are not read there."));
        return (package, null);
    }

    internal async Task<(RegisteredInstance? Runner, CatalogReadAnswer? Error)> RunnerAsync(string? runner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runner)) return (null, CatalogReadAnswer.Error(400, "Name the Runner to read from: `runner`."));
        if (instances.Registry.Find(runner) is not { } target) return (null, CatalogReadAnswer.Error(404, $"No server '{runner}' is registered."));
        if (await relay.ProductOfAsync(target, ct) != ServerProducts.Runner) return (null, CatalogReadAnswer.Error(409, $"'{target.Name}' is not a Runner."));
        return (target, null);
    }

    /// <summary>The connection is a name of the project's (a file under <c>connections/</c>); the package reads it through the slot <see cref="ConnectionSlots.NameOf"/> makes of it.</summary>
    internal static string? SlotProblem(string? connection) =>
        connection is { Length: > 0 and <= 128 } && !connection.Any(char.IsControl) && connection.Trim() == connection ? null : "Name the project's connection to read: `connection`.";

    /// <summary>A server's refusal handed on with its own words, or <paramref name="otherwise"/> when it gave none.</summary>
    internal static CatalogReadAnswer Passed(int status, JsonNode? body, string otherwise) =>
        CatalogReadAnswer.Error(status is >= 400 ? status : 502, (body as JsonObject)?["error"]?.GetValue<string>() ?? otherwise);
}
