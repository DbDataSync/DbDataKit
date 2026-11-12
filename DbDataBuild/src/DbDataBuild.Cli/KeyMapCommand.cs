using System.Data;
using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataKit.Dialect;

namespace DbDataBuild.Cli;

/// <summary>A key of the project that is kept in a map (`method: key_map`): where its map is and how many arguments a business key of it has.</summary>
internal sealed record MappedKey(string Name, string OwnerModel, string Schema, int Parts, int Line);

/// <summary>Where the map of a key stands: whether the table is there, how many rows it has, the largest integer and where the identity is.</summary>
internal sealed record KeyMapState(bool Exists, long Rows, long? MaxValue, long? IdentityLast)
{
    /// <summary>True when the identity would hand out an integer that is already in the map (or cannot be told, which is treated the same way).</summary>
    public bool IdentityBelowMaximum => Rows > 0 && MaxValue is { } max && (IdentityLast ?? 0) < max;
}

/// <summary>A column that holds the key's integers and how many rows of it have a given one: null when the count could not be made.</summary>
internal sealed record KeyMapUse(string Table, string Column, long? Rows, string? Error);

/// <summary>
/// `dbdatabuild connection key-map show` and `repair` (plan 25 K10, owner decision of 2026-11-12). A key kept in a map is written only by the Build write login, through the load scripts, and is never retired; this is the one other way
/// to change a map, an administrator's repair in an emergency, a recovery or after a bug. `show` reads the map (counts always, the business keys with `--show-values`, as `connection check` does). `repair` does one explicit operation
/// (<see cref="KeyMapOperation"/>): it is a dry run unless `--apply` says otherwise, it needs `--reason` to apply, and it writes in one transaction the change and a row of the repair log beside the map
/// (`marts.customer_key_map_repair`: who, when, the operation, before and after). It touches no table but the named key's map and its log, and no row of the map but the one it names.
/// </summary>
internal static class KeyMapCommand
{
    public const int DefaultLimit = 20;

    // ---------------------------------------------------------------- the key

    /// <summary>The key a person named: its function name, or the name of the version key of a key with history. A hash key has no map to show or repair.</summary>
    internal static (MappedKey? Key, string? Problem) FindKey(ProjectContext ctx, string name, string connection)
    {
        var mapped = new List<string>();
        foreach (var built in ctx.Project.BuiltModels)
            foreach (var key in built.Definition.Keys)
            {
                var versionKey = key.VersionKey;
                foreach (var candidate in new[] { key.Name, versionKey }.Where(n => n != null))
                {
                    var parts = candidate == key.Name ? key.From.Count : key.From.Count + (key.History == null ? 0 : 1);
                    if (key.Method == KeyMethods.KeyMap) mapped.Add(candidate!);
                    if (!string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.Method != KeyMethods.KeyMap)
                        return (null, $"`{candidate}` is a hash key (`method: hash`): it has no map. Only a key declared `method: key_map` has one.");
                    if (!ctx.TargetsOf(built.Definition).Contains(connection, StringComparer.Ordinal))
                        return (null, $"`{built.Definition.Name}`, which owns `{candidate}`, is not built on the connection `{connection}`, so its map is not there.");
                    return (new MappedKey(candidate!, built.Definition.Name, SurrogateKey.KeyMapTable(built.Definition.Name, candidate!).Schema, parts, key.Line), null);
                }
            }
        return (null, $"`{name}` is not a key of this project that is kept in a map. " + (mapped.Count == 0 ? "The project has none (`keys:` with `method: key_map`)." : $"The keys kept in a map: {string.Join(", ", mapped.Distinct().Order(StringComparer.Ordinal))}."));
    }

    /// <summary>The business key text a person typed: the exact stored text (`--text`), or its parts (`--part`, repeated), written as the key writes them: spaces at the ends removed, joined by U+001F.</summary>
    internal static (string? Text, string? Problem) BusinessKeyText(string? text, string[] parts, MappedKey key)
    {
        if (text != null && parts.Length > 0) return (null, "Give the business key as --text (the stored text exactly) or as --part (once per part), not both.");
        if (parts.Length > 0)
        {
            if (parts.Length != key.Parts) return (null, $"`{key.Name}` has {key.Parts} part(s) in its business key; --part was given {parts.Length} time(s).");
            return (string.Join(SurrogateKey.Separator, parts.Select(p => SurrogateKey.PartText(p)!)), null);
        }
        return (text, null);
    }

