using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.State.Cli;
using DbDataKit.State.Engines.Snapshots;
using DbDataKit.State.Snapshots;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>
/// <c>POST /api/v1/state/move</c> and <c>GET /api/v1/state/move</c> (plan 29b, C06): the move of the server's own state, run by the server process on the machine that has the settings file and the secret store. The server has to be in maintenance and idle
/// (the call refuses with 409 otherwise: it never freezes a server itself), the target is named as an engine and a connection whose credentials are secret references the server resolves in its own store (a password written out is refused), the target has to be
/// empty, and <see cref="StateMoveService"/> does the rest (export, import, both verifications, then the settings with the previous file kept as <c>&lt;settings&gt;.before-state-move</c>). It runs in a task of the server; <c>GET</c> reads its progress. It never restarts
/// anything: the answer says how. A failure leaves the settings unchanged, and the old database is never touched. Nothing here puts a secret in an answer.
/// </summary>
internal sealed class StateMoveRunner(StateManagementOptions options, Action<StateCaller, string, string, string, DateTimeOffset> record, TimeProvider clock)
{
    public const string AuditCommand = "state.move";

    private readonly object _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private Run? _run;

    /// <summary>What is known of the move of this process (one at a time; after a success the server needs its restart).</summary>
    private sealed class Run
    {
        public string State { get; set; } = "running";
        public string Step { get; set; } = "preflight";
        public string Message { get; set; } = "Starting.";
        public DateTimeOffset Started { get; init; }
        public DateTimeOffset? Finished { get; set; }
        public required string ToEngine { get; init; }
        public required string ToConnection { get; init; }
        public required string By { get; init; }
        public Dictionary<string, JsonObject> Tables { get; } = new(StringComparer.Ordinal);
        public List<JsonObject> Steps { get; } = [];
        public long Rows { get; set; }
        public int TablesDone { get; set; }
        public int TablesTotal { get; set; }
        public string? Error { get; set; }
        public bool SettingsChanged { get; set; }
        public MoveResult? Result { get; set; }
        public string? KeptSnapshot { get; set; }
    }

    public bool Enabled => options.Move;

    public StateResult Read()
    {
        lock (_lock)
        {
            if (_run is not { } run) return StateResult.Ok(new JsonObject { ["state"] = "none", ["available"] = options.Move });
            return StateResult.Ok(Describe(run));
        }
    }

    private JsonObject Describe(Run run)
    {
        var node = new JsonObject
        {
            ["state"] = run.State,
            ["available"] = options.Move,
            ["step"] = run.Step,
            ["message"] = run.Message,
            ["started"] = run.Started.ToString("O", CultureInfo.InvariantCulture),
            ["finished"] = run.Finished?.ToString("O", CultureInfo.InvariantCulture),
            ["by"] = run.By,
            ["to_engine"] = run.ToEngine,
            ["to_connection"] = run.ToConnection,
            ["rows"] = run.Rows,
            ["tables_done"] = run.TablesDone,
            ["tables_total"] = run.TablesTotal,
            ["settings_changed"] = run.SettingsChanged,
            ["steps"] = new JsonArray([.. run.Steps.Select(s => (JsonNode?)s.DeepClone())]),
            ["tables"] = new JsonArray([.. run.Tables.Values.Select(t => (JsonNode?)t.DeepClone())]),
        };
        if (run.Error is not null)
        {
            node["error"] = run.Error;
            node["kept"] = run.SettingsChanged
                ? "The new settings were written."
                : $"The settings file '{SettingsFile()}' was not changed and the old database was not touched." + (run.KeptSnapshot is not null ? $" The temporary snapshot was kept in '{run.KeptSnapshot}'." : "");
        }

        if (run.Result is { } result)
        {
            node["restart"] = result.RestartInstruction;
            node["way_back"] = result.WayBack;
            node["settings_file"] = result.SettingsFile;
            node["backup_file"] = result.BackupFile;
            node["snapshot_directory"] = result.SnapshotDirectory;
            node["summary"] = result.Summary;
            node["verified_against_snapshot"] = result.VerifiedAgainstSnapshot.Equal;
            node["verified_against_source"] = result.VerifiedAgainstSource.Equal;
            node["tables"] = new JsonArray([.. run.Tables.Values.Select(t => (JsonNode?)t.DeepClone())]);
        }

        return node;
    }

    private string? SettingsFile()
    {
        try { return options.Target().SettingsFile; }
        catch (InvalidOperationException) { return null; }
    }

