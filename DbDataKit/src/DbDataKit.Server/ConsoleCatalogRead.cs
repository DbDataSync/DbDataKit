using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataKit.Catalog;
using DbDataKit.Deploy;
using DbDataKit.Runner;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>What a screen asks to read: the Runner (a registered server's id or name), the channel, the project's connection, and optionally one schema or one table of it.</summary>
public sealed record CatalogReadBody(string? Runner, string? Channel, string? Connection, string? Schema = null, string? Table = null);

/// <summary>What a screen asks to describe: the Runner (a registered server's id or name), the channel, the project's connection, and the query whose columns it wants.</summary>
public sealed record DescribeBody(string? Runner, string? Channel, string? Connection, string? Sql);

/// <summary>What a screen asks to read from a Gateway: the Gateway (a registered server's id or name), the project's connection, the Gateway's data source for it (the connection's name when none is given), and whether the person confirmed reading a data source made by hand.</summary>
public sealed record GatewayReadBody(string? Gateway, string? Connection, [property: JsonPropertyName("data_source")] string? DataSource = null, bool Adopt = false);

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
/// <para>
/// A connection that is a data source of a Gateway is read from that Gateway instead (<see cref="GatewayAsync"/>): the console asks the Gateway's schema call as the person, and the Gateway reads with its own login. The Workspace server is
/// not in that path either.
/// </para>
/// Design: docs/authoring-metadata.md#the-catalog-cache
/// </summary>
public sealed class ConsoleCatalogRead(ConsoleInstances instances, ConsoleRelay relay, ConsolePromotions promotions)
{
    /// <summary>The built-in package and its task, as the Runner names them.</summary>
    private const string BuiltIn = "dbdatakit-metadata";
    private const string Discover = "discover";
    private const string Describe = "describe";

    /// <summary>The longest query the console sends to be described.</summary>
    public const int MaxSqlChars = 100_000;

    /// <summary>How long a read is waited for before the console answers the run instead (the screen then follows it).</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the run is looked at while the console waits.</summary>
    public TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The channel that is production on a Runner: a read there goes ahead, and the screen warns first.</summary>
    public const string Production = "prod";

    /// <summary>
    /// The channels of the Runner and whether each binds the connection's slot (<see cref="ConnectionSlots.NameOf"/>) for the project's package: what the channel picker lists. Every channel the Runner serves is listed, whether or not
    /// it holds a version of the package (<c>GET /api/v1/metadata/channels?package=</c>, asked as the person): a channel with no version reads any slot it binds, so a draft is read there once Set up for development bound the slot.
    /// <c>default</c> is the first channel that is not production and binds the slot, or null. A Runner that does not answer that call (an older one) lists the channels that hold a version of the package (<see cref="PublishedChannelsAsync"/>).
    /// </summary>
    public async Task<CatalogReadAnswer> ChannelsAsync(string workspace, string project, string? runner, string? connection, CancellationToken ct)
    {
        var (package, refused) = await PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        var (target, problem) = await RunnerAsync(runner, ct);
        if (problem is not null) return problem;

        var slot = ConnectionSlots.NameOf(connection!);
        var answer = new JsonObject
        {
            ["package"] = package, ["connection"] = connection, ["slot"] = slot, ["runner"] = target!.InstanceId, ["runner_name"] = target.Name,
        };
        var (status, body) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/metadata/channels?package={Uri.EscapeDataString(package!)}", null, ct);
        if (status == 404) return await PublishedChannelsAsync(target, package!, slot, answer, ct);
        if (status != 200 || body is not JsonObject served) return Passed(status, body, $"{target.Name} did not say which channels it serves.");

        var channels = (served["channels"] as JsonArray ?? []).Select(c => (string?)c).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new(200, Listed(answer, channels, served["bindings"] as JsonArray, package!, target.Name, slot));
    }

    /// <summary>The channels in the answer, each with whether it binds the slot and to what, the first that is not production and binds it, and what to do when none does.</summary>
    private static JsonObject Listed(JsonObject answer, IReadOnlyList<string> channels, JsonArray? bindings, string package, string runner, string slot)
    {
        var bound = (bindings ?? []).OfType<JsonObject>().Where(b => (string?)b["slot"] == slot)
            .GroupBy(b => (string?)b["channel"] ?? "").ToDictionary(g => g.Key, g => (string?)g.First()["bound_to"], StringComparer.Ordinal);
        answer["channels"] = new JsonArray([.. channels.Select(c => (JsonNode?)new JsonObject
        {
            ["channel"] = c, ["production"] = c == Production, ["bound"] = bound.ContainsKey(c), ["bound_to"] = bound.GetValueOrDefault(c),
        })]);
        answer["default"] = channels.FirstOrDefault(c => c != Production && bound.ContainsKey(c));
        if (!channels.Any(bound.ContainsKey))
            answer["note"] = $"No channel of {runner} binds the slot '{slot}' for {package}. Set up for development binds it in the channel you pick, or an administrator binds it on the Runner's Packages page (Bindings), to a connection the Runner has.";
        return answer;
    }

    /// <summary>
    /// What an older Runner, which cannot list its channels for a package, offers: the channels that hold a version of the package, from its description. A package it holds no version of lists no channel, and the note says a build of
    /// the draft published to a channel comes first there.
    /// </summary>
    private async Task<CatalogReadAnswer> PublishedChannelsAsync(RegisteredInstance target, string package, string slot, JsonObject answer, CancellationToken ct)
    {
        var (status, body) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/definitions/integration/{Uri.EscapeDataString(package)}", null, ct);
        if (status == 404)
        {
            answer["channels"] = new JsonArray();
            answer["default"] = null;
            answer["note"] = $"{target.Name} has no package {package} yet. Set up for development binds the slot '{slot}' in a channel of {target.Name}; a read there needs a build of the draft published to that channel (staged is enough).";
            return new(200, answer);
        }
        if (status != 200 || body is not JsonObject described) return Passed(status, body, $"{target.Name} did not say which channels hold {package}.");
        var channels = (described["channels"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (string?)c["channel"]).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new(200, Listed(answer, channels, described["bindings"] as JsonArray, package, target.Name, slot));
    }

    /// <summary>Starts the read and waits for it up to <see cref="Wait"/>: the finished read (200), the run still going (202), or the run's failure (422).</summary>
    public async Task<CatalogReadAnswer> StartAsync(string workspace, string project, CatalogReadBody body, string person, CancellationToken ct)
    {
        var (package, refused) = await PackageAsync(workspace, project, ct);
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

        return await WaitAsync(() => OutcomeAsync(target!, body.Channel!, body.Connection!, run, ct), ct);
    }

    /// <summary>Asks how a run stands until it is no longer running or <see cref="Wait"/> has passed, and answers the last look.</summary>
    private async Task<CatalogReadAnswer> WaitAsync(Func<Task<CatalogReadAnswer>> look, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + Wait;
        while (true)
        {
            var answer = await look();
            if (answer.Status != 202 || DateTimeOffset.UtcNow >= until) return answer;
            await Task.Delay(Poll, ct);
        }
    }

    /// <summary>
    /// Starts the Runner's built-in task <c>describe</c> for the query, as the signed-in person, and waits for it up to <see cref="Wait"/>: the query's columns (200), the run still going (202), or the run's failure (422), such as a query
    /// that writes, which the read-only policy refuses before the connection opens. The query is described, never run: no row comes back.
    /// </summary>
    public async Task<CatalogReadAnswer> StartDescribeAsync(string workspace, string project, DescribeBody body, string person, CancellationToken ct)
    {
        var (package, refused) = await PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad.Replace("to read", "the query reads", StringComparison.Ordinal));
        if (!PackageManifestValidator.NameRule.IsMatch(body.Channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel to describe the query in: `channel` (lower-case letters, digits, - and _).");
        if (string.IsNullOrWhiteSpace(body.Sql)) return CatalogReadAnswer.Error(400, "Give the query to describe: `sql`.");
        if (body.Sql.Length > MaxSqlChars) return CatalogReadAnswer.Error(400, $"The query is longer than {MaxSqlChars} characters.");
        var (target, problem) = await RunnerAsync(body.Runner, ct);
        if (problem is not null) return problem;

        var (status, started) = await relay.CallAsync(target!, HttpMethod.Post, $"/api/v1/metadata/{Uri.EscapeDataString(body.Channel!)}/{Describe}",
            new JsonObject { ["package"] = package, ["slot"] = ConnectionSlots.NameOf(body.Connection!), ["inputs"] = new JsonObject { ["sql"] = body.Sql }, ["on_behalf_of"] = person }, ct);
        if (status != 202 || (string?)started?["run_id"] is not { } runText || !Guid.TryParse(runText, out var run))
            return Passed(status, started, $"{target!.Name} did not start describing the query.");
        return await WaitAsync(() => DescribedAsync(target!, body.Channel!, body.Connection!, run, ct), ct);
    }

    /// <summary>The description the screen follows after the console stopped waiting: as <see cref="StartDescribeAsync"/> answers, for the run <paramref name="run"/>. The person must still have access to the project.</summary>
    public async Task<CatalogReadAnswer> FollowDescribeAsync(string workspace, string project, string? runner, string? channel, string? connection, string run, CancellationToken ct)
    {
        var (_, refused) = await PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        if (!PackageManifestValidator.NameRule.IsMatch(channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel the query was described in: `channel`.");
        if (!Guid.TryParse(run, out var id)) return CatalogReadAnswer.Error(400, $"'{run}' is not a run's id.");
        var (target, problem) = await RunnerAsync(runner, ct);
        if (problem is not null) return problem;
        return await DescribedAsync(target!, channel!, connection!, id, ct);
    }

    /// <summary>The read the screen follows after the console stopped waiting: as <see cref="StartAsync"/> answers, for the run <paramref name="run"/>. The person must still have access to the project.</summary>
    public async Task<CatalogReadAnswer> FollowAsync(string workspace, string project, string? runner, string? channel, string? connection, string run, CancellationToken ct)
    {
        var (_, refused) = await PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if (SlotProblem(connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        if (!PackageManifestValidator.NameRule.IsMatch(channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel the read was started in: `channel`.");
        if (!Guid.TryParse(run, out var id)) return CatalogReadAnswer.Error(400, $"'{run}' is not a run's id.");
        var (target, problem) = await RunnerAsync(runner, ct);
        if (problem is not null) return problem;
        return await OutcomeAsync(target!, channel!, connection!, id, ct);
    }

    /// <summary>Where the run is: still going (202), failed (422, with the Runner's reason), or finished, with the connection's entry read from the step <c>discover</c> the run kept (200).</summary>
    private static JsonObject BaseOf(RegisteredInstance target, string channel, string connection, string id, string state) => new()
    {
        ["state"] = state, ["run_id"] = id, ["link"] = $"/servers/{Uri.EscapeDataString(target.InstanceId)}/runs?run={id}",
        ["runner"] = target.InstanceId, ["runner_name"] = target.Name, ["channel"] = channel, ["production"] = channel == Production, ["connection"] = connection,
    };

    /// <summary>
    /// How a run of the built-in task <paramref name="task"/> stands: null when it succeeded (the caller reads its step), else the answer to give: still going (202, also while it is queued and has no record yet), failed (422, with
    /// the Runner's reason), or a refusal.
    /// </summary>
    private async Task<CatalogReadAnswer?> RunStateAsync(RegisteredInstance target, string channel, string connection, Guid run, string task, string what, CancellationToken ct)
    {
        var id = run.ToString("D");
        var (status, record) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/runs/{id}", null, ct);
        // a run that is queued has no record yet
        if (status == 404) return new(202, BaseOf(target, channel, connection, id, "running"));
        if (status != 200 || record is not JsonObject found) return Passed(status, record, $"{target.Name} did not say how the {what} {id} went.");
        if ((string?)found["package"] != BuiltIn || (string?)found["channel"] != channel || (string?)found["task"] != task)
            return CatalogReadAnswer.Error(404, task == Discover
                ? $"The run {id} is not a read of a database's shape in channel '{channel}'."
                : $"The run {id} is not a description of a query in channel '{channel}'.");
        var outcome = (string?)found["outcome"] ?? "running";
        if (outcome == "running") return new(202, BaseOf(target, channel, connection, id, "running"));
        if (outcome == "succeeded") return null;
        var failed = BaseOf(target, channel, connection, id, "failed");
        failed["error"] = $"The {what} failed on {target.Name} ({outcome}): {(string?)found["failure"]?["message"] ?? "the run gave no reason"}";
        return new(422, failed);
    }

    /// <summary>Where a description of a query is: still going (202), failed (422), or finished, with the columns the step <c>describe</c> of the run kept (200): each column's name, its type as the engine names it, and its nullability.</summary>
    private async Task<CatalogReadAnswer> DescribedAsync(RegisteredInstance target, string channel, string connection, Guid run, CancellationToken ct)
    {
        if (await RunStateAsync(target, channel, connection, run, Describe, "description of the query", ct) is { } notDone) return notDone;
        var id = run.ToString("D");
        var (stepStatus, steps) = await relay.CallAsync(target, HttpMethod.Get, $"/api/v1/tasks/{BuiltIn}/{Uri.EscapeDataString(channel)}/{Describe}/steps/{Describe}?limit=1&key={id}", null, ct);
        if (stepStatus != 200) return Passed(stepStatus, steps, $"{target.Name} did not give the columns of the query {id}.");
        if ((steps?["steps"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["result"] is not JsonObject result || result["columns"] is not JsonArray described)
            return CatalogReadAnswer.Error(502, $"The description {id} finished on {target.Name} without the query's columns.");

        // only a column's name, type and nullability are passed on: the answer of a description holds no value
        var columns = new JsonArray();
        foreach (var column in described.OfType<JsonObject>())
        {
            if ((string?)column["name"] is not { } name || (string?)column["type"] is not { } type)
                return CatalogReadAnswer.Error(502, $"The description {id} has a column without a name or a type.");
            columns.Add(new JsonObject { ["name"] = name, ["type"] = type, ["nullable"] = (bool?)column["nullable"] ?? true });
        }
        var done = BaseOf(target, channel, connection, id, "done");
        done["engine"] = (string?)result["engine"];
        done["strategy"] = (string?)result["strategy"];
        done["columns"] = columns;
        return new(200, done);
    }

    private async Task<CatalogReadAnswer> OutcomeAsync(RegisteredInstance target, string channel, string connection, Guid run, CancellationToken ct)
    {
        var id = run.ToString("D");
        JsonObject Base(string state) => BaseOf(target, channel, connection, id, state);
        if (await RunStateAsync(target, channel, connection, run, Discover, "read", ct) is { } notDone) return notDone;

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

    /// <summary>
    /// The shape of a data source of a Gateway, for the project's connection: the Gateway's schema call (<c>POST /api/v1/data-sources/{name}/schema</c>, <see cref="GatewayCalls.ReadSchemaAsync"/>) as the signed-in person, which the
    /// Gateway answers with the tables, columns and keys it reads with its own login. A data source the Gateway says was made by hand is read only once the person confirmed it (<see cref="GatewayReadBody.Adopt"/>); until then the
    /// refusal carries <c>adopt</c> (the data source and who made it) for the screen to ask. The answer is a file of <c>catalog-cache/2</c> holding that one connection, recorded as read from the Gateway and checked by the cache's strict
    /// reader; the screen compares it with the cache it holds, as it does a Runner's read. An app is refused: it has no connections.
    /// </summary>
    public async Task<CatalogReadAnswer> GatewayAsync(string workspace, string project, GatewayReadBody body, RelayPerson person, CancellationToken ct)
    {
        var (found, refused) = await ProjectAsync(workspace, project, ct);
        if (refused is not null) return refused;
        if ((string?)found!["kind"] == "app") return CatalogReadAnswer.Error(409, $"The project '{project}' is an app, which has no connections, so it has no catalog to read.");
        if (SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad);
        if (string.IsNullOrWhiteSpace(body.Gateway)) return CatalogReadAnswer.Error(400, "Name the Gateway to read from: `gateway`.");
        if (instances.Registry.Find(body.Gateway) is not { } gateway) return CatalogReadAnswer.Error(404, $"No server '{body.Gateway}' is registered.");
        if (await relay.ProductOfAsync(gateway, ct) != ServerProducts.Gateway) return CatalogReadAnswer.Error(409, $"'{gateway.Name}' is not a Gateway: the catalog of a data source is read from a Gateway.");
        var dataSource = string.IsNullOrWhiteSpace(body.DataSource) ? body.Connection! : body.DataSource.Trim();

        var link = promotions.LinkFor(gateway.InstanceId, person) ?? throw new InvalidOperationException("A registered server has a link.");
        var target = new DeployTarget(gateway.Name, gateway.Url.ToString().TrimEnd('/'), gateway.InstanceId, DeployProducts.Gateway);
        JsonObject schema;
        try { schema = await GatewayCalls.ReadSchemaAsync(link, target, dataSource, body.Adopt, _ => { }, ct); }
        catch (DeployException ex)
        {
            var refusal = CatalogReadAnswer.Error(ex.Status, ex.Message);
            if (ex.Adopt is not null) refusal.Body["adopt"] = ex.Adopt.DeepClone();
            return refusal;
        }

        var at = DateTimeOffset.UtcNow;
        string text;
        try { text = CatalogCache.Write(CatalogCache.Refreshed(null, body.Connection!, schema, gateway.Name, DeployProducts.Gateway, at)); }
        catch (InvalidDataException ex) { return CatalogReadAnswer.Error(502, $"The catalog {gateway.Name} answered for '{dataSource}' cannot be a catalog cache: {ex.Message}"); }
        // the cache's strict reader, the one the draft's check runs: a value that looks like a secret or a row is refused here, before the screen ever holds it
        var load = CatalogCache.Load(text);
        if (!load.Ok)
            return CatalogReadAnswer.Error(502, $"The catalog {gateway.Name} answered for '{dataSource}' is not one the cache can hold: {string.Join(" ", load.Diagnostics.Select(d => d.Message))}");
        var entry = load.Cache!.Connections[body.Connection!];
        return new(200, new JsonObject
        {
            ["state"] = "done", ["gateway"] = gateway.InstanceId, ["gateway_name"] = gateway.Name, ["connection"] = body.Connection, ["data_source"] = dataSource,
            ["cache"] = text, ["tables"] = entry.Tables.Count, ["columns"] = entry.Tables.Sum(t => t.Columns.Count),
            ["read"] = new JsonObject { ["server"] = gateway.Name, ["product"] = DeployProducts.Gateway, ["at"] = CatalogCache.Stamp(at) },
        });
    }

    /// <summary>The project's package on a Runner (<see cref="ProjectAsync"/>); a project whose kind makes none is refused, since its connections are not read on a Runner.</summary>
    internal async Task<(string? Package, CatalogReadAnswer? Error)> PackageAsync(string workspace, string project, CancellationToken ct)
    {
        var (found, refused) = await ProjectAsync(workspace, project, ct);
        if (refused is not null) return (null, refused);
        if ((string?)found!["package"] is not { Length: > 0 } package)
            return (null, CatalogReadAnswer.Error(409, $"The project '{project}' is a {(string?)found["kind"] ?? "kind of"} project, which makes no package of tasks on a Runner, so its connections are not read there."));
        return (package, null);
    }

    /// <summary>The project as the Workspace server describes it (its kind, and the package it becomes on a Runner), asked as the signed-in person, which also says whether they may read the project; a refusal is the Workspace server's own words.</summary>
    private async Task<(JsonObject? Project, CatalogReadAnswer? Error)> ProjectAsync(string workspace, string project, CancellationToken ct)
    {
        if (instances.Registry.Find(workspace) is not { } server) return (null, CatalogReadAnswer.Error(404, $"No server '{workspace}' is registered."));
        if (await relay.ProductOfAsync(server, ct) != ServerProducts.Workspace) return (null, CatalogReadAnswer.Error(409, $"'{server.Name}' is not a Workspace server."));
        var (status, body) = await relay.CallAsync(server, HttpMethod.Get, $"/api/v1/projects/{Uri.EscapeDataString(project)}", null, ct);
        if (status == 404) return (null, CatalogReadAnswer.Error(404, $"{server.Name} has no project '{project}'."));
        if (status != 200 || body is not JsonObject found) return (null, Passed(status, body, $"{server.Name} did not say whether you may read the project '{project}'."));
        return (found, null);
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