    /// <summary>How a business key reads on a screen: its parts, separated by ` | ` (U+001F does not print on every console).</summary>
    internal static string Display(string text) => text.Replace(SurrogateKey.Separator.ToString(), " | ");

    private static string[] Split(string text) => text.Split(SurrogateKey.Separator);

    // ---------------------------------------------------------------- reading

    internal static async Task<KeyMapState> ReadStateAsync(ReadSession read, KeyMapTables tables, CancellationToken ct = default)
    {
        var exists = Number(await read.QueryAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
            [new("schema", DbType.String, tables.Schema), new("table", DbType.String, tables.Key + KeyMapTables.MapSuffix)], ct)) > 0;
        if (!exists) return new KeyMapState(false, 0, null, null);
        var summary = (await read.QueryAsync($"SELECT COUNT(*), MAX({tables.Col("key_value")}) FROM {tables.Map}", null, ct, 0))[0];
        var quoted = tables.Map;
        var identity = tables.Engine == "sqlserver"
            ? await read.QueryAsync("SELECT CAST(IDENT_CURRENT(@name) AS BIGINT)", [new("name", DbType.String, quoted)], ct)
            : await read.QueryAsync("SELECT pg_sequence_last_value(pg_get_serial_sequence(@name, 'key_value')::regclass)", [new("name", DbType.String, quoted)], ct);
        return new KeyMapState(true, Convert.ToInt64(summary[0], CultureInfo.InvariantCulture), summary[1] is null or DBNull ? null : Convert.ToInt64(summary[1], CultureInfo.InvariantCulture), identity[0][0] is null or DBNull ? null : Convert.ToInt64(identity[0][0], CultureInfo.InvariantCulture));
    }

    private static long Number(IReadOnlyList<IReadOnlyList<object?>> rows) => Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);

    private static async Task<string?> TextOfValueAsync(ReadSession read, KeyMapTables t, long value, CancellationToken ct = default)
    {
        var rows = await read.QueryAsync($"SELECT {t.Col("key_text")} FROM {t.Map} WHERE {t.Col("key_value")} = @value", [new("value", DbType.Int64, value)], ct);
        return rows.Count == 0 ? null : Convert.ToString(rows[0][0], CultureInfo.InvariantCulture);
    }

    private static async Task<long?> ValueOfTextAsync(ReadSession read, KeyMapTables t, string text, CancellationToken ct = default)
    {
        var rows = await read.QueryAsync($"SELECT {t.Col("key_value")} FROM {t.Map} WHERE {t.Col("key_text")} = @text", [new("text", DbType.String, text)], ct);
        return rows.Count == 0 ? null : Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where the integers of a key are used: the tables the check can find. The names it looks for are the key's own and the columns the project's queries select it as (a role, `order_date_key` for `date_key`); a column of those names and
    /// of type BIGINT, in a base table of the connection, is counted for the value. A table the project does not know and a column named otherwise are not found, which the output says.
    /// </summary>
    internal static async Task<IReadOnlyList<KeyMapUse>> FindUsesAsync(ReadSession read, KeyMapTables tables, ProjectContext ctx, string connection, MappedKey key, long value, CancellationToken ct = default)
    {
        var names = new HashSet<string>([key.Name], StringComparer.OrdinalIgnoreCase);
        var queries = new List<(ModelDefinition Model, string Sql)>();
        foreach (var built in ctx.Project.BuiltModels.Where(b => ctx.TargetsOf(b.Definition).Contains(connection, StringComparer.Ordinal)))
        {
            try { queries.Add((built.Definition, built.ReadQuery(ctx.Root, ctx.Config, connection))); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { /* the project's own checks say why a model has no query */ }
        }
        foreach (var r in KeyRelationships.Infer(queries, ctx.Project.BuiltModels.Select(b => b.Definition)).Where(r => string.Equals(r.Key, key.Name, StringComparison.OrdinalIgnoreCase))) names.Add(r.Column);

        var parameters = names.Select((n, i) => new GateParameter("n" + i, DbType.String, n.ToLowerInvariant())).ToList();
        var columns = await read.QueryAsync(
            "SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS c JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME " +
            $"WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.DATA_TYPE = 'bigint' AND LOWER(c.COLUMN_NAME) IN ({string.Join(", ", parameters.Select(p => "@" + p.Name))}) ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME", parameters, ct);
        var uses = new List<KeyMapUse>();
        foreach (var row in columns)
        {
            var (schema, table, column) = (Convert.ToString(row[0], CultureInfo.InvariantCulture)!, Convert.ToString(row[1], CultureInfo.InvariantCulture)!, Convert.ToString(row[2], CultureInfo.InvariantCulture)!);
            if (string.Equals(schema, tables.Schema, StringComparison.Ordinal) && (table == key.Name + KeyMapTables.MapSuffix || table == key.Name + KeyMapTables.LogSuffix)) continue;
            var name = schema + "." + table;
            try
            {
                var count = await read.QueryAsync($"SELECT COUNT(*) FROM {SharedQuoting.Quote(tables.Engine, schema)}.{SharedQuoting.Quote(tables.Engine, table)} WHERE {SharedQuoting.Quote(tables.Engine, column)} = @value", [new("value", DbType.Int64, value)], ct, 0);
                uses.Add(new KeyMapUse(name, column, Number(count), null));
            }
            catch (GateRefusedException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException) { uses.Add(new KeyMapUse(name, column, null, Failures.Describe(ex))); }
        }
        return uses;
    }

    // ---------------------------------------------------------------- the shared start of both commands

    private sealed record Prepared(ProjectContext Context, ConnectionConfig Connection, MappedKey Key, KeyMapTables Tables);

    private static Prepared? Prepare(string root, string? targetArg, string? keyArg, bool writes, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(keyArg)) { error.WriteLine("The key is required: `key-map [connection] <key>`, the name of a key declared `method: key_map`."); return null; }
        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error, readOnlyOk: !writes);
        if (connection == null) return null;
        if (!connection.Kind.IsBuildTarget || connection.Engine is not ("sqlserver" or "postgres"))
        {
            error.WriteLine($"A key map is kept on SQL Server and PostgreSQL; `{connection.Name}` is {connection.Kind.Describe()} ({connection.Engine}), where the key map is not built (a DuckDB or Fabric target has none).");
            return null;
        }
        var (key, problem) = FindKey(ctx, keyArg, connection.Name);
        if (key == null) { error.WriteLine(problem!); return null; }
        return new Prepared(ctx, connection, key, new KeyMapTables(connection.Engine, key.Schema, key.Name));
    }

    // ---------------------------------------------------------------- show

    public static int Show(CommandSpec spec, string root, string? targetArg, string? keyArg, long? value, string? text, string[] parts, string? contains, bool showValues, int limit, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (limit < 0) { error.WriteLine("--limit must not be negative."); return CliApp.ExitUsage; }
        if (Prepare(root, targetArg, keyArg, writes: false, error) is not { } p) return CliApp.ExitUsage;
        var (filterText, textProblem) = BusinessKeyText(text, parts, p.Key);
        if (textProblem != null) { error.WriteLine(textProblem); return CliApp.ExitUsage; }
        var (read, missing) = LoginSettings.FromEnvironment(p.Connection, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {p.Connection.Name}  |  login: {read?.Describe() ?? "none"}  |  values: {(showValues ? "shown (requested)" : "not read")}");
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }

        var t = p.Tables;
        var where = new List<string>();
        var parameters = new List<GateParameter>();
        if (value != null) { where.Add($"{t.Col("key_value")} = @value"); parameters.Add(new("value", DbType.Int64, value.Value)); }
        if (filterText != null) { where.Add($"{t.Col("key_text")} = @text"); parameters.Add(new("text", DbType.String, filterText)); }
        if (!string.IsNullOrEmpty(contains))
        {
            where.Add(t.Engine == "sqlserver" ? $"CHARINDEX(@contains, {t.Col("key_text")}) > 0" : $"strpos({t.Col("key_text")}, @contains) > 0");
            parameters.Add(new("contains", DbType.String, contains));
        }
        var clause = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);

        try
        {
            return Task.Run(async () =>
            {
                await using var session = await ReadSession.OpenAsync(read!);
                var state = await ReadStateAsync(session, t);
                output.Payload("connection", p.Connection.Name);
                output.Payload("key", p.Key.Name);
                output.Payload("owner", p.Key.OwnerModel);
                output.Payload("table", t.MapName);
                output.Payload("exists", state.Exists);
                if (!state.Exists)
                {
                    output.WriteLine($"The map `{t.MapName}` does not exist: no load that calls `{p.Key.Name}` has run on `{p.Connection.Name}` yet.");
                    output.Payload("rows", 0); output.Payload("matching", 0);
                    return CliApp.ExitOk;
                }
                var matching = where.Count == 0 ? state.Rows : Number(await session.QueryAsync($"SELECT COUNT(*) FROM {t.Map}{clause}", parameters, default, 0));
                output.Payload("rows", state.Rows);
                output.Payload("matching", matching);
                output.Payload("max_value", state.MaxValue);
                output.Payload("identity_last", state.IdentityLast);
                output.Payload("identity_below_maximum", state.IdentityBelowMaximum);
                output.Payload("filtered", where.Count > 0);
                output.WriteLine($"{t.MapName}: {state.Rows} mapping(s), largest integer {state.MaxValue?.ToString(CultureInfo.InvariantCulture) ?? "none"}, identity at {state.IdentityLast?.ToString(CultureInfo.InvariantCulture) ?? "none"}{(state.IdentityBelowMaximum ? " (BELOW the largest integer: `connection key-map repair --operation reseed` raises it)" : "")}.");
                if (where.Count > 0) output.WriteLine($"{matching} mapping(s) match the filter.");

                if (!showValues || limit == 0)
                {
                    output.Payload("entries", null);
                    if (matching > 0 && !showValues) output.WriteLine($"The business keys are not read: add --show-values to list them (up to --limit, {limit}).");
                    return CliApp.ExitOk;
                }
                // the limit is a number of ours, so it is written into the text; the values of the filter are bound
                var top = t.Engine == "sqlserver" ? $"TOP ({limit.ToString(CultureInfo.InvariantCulture)}) " : "";
                var tail = t.Engine == "sqlserver" ? "" : $" LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
                var rows = await session.QueryAsync($"SELECT {top}{t.Col("key_value")}, {t.Col("key_text")} FROM {t.Map}{clause} ORDER BY {t.Col("key_value")}{tail}", parameters, default, 0);
                var entries = rows.Select(r => (Value: Convert.ToInt64(r[0], CultureInfo.InvariantCulture), Text: Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? "")).ToList();
                output.Payload("entries", entries.Select(e => new { value = e.Value, text = e.Text, parts = Split(e.Text) }).ToList());
                foreach (var e in entries) output.WriteLine($"  {e.Value,12}  {Display(e.Text)}");
                if (matching > entries.Count) output.WriteLine($"  ... {matching - entries.Count} more (--limit {limit}).");
                return CliApp.ExitOk;
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
        catch (Exception ex) when (ex is not OperationCanceledException) { error.WriteLine($"The map could not be read: {Failures.Describe(ex)}."); return CliApp.ExitFindings; }
    }

    // ---------------------------------------------------------------- repair

    /// <summary>
    /// Whether the operation may go on, from what was read: a refusal (the reason, exit 1), a note that there is nothing to do (exit 0, nothing written), or neither. Pure, so every case is a unit test.
    /// </summary>
    internal static (string? Refusal, string? NothingToDo) Check(KeyMapOperation operation, string engine, KeyMapState state, long? value, string? text, string? textOfValue, long? valueOfText, IReadOnlyList<KeyMapUse> uses, bool force, string keyName)
    {
        if (!state.Exists) return ($"The map of `{keyName}` does not exist: no load that calls it has run on this connection, so there is nothing to repair.", null);
        if (operation == KeyMapOperation.Reseed)
            return state.IdentityBelowMaximum ? (null, null) : (null, state.Rows == 0 ? "The map is empty: its identity is left alone." : $"The identity ({state.IdentityLast}) is already at or above the largest integer ({state.MaxValue}): nothing to do.");

        if (value is null or < 1) return ("--value is required and is 1 or more: 0 is the reserved unknown member, which is not in a map.", null);
        if (operation is KeyMapOperation.Add or KeyMapOperation.SetText && string.IsNullOrEmpty(text)) return ("The business key is required: --text (the stored text exactly) or --part (once per part).", null);
        if (text is { Length: > KeyMapRepairScript.MaxTextSqlServer } && engine == "sqlserver") return ($"The business key is {text.Length} characters; SQL Server's map holds {KeyMapRepairScript.MaxTextSqlServer}.", null);

        switch (operation)
        {
            case KeyMapOperation.Add:
                if (textOfValue != null) return ($"The integer {value} already belongs to a business key; `add` never changes a mapping (use `set-text` to correct one).", null);
                if (valueOfText != null) return ($"That business key already has the integer {valueOfText}; a business key has one integer.", null);
                return (null, null);
            case KeyMapOperation.SetText:
                if (textOfValue == null) return ($"No mapping has the integer {value}: there is no text to change (use `add` for a mapping that is missing).", null);
                if (textOfValue == text) return (null, $"The integer {value} already has that business key: nothing to do.");
                if (valueOfText != null) return ($"That business key already has the integer {valueOfText}; two integers cannot have one business key.", null);
                return (null, null);
            default:
                if (textOfValue == null) return ($"No mapping has the integer {value}: nothing to remove.", null);
                var used = uses.Where(u => u.Rows is null or > 0).ToList();
                if (used.Count > 0 && !force)
                    return ($"The integer {value} is in use: {string.Join("; ", used.Select(u => u.Rows is { } n ? $"{u.Table}.{u.Column} ({n} row(s))" : $"{u.Table}.{u.Column} (could not be checked: {u.Error})"))}. A fact row would be left with a key that no business key has. Remove those rows first, or repeat with --force when this is the intended repair.", null);
                return (null, null);
        }
    }

    public static int Repair(CommandSpec spec, string root, string? targetArg, string? keyArg, string? operationName, long? value, string? text, string[] parts, string? reason, bool force, bool showValues, bool apply, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (KeyMapOperations.Parse(operationName) is not { } operation) { error.WriteLine($"--operation is required: one of {string.Join(", ", KeyMapOperations.Names)}."); return CliApp.ExitUsage; }
        if (apply && string.IsNullOrWhiteSpace(reason)) { error.WriteLine("--reason is required with --apply: a repair is recorded with who made it and why."); return CliApp.ExitUsage; }
        if (operation == KeyMapOperation.Reseed && (value != null || text != null || parts.Length > 0)) { error.WriteLine("`reseed` takes no --value, --text or --part: it raises the identity to the largest integer of the map."); return CliApp.ExitUsage; }
        if (operation == KeyMapOperation.Remove && (text != null || parts.Length > 0)) { error.WriteLine("`remove` names the mapping by --value only."); return CliApp.ExitUsage; }
        if (Prepare(root, targetArg, keyArg, writes: true, error) is not { } p) return CliApp.ExitUsage;
        var (businessKey, textProblem) = BusinessKeyText(text, parts, p.Key);
        if (textProblem != null) { error.WriteLine(textProblem); return CliApp.ExitUsage; }

        var (read, readMissing) = LoginSettings.FromEnvironment(p.Connection, Login.Read, env);
        LoginSettings? write = null;
        Diagnostic? writeMissing = null;
        if (apply) (write, writeMissing) = LoginSettings.FromEnvironment(p.Connection, Login.Write, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {p.Connection.Name}  |  login: read {read?.Describe() ?? "none"}; write {(apply ? write?.Describe() ?? "none" : "none (not applying)")}  |  values: {(showValues ? "shown (requested)" : "not read")}");
        if (readMissing != null) error.Diag(readMissing);
        if (writeMissing != null) error.Diag(writeMissing);
        if (read == null || (apply && write == null)) return CliApp.ExitFindings;

        var t = p.Tables;
        var actor = Actor.Of(env, write?.User ?? Environment.UserName);
        try
        {
            return Task.Run(async () =>
            {
                await using var session = await ReadSession.OpenAsync(read);
                var state = await ReadStateAsync(session, t);
                string? textOfValue = null; long? valueOfText = null;
                IReadOnlyList<KeyMapUse> uses = [];
                if (state.Exists && operation != KeyMapOperation.Reseed && value is >= 1)
                {
                    textOfValue = await TextOfValueAsync(session, t, value.Value);
                    if (businessKey != null) valueOfText = await ValueOfTextAsync(session, t, businessKey);
                    // the integers a fact table holds: counted for a removal (which refuses them) and for a change of text (which keeps them pointing at a different business key); an addition has none yet
                    if (textOfValue != null && operation is KeyMapOperation.Remove or KeyMapOperation.SetText) uses = await FindUsesAsync(session, t, p.Context, p.Connection.Name, p.Key, value.Value);
                }

                output.Payload("connection", p.Connection.Name);
                output.Payload("key", p.Key.Name);
                output.Payload("table", t.MapName);
                output.Payload("log_table", t.LogName);
                output.Payload("operation", operation.Name());
                output.Payload("value", value);
                output.Payload("forced", force);
                output.Payload("reason", reason);
                output.Payload("actor", actor);
                output.Payload("applied", false);

                var (refusal, nothing) = Check(operation, t.Engine, state, value, businessKey, textOfValue, valueOfText, uses, force, p.Key.Name);
                output.Payload("uses", uses.Select(u => new { table = u.Table, column = u.Column, rows = u.Rows, error = u.Error }).ToList());
                if (refusal != null) { output.Payload("refused", refusal); error.WriteLine(refusal + " Nothing was changed."); return CliApp.ExitFindings; }
                if (nothing != null) { output.Payload("changes", Array.Empty<object>()); output.WriteLine(nothing); return CliApp.ExitOk; }

                var changes = Describe(operation, t, state, value, businessKey, textOfValue, showValues);
                output.Payload("changes", changes.Payload);
                output.WriteLine($"{(apply ? "Repair" : "Dry run")}: {operation.Name()} on `{t.MapName}`{(operation == KeyMapOperation.Reseed ? "" : $", integer {value}")}.");
                foreach (var line in changes.Lines) output.WriteLine("  " + line);
                foreach (var u in uses.Where(u => u.Rows is > 0)) output.WriteLine($"  in use: {u.Table}.{u.Column} has {u.Rows} row(s) with this integer{(operation == KeyMapOperation.SetText ? ", which will mean the new business key" : force ? " (--force)" : "")}.");
                foreach (var u in uses.Where(u => u.Rows is null)) output.WriteLine($"  could not check {u.Table}.{u.Column}: {u.Error}.");
                if (operation is KeyMapOperation.Remove or KeyMapOperation.SetText) output.WriteLine("  checked: columns named like the key or selected as it by the project's queries, of type BIGINT, in the base tables of this connection; a table or column outside that is not found.");

                var script = KeyMapRepairScript.Build(t.Engine, t, operation, value, businessKey, force, actor, Guid.Empty, reason);
                if (!apply)
                {
                    output.WriteLine();
                    output.WriteLine($"The statements (one transaction; the values are bound, not written here):");
                    foreach (var line in script.Text.TrimEnd().Split('\n')) output.WriteLine("  " + line);
                    output.WriteLine();
                    output.WriteLine($"Nothing was executed. Repeat with --apply --reason \"why\" and the write login configured to make the change and record it in `{t.LogName}`.");
                    output.Next($"connection key-map repair {p.Connection.Name} {p.Key.Name} --operation {operation.Name()}{Echo(value, text, parts)} --apply --reason \"...\"");
                    return CliApp.ExitOk;
                }

                var runId = Guid.NewGuid();
                script = KeyMapRepairScript.Build(t.Engine, t, operation, value, businessKey, force, actor, runId, reason);
                using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), spec.Name, runId);
                output.WriteLine($"Statement log: {Path.GetRelativePath(root, log.Path)}");
                try
                {
                    await using var gate = await MutationGate.OpenAsync(write!, spec.Name, StatementKind.Ddl | StatementKind.Data, log, runId);
                    await gate.ExecuteAsync(GateStatement.FromPlanStep("key-map-repair:" + operation.Name(), StatementKind.Ddl, script.Text, script.Parameters));
                }
                catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the transaction rolled back; the driver's own message is never echoed (it can quote data)
                    error.WriteLine($"The repair was rolled back and nothing changed: {Failures.Describe(ex)}. A guard that fails (a division by zero) means the map is no longer as it was read: show it again.");
                    return CliApp.ExitFindings;
                }

                var after = await ReadStateAsync(session, t);
                var logged = await session.QueryAsync($"SELECT {t.Col("repair_id")} FROM {t.Log} WHERE {t.Col("run_id")} = @run ORDER BY {t.Col("repair_id")}", [new("run", DbType.String, runId.ToString("D"))]);
                output.Payload("applied", true);
                output.Payload("run_id", runId.ToString("D"));
                output.Payload("log_rows", logged.Select(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)).ToList());
                output.Payload("rows_after", after.Rows);
                output.Payload("identity_last_after", after.IdentityLast);
                output.WriteLine($"Applied. The map now has {after.Rows} mapping(s), largest integer {after.MaxValue?.ToString(CultureInfo.InvariantCulture) ?? "none"}, identity at {after.IdentityLast?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
                output.WriteLine($"Recorded in `{t.LogName}` as {actor}: repair {string.Join(", ", logged.Select(r => r[0]))} (run {runId:D}, {operation.Name()}{(force ? ", forced" : "")}).");
                output.Next($"connection key-map show {p.Connection.Name} {p.Key.Name}", $"connection check {p.Connection.Name}");
                return CliApp.ExitOk;
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
        catch (Exception ex) when (ex is not OperationCanceledException) { error.WriteLine($"The map could not be read: {Failures.Describe(ex)}."); return CliApp.ExitFindings; }
    }

    private static string Echo(long? value, string? text, string[] parts) =>
        (value != null ? $" --value {value}" : "") + (text != null ? $" --text \"{text}\"" : "") + string.Concat(parts.Select(p => $" --part \"{p}\""));

    /// <summary>What a repair would change, in words and as data. A business key is shown only when values were asked for; the integers always are (they are not data).</summary>
    private static (IReadOnlyList<string> Lines, object Payload) Describe(KeyMapOperation operation, KeyMapTables t, KeyMapState state, long? value, string? text, string? textOfValue, bool showValues)
    {
        string Shown(string? s) => s == null ? "" : showValues ? Display(s) : $"({s.Length} characters, not shown)";
        switch (operation)
        {
            case KeyMapOperation.Add:
                return ([$"adds 1 mapping: {value} -> {Shown(text)}"], new[] { new { value, text_before = (string?)null, text_after = showValues ? text : null } });
            case KeyMapOperation.SetText:
                return ([$"changes 1 mapping: {value}: {Shown(textOfValue)} -> {Shown(text)}"], new[] { new { value, text_before = showValues ? textOfValue : null, text_after = showValues ? text : null } });
            case KeyMapOperation.Remove:
                return ([$"deletes 1 mapping: {value} -> {Shown(textOfValue)}"], new[] { new { value, text_before = showValues ? textOfValue : null, text_after = (string?)null } });
            default:
                return ([$"raises the identity from {state.IdentityLast} to {state.MaxValue}: no mapping changes; the next integer given is {state.MaxValue + 1}"], new[] { new { value = state.MaxValue, text_before = (string?)null, text_after = (string?)null } });
        }
    }
}