    public StateResult Start(string? body, StateCaller caller, ServerRole admin)
    {
        var at = clock.GetUtcNow();
        if (!caller.Holds(admin))
        {
            record(caller, AuditCommand, "refused", "not allowed: the admin role is needed", at);
            return StateResult.Problem(403, $"This call needs the {admin.ToString().ToLowerInvariant()} role.");
        }

        StateResult Refuse(int status, string sentence, string outcome = "refused")
        {
            record(caller, AuditCommand, outcome, "refused: " + sentence, at);
            return StateResult.Problem(status, sentence);
        }

        if (!options.Move) return StateResult.Problem(404, "This server does not offer a move of its state from the console: use the command line on the machine (state move).");

        // ---- the body
        JsonNode? parsed;
        try { parsed = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { return Refuse(400, "The body is not JSON."); }
        if (parsed is not JsonObject request) return Refuse(400, "The body is {\"engine\": \"postgres|mssql\", \"connection\": \"... Password=secret:name\", \"dry_run\": false}.");
        foreach (var (key, value) in request)
            if (key is not ("engine" or "connection" or "schema" or "prefix" or "dry_run"))
                return Refuse(400, $"'{key}' is not a setting of the call (engine, connection, schema, prefix, dry_run).");

        string? Text(string key) => request[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var dryRun = request["dry_run"] is JsonValue d && d.TryGetValue<bool>(out var flag) && flag;

        StateEndpoint to;
        try { to = ParseTarget(Text("engine"), Text("connection"), Text("schema"), Text("prefix"), options.Target().Settings); }
        catch (ArgumentException ex) { return Refuse(400, ex.Message); }
        catch (InvalidOperationException ex) { return Refuse(409, "This server's own state settings cannot be read: " + ex.Message); }

        var masked = ConnectionStringReferences.Mask(to.Settings.ConnectionString);

        // ---- the server has to be frozen: maintenance, and idle
        var gate = options.Gate.Status();
        if (!gate.Enabled) return Refuse(409, "The server is not in maintenance: put it into maintenance first (POST /api/v1/state/maintenance), wait until it reports idle, then move.");
        if (!gate.Idle) return Refuse(409, $"The server is in maintenance but not idle yet ({gate.InFlight} still in flight): wait until it reports idle, then move.");

        StateTarget current;
        try { current = options.Target(); }
        catch (InvalidOperationException ex) { return Refuse(409, "This server's own state settings cannot be read: " + ex.Message); }
        if (current.SettingsFile is not { Length: > 0 } settingsFile)
            return Refuse(409, "This server keeps no settings file with a state section that a move can write: use 'state export' and 'state import' on the machine.");

        // ---- the same checks a move starts with (target opens and is empty, not the same database, the file can be written); nothing is changed
        var secrets = SecretsOf(current);
        var service = new StateMoveService(secrets, clock, options.Program);
        var from = new StateEndpoint(current.Settings, current.Settings.IsSqlite ? current.SqlitePath : null);
        try { service.Preflight(from, to, settingsFile, options.Modules, options.Scope); }
        catch (StateServiceException ex)
        {
            var sentence = Redact(ex.Message, to, secrets);
            var named = ex.Reason == StateServiceRefusal.SecretMissing ? sentence + $" Set it in this server's own secret store with '{options.Program} secret set <name>' (the console cannot copy a secret to a server)." : sentence;
            record(caller, AuditCommand, "refused", $"preflight: {to.Engine} {masked}: {named}", at);
            var status = ex.Reason is StateServiceRefusal.TargetNotEmpty or StateServiceRefusal.SameDatabase ? 409 : 400;
            return new StateResult(status, new JsonObject { ["error"] = named, ["reason"] = ex.Reason.ToString().ToLowerInvariant() });
        }

        if (dryRun)
        {
            record(caller, AuditCommand, "ok", $"preflight: {to.Engine} {masked}", at);
            return StateResult.Ok(new JsonObject { ["state"] = "preflight_ok", ["to_engine"] = to.Engine, ["to_connection"] = masked, ["message"] = $"The target ({to.Describe()}) opens, is empty and is not this server's own database; the settings file can take the new section." });
        }

        // ---- start it
        Run run;
        lock (_lock)
        {
            if (_run is { State: "running" })
                return Refuse(409, "A move is already running: read its progress with GET /api/v1/state/move.");
            if (_run is { State: "succeeded" })
                return Refuse(409, "This server's state was already moved; restart it so it opens the new database before another move.");
            run = new Run { Started = at, ToEngine = to.Engine, ToConnection = masked, By = caller.Id };
            _run = run;
        }

        record(caller, AuditCommand, "started", $"{from.Engine} to {to.Engine} {masked}", at);
        var tuning = options.TuneMove;
        _ = Task.Run(() => RunAsync(run, service, from, to, settingsFile, secrets, caller, tuning));
        lock (_lock) return new StateResult(202, Describe(run));
    }

    private async Task RunAsync(Run run, StateMoveService service, StateEndpoint from, StateEndpoint to, string settingsFile, ISecretResolver? secrets, StateCaller caller, Func<MoveOptions, MoveOptions>? tuning)
    {
        var ignored = (options.Instances ?? IInstanceDirectory.Empty).Live().Where(i => i.IsSelf).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var move = new MoveOptions { Export = SnapshotOptions.Default with { IgnoredInstances = ignored, SqliteLockTimeout = TimeSpan.FromSeconds(10) } };
        if (tuning is not null) move = tuning(move);
        var progress = new InlineProgress<MoveProgress>(p => Update(run, p));
        try
        {
            var result = await service.MoveAsync(from, to, settingsFile, options.Modules, options.Scope, move, progress, _stop.Token).ConfigureAwait(false);
            lock (_lock)
            {
                run.Result = result;
                run.SettingsChanged = true;
                run.State = "succeeded";
                run.Step = "done";
                run.Message = Redact("The move is complete. " + result.RestartInstruction, to, secrets);
                run.Finished = clock.GetUtcNow();
            }

            Audit(caller, "ok", $"moved to {to.Engine} {run.ToConnection}; settings '{result.SettingsFile}' written, previous kept as '{result.BackupFile}'");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var text = Redact(ex.Message, to, secrets);
            lock (_lock)
            {
                run.Error = text;
                run.State = "failed";
                run.Finished = clock.GetUtcNow();
                run.Message = "The move failed.";
                if (ex is StateMoveException { SnapshotDirectory: { } kept }) run.KeptSnapshot = kept;
            }

            Audit(caller, "failed", $"move to {to.Engine} {run.ToConnection} stopped: {FirstLine(text)}");
        }
    }

    private void Audit(StateCaller caller, string outcome, string target)
    {
        try { record(caller, AuditCommand, outcome, target, clock.GetUtcNow()); }
        catch (Exception) { /* the move is over; its answer says what happened */ }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private void Update(Run run, MoveProgress p)
    {
        lock (_lock)
        {
            var step = p.Step.ToString().ToLowerInvariant();
            if (run.Step != step || run.Steps.Count == 0)
            {
                run.Steps.Add(new JsonObject { ["step"] = step, ["at"] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), ["message"] = p.Message });
                run.Step = step;
            }

            run.Message = p.Message;
            if (p.Snapshot is { Module: { } module, Table: { } table } snapshot)
            {
                var key = $"{step}/{module}.{table}";
                if (!run.Tables.TryGetValue(key, out var row)) run.Tables[key] = row = new JsonObject { ["step"] = step, ["module"] = module, ["table"] = table, ["rows"] = 0L, ["done"] = false };
                row["rows"] = snapshot.Rows;
                if (snapshot.Phase == "table-done") row["done"] = true;
                run.Rows = Math.Max(run.Rows, snapshot.Rows);
            }
            else if (p.Verification is { } verification)
            {
                run.TablesDone = verification.TablesDone;
                run.TablesTotal = verification.TablesTotal;
                var key = $"{step}/{verification.Module}.{verification.Table}";
                run.Tables[key] = new JsonObject { ["step"] = step, ["module"] = verification.Module, ["table"] = verification.Table, ["done"] = true };
            }
        }
    }

    private static ISecretResolver? SecretsOf(StateTarget target)
    {
        try { return target.Secrets?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Hides what the target's references resolve to (a provider may quote its connection string in a message), whatever the service already did.</summary>
    private static string Redact(string text, StateEndpoint to, ISecretResolver? secrets)
    {
        try
        {
            var resolved = to.Settings.IsSqlite || secrets is null ? null : ConnectionStringReferences.ResolveAsync(to.Settings.ConnectionString!, secrets, ConnectionStringReferences.QuoteDouble).AsTask().GetAwaiter().GetResult();
            if (resolved is not null)
                foreach (var secret in ConnectionStringReferences.CredentialValues(resolved).Where(s => s.Length >= 3).Append(resolved).OrderByDescending(s => s.Length))
                    text = text.Replace(secret, "***", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SecretNotFoundException or InvalidOperationException) { /* nothing resolved, nothing to hide */ }
        return text;
    }

    /// <summary>The target of the body as an endpoint: SQL Server or PostgreSQL, a connection string whose credentials are secret references (a password written out is refused, it would be in the audit and the log).</summary>
    private static StateEndpoint ParseTarget(string? engine, string? connection, string? schema, string? prefix, StateSettings basis)
    {
        if (string.IsNullOrWhiteSpace(engine)) throw new ArgumentException("The engine is needed: mssql or postgres.");
        var settings = new StateSettings(engine, connection) { Namespaces = basis.Namespaces, Retention = basis.Retention };
        try { _ = settings.EngineId; }
        catch (InvalidOperationException ex) { throw new ArgumentException(ex.Message); }
        if (settings.IsSqlite) throw new ArgumentException("A move to SQLite is made on the machine with the command line (state move --to-engine sqlite): a SQLite state is a file on that machine.");
        if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException($"The connection is needed for {engine}: a connection string whose password is a secret reference (Password=secret:state-db).");
        if (ConnectionStringReferences.CredentialValues(connection).Count > 0)
            throw new ArgumentException("The connection holds a password written out: send a secret reference (Password=secret:state-db) that this server resolves in its own store.");
        if (schema is { Length: > 0 } || prefix is { Length: > 0 })
        {
            var current = basis.Namespaces.Default;
            settings = settings with
            {
                Namespaces = new StateNamespaceSettings
                {
                    Default = new NamespaceSetting(schema ?? current.Schema, prefix ?? current.Prefix, current.File),
                    Modules = basis.Namespaces.Modules,
                    Registry = basis.Namespaces.Registry,
                },
            };
        }

        return new StateEndpoint(settings);
    }
}
