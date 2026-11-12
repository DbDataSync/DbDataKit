using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <param name="Engine"><c>postgres</c>, <c>mssql</c> or <c>sqlite</c>.</param>
/// <param name="Connection">Server, database and login, without the password.</param>
/// <param name="Path">The file of a SQLite state (in a move, a path on the server that is moved).</param>
/// <param name="Password">Stored in the console's secret store; empty on an edit keeps the stored one.</param>
public sealed record SaveStateConnectionRequest(string Engine, string? Connection, string? Path, string? Password, string? Note);

/// <param name="Server">The paired server whose state is moved: its id or name.</param>
/// <param name="Connection">The name of the state connection that is the target.</param>
public sealed record StateMoveRequest(string Server, string Connection);

/// <summary>
/// State databases as connections of kind <c>state</c> and the move of a paired server's state onto one (plan 29b, C06). Administrators only, by the fallback policy: a state connection holds a login, and a move changes what a server runs on.
/// The checks are the shared library's (<see cref="DbDataKit.State.Cli.StateConnectionCheck"/>, the checks of <c>state test</c>) and the servers' own (<c>GET /api/v1/state</c>, the move preflight), reached through the relay as the signed-in person.
/// </summary>
[ApiController]
[Route("api/state-connections")]
public sealed class StateConnectionsController(ConsoleStateConnections connections, ConsoleInstances instances, ConsoleRelay relay, ConsoleSettings settings) : ControllerBase
{
    /// <summary>The products whose state a console can move: the ones whose server mounts the move call.</summary>
    private static readonly Dictionary<string, string> Programs = new() { ["dbdatagateway"] = "dbdatagateway", ["dbdatastore"] = "dbdatastore", ["dbdatabuild"] = "dbdatabuild", ["dbdatarunner"] = "dbdatarunner" };

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Each connection with the servers that use it (matched on the engine, server, port and database of the connection each server reports, masked) and the module versions found there.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var uses = await ServerStatesAsync(cancellationToken);
        var rows = await Task.WhenAll(connections.All().Select(async c =>
        {
            var identity = ConsoleStateConnections.IdentityOf(c.Engine, c.Connection, c.Path);
            var report = await TestAsync(c);
            return new
            {
                name = c.Name, engine = c.Engine, connection = c.Engine == "sqlite" ? c.Path : DbDataKit.Secrets.ConnectionStringReferences.Mask(c.Connection), note = c.Note,
                secret = c.Engine == "sqlite" ? null : ConsoleStateConnections.SecretNameOf(c.Name).ToString(),
                reachable = report.Reachable, empty = report.Empty, ok = report.Ok, sentence = report.Sentence,
                modules = report.Modules.Select(m => new { m.Module, m.Scope, m.Version }),
                usedBy = uses.Where(u => u.Identity == identity).Select(u => new { u.Id, u.Name, u.Product }).ToList(),
            };
        }));
        return Ok(rows);
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Save(string name, [FromBody] SaveStateConnectionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await connections.SaveAsync(name, request.Engine, request.Connection, request.Path, request.Password, request.Note, cancellationToken);
            return Ok(new { name = saved.Name, engine = saved.Engine, connection = saved.Connection is null ? saved.Path : DbDataKit.Secrets.ConnectionStringReferences.Mask(saved.Connection) });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Remove(string name, CancellationToken cancellationToken)
    {
        if (connections.Find(name) is not { } found) return NotFound(new { error = "No such state connection." });
        var identity = ConsoleStateConnections.IdentityOf(found.Engine, found.Connection, found.Path);
        if ((await ServerStatesAsync(cancellationToken)).Where(u => u.Identity == identity).Select(u => u.Name).ToList() is { Count: > 0 } users)
            return Conflict(new { error = $"{string.Join(", ", users)} use{(users.Count == 1 ? "s" : "")} this state database: move {(users.Count == 1 ? "it" : "them")} elsewhere first, then remove the connection." });
        await connections.RemoveAsync(name, cancellationToken);
        return NoContent();
    }

    /// <summary><c>state test</c> on one connection, as one sentence that names the fix and the steps behind it.</summary>
    [HttpPost("{name}/test")]
    public async Task<IActionResult> Test(string name, CancellationToken cancellationToken)
    {
        if (connections.Find(name) is not { } found) return NotFound(new { error = "No such state connection." });
        var report = await TestAsync(found);
        return Ok(new
        {
            ok = report.Ok, report.Reachable, report.Empty, sentence = report.Sentence,
            steps = report.Steps.Select(s => new { s.Name, s.Status, s.Message }),
            modules = report.Modules.Select(m => new { m.Module, m.Scope, m.Version }),
        });
    }

    /// <summary>
    /// What a move of <paramref name="request"/>.Server onto the connection needs, each as a check with one sentence: the server offers the state API, the target is reachable and empty, the login's rights, the engine and its features, it is not the server's own
    /// database, and the server can resolve the password reference in its own secret store (asked of the server itself, which also checks its settings file). Nothing is changed.
    /// </summary>
    [HttpPost("/api/state-moves/preflight")]
    public async Task<IActionResult> Preflight([FromBody] StateMoveRequest request, CancellationToken cancellationToken)
    {
        var (server, connection, problem) = Resolve(request);
        if (problem is not null) return problem;
        var checks = new List<object>();
        var ok = true;
        void Add(string name, bool passed, string sentence) { checks.Add(new { name, ok = passed, sentence }); ok &= passed; }

        var status = await instances.StatusAsync(server!, cancellationToken);
        var program = status.Product is { } product && Programs.TryGetValue(product, out var p) ? p : null;
        if (!status.Reachable) { Add("server", false, $"{server!.Name} is not answering: {status.Problem ?? "no answer"}. Start it, then try again."); return Ok(new { ok, checks, program }); }
        if (program is null) { Add("server", false, $"The console cannot move the state of {status.Product ?? "this kind of server"}; its state is moved on its machine with its own state move command."); return Ok(new { ok, checks, program }); }

        var (code, state) = await relay.CallAsync(server!, HttpMethod.Get, "/api/v1/state", null, cancellationToken);
        if (code != 200 || state is not JsonObject)
        {
            Add("server", false, code == 404 ? OldServer(program, connection!) : $"{server!.Name} would not report its state ({code}): {state?["error"]?.GetValue<string>() ?? "no answer"}.");
            return Ok(new { ok, checks, program, old = code == 404 });
        }

        Add("server", true, $"{server!.Name} ({status.Product} {status.Version}) reports its state: engine {state["engine"]?.GetValue<string>() ?? "?"}, connection {state["connection"]?.GetValue<string>() ?? "its own file"}.");

        // the target's checks are the server's own, made with its own modules (their tables, their engine features) and its own secret store: the dry run of the move answers them (plan 29b C06)
        var (dryCode, dry) = await relay.CallAsync(server, HttpMethod.Post, "/api/v1/state/move", Payload(connection!, dryRun: true), cancellationToken);
        if (dryCode == 200 && dry?["steps"] is JsonArray steps)
        {
            foreach (var step in steps.OfType<JsonObject>())
                Add($"target {step["name"]?.GetValue<string>()}", step["status"]?.GetValue<string>() != "failed", step["message"]?.GetValue<string>() ?? "");
            Add("target empty", true, "The target holds no module: it can take the state.");
        }
        else if (dryCode == 200) Add("server can open the target", true, dry?["message"]?.GetValue<string>() ?? "The server can open the target.");
        else
        {
            // an older server answers without the steps: the checks the console can make itself (never for a SQLite file, which is on the server's machine)
            if (connection!.Engine != "sqlite" && dryCode != 404)
            {
                var report = await TestAsync(connection);
                foreach (var step in report.Steps) Add($"target {step.Name}", step.Status != "failed", step.Message);
                if (report.Ok) Add("target empty", report.Empty, report.Empty ? "The target holds no module: it can take the state." : $"The target already holds {string.Join(", ", report.Modules.Select(m => m.Module).Distinct())}: a move goes into an empty database. Create a new empty database and point the connection at it.");
            }

            switch (dryCode)
            {
                case 404: Add("server offers the move", false, OldServer(program, connection!)); break;
                case 400 when (dry?["reason"]?.GetValue<string>() == "secretmissing"):
                    Add("server has the secret", false, $"{server.Name} cannot resolve the password of this connection: set it on that server with '{program} secret set {ConsoleStateConnections.SecretNameOf(connection!.Name)}' (the console never copies a password to a server), then check again.");
                    break;
                default: Add("server can open the target", false, dry?["error"]?.GetValue<string>() ?? $"{server.Name} refused the check ({dryCode}).");
                    break;
            }
        }

        // never the server's own database
        var own = ConsoleStateConnections.IdentityOf(state["engine"]?.GetValue<string>(), state["connection"]?.GetValue<string>(), state["file"]?.GetValue<string>());
        var target = ConsoleStateConnections.IdentityOf(connection!.Engine, connection.Connection, connection.Path);
        Add("not the same database", own != target, own != target ? "The target is not the database the server uses now." : "The target is the database this server already uses: name another.");

        return Ok(new { ok, checks, program, maintenance = state["maintenance"] });
    }

    /// <summary>Starts the move on the server (it has to be in maintenance and idle: the server checks, and refuses with 409). Nothing is restarted. The server audits who started it.</summary>
    [HttpPost("/api/state-moves/start")]
    public async Task<IActionResult> Start([FromBody] StateMoveRequest request, CancellationToken cancellationToken)
    {
        var (server, connection, problem) = Resolve(request);
        if (problem is not null) return problem;
        var (code, body) = await relay.CallAsync(server!, HttpMethod.Post, "/api/v1/state/move", Payload(connection!, dryRun: false), cancellationToken);
        return StatusCode(code is >= 200 and < 600 ? code : 502, body ?? new JsonObject { ["error"] = "No answer." });
    }

    /// <summary>The progress of the move on the server, as it reports it.</summary>
    [HttpGet("/api/state-moves/{server}")]
    public async Task<IActionResult> Progress(string server, CancellationToken cancellationToken)
    {
        if (instances.Registry.Find(server) is not { } found) return NotFound(new { error = "That server is not registered here." });
        var (code, body) = await relay.CallAsync(found, HttpMethod.Get, "/api/v1/state/move", null, cancellationToken);
        return StatusCode(code is >= 200 and < 600 ? code : 502, body ?? new JsonObject { ["error"] = "No answer." });
    }

    private static object Payload(StateConnection connection, bool dryRun) => new { engine = connection.Engine, connection = connection.Connection, sqlite_path = connection.Path, dry_run = dryRun };

    private static string OldServer(string program, StateConnection connection) =>
        $"This server is too old to move its state from the console. On its machine, stop it and run: {program} state move --to-engine {connection.Engine} --to-connection \"{connection.Connection}\" --yes " +
        $"(set the password there first with '{program} secret set {ConsoleStateConnections.SecretNameOf(connection.Name)}').";

    private (RegisteredInstance? Server, StateConnection? Connection, IActionResult? Problem) Resolve(StateMoveRequest request)
    {
        if (instances.Registry.Find(request.Server ?? "") is not { } server) return (null, null, NotFound(new { error = "That server is not registered here." }));
        if (connections.Find(request.Connection ?? "") is not { } connection) return (null, null, NotFound(new { error = "No such state connection." }));
        return (server, connection, null);
    }

    private async Task<DbDataKit.State.Cli.StateConnectionReport> TestAsync(StateConnection connection)
    {
        // the checks open connections: not on the request thread, and not for longer than a person will wait
        var run = Task.Run(() => connections.Test(connection));
        if (await Task.WhenAny(run, Task.Delay(CheckTimeout)) == run) return await run;
        return new DbDataKit.State.Cli.StateConnectionReport(connection.Engine, false, [], "The database did not answer in time: check the host and port, and that the database server is running.", false, false, []);
    }

    private sealed record ServerUse(string Id, string Name, string Product, string Identity);

    /// <summary>The state database each paired server (and this console) reports using, as an identity comparable with a connection's.</summary>
    private async Task<List<ServerUse>> ServerStatesAsync(CancellationToken cancellationToken)
    {
        var uses = new List<ServerUse>();
        try
        {
            var own = ConsoleState.Resolve(settings.Directory).Settings;
            uses.Add(new ServerUse("", "this console", ConsoleSettings.Product, ConsoleStateConnections.IdentityOf(own.EngineId, own.ConnectionString, settings.StatePath)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* the console's own settings are not readable: it uses nothing we can name */ }

        var asked = await Task.WhenAll(instances.All().Select(async instance =>
        {
            var status = await instances.StatusAsync(instance, cancellationToken);
            if (!status.Reachable || status.Product is null || !Programs.ContainsKey(status.Product)) return null;
            var (code, state) = await relay.CallAsync(instance, HttpMethod.Get, "/api/v1/state", null, cancellationToken);
            if (code != 200 || state?["engine"]?.GetValue<string>() is not { } engine) return null;
            return new ServerUse(instance.InstanceId, instance.Name, status.Product, ConsoleStateConnections.IdentityOf(engine, state["connection"]?.GetValue<string>(), state["file"]?.GetValue<string>()));
        }));
        uses.AddRange(asked.OfType<ServerUse>());
        return uses;
    }
}
