using System.Data.Common;
using System.Text.Json.Nodes;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;

namespace DbDataGateway.Server.State;

/// <summary>
/// The <c>semantic_runs</c> module (plan 14a SL23, D21): a record of every statement the Gateway's semantic module answered, kept per group (the same scope as the published models a record names). A record holds who asked
/// (<c>caller</c>: the caller's name as the Gateway knows it, which is not unique across identity providers, so a list read by it is only as exact as the names are), the model and the published version of it, what was written and
/// the request document that ran, the request hash, how it ended (<c>status</c>, <c>error_code</c>, <c>message</c>), the engine's warnings, the plan of an explain, the start, the duration and the number of rows; never the rows.
/// <c>replay_of</c> names the record a replay re-ran. A record older than <see cref="RetentionDays"/> days is removed by the shared retention job. Migrations are appended, never edited.
/// </summary>
public static class SemanticRunsModule
{
    public const string Name = "semantic_runs";

    public const double RetentionDays = 30;

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:run} (
                {{identity:id}},
                caller         {{key}} NOT NULL,
                model          {{key}} NOT NULL,
                model_version  {{int}} NULL,
                kind           {{key}} NOT NULL,
                request_text   {{text}} NOT NULL,
                request_json   {{text}} NULL,
                request_hash   {{key}} NULL,
                status         {{key}} NOT NULL,
                error_code     {{key}} NULL,
                message        {{text}} NULL,
                warnings_json  {{text}} NULL,
                plan_text      {{text}} NULL,
                started_at     {{time}} NOT NULL,
                duration_ms    {{int}} NOT NULL,
                row_count      {{int}} NOT NULL,
                replay_of      {{int}} NULL
            );
            CREATE INDEX {index:run_by_caller} ON {table:run}(caller, started_at);
            CREATE INDEX {index:run_by_start} ON {table:run}(started_at);
            """,
        ],
        retention: [new RetentionRule("run", "started_at", RetentionDays)]);
}

/// <summary>A run as read back.</summary>
public sealed record SemanticRunRecord(long Id, SemanticRun Run);

/// <summary>The records of the semantic module's statements, on the <c>semantic_runs</c> module. Also what the model source writes to (<see cref="ISemanticRuns"/>).</summary>
public sealed class SemanticRunStore(StateDatabase database) : ISemanticRuns
{
    private const string Module = SemanticRunsModule.Name;

    private const string Select = "SELECT id, caller, model, model_version, kind, request_text, request_json, request_hash, status, error_code, message, warnings_json, plan_text, started_at, duration_ms, row_count, replay_of FROM {table:run}";

    public void Record(SemanticRun run) => Add(run);

    /// <summary>Writes the record and answers its id.</summary>
    public long Add(SemanticRun run) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var insert = database.Command(Module, connection, transaction, """
                INSERT INTO {table:run} (caller, model, model_version, kind, request_text, request_json, request_hash, status, error_code, message, warnings_json, plan_text, started_at, duration_ms, row_count, replay_of)
                VALUES ($caller, $model, $version, $kind, $text, $json, $hash, $status, $code, $message, $warnings, $plan, $started, $duration, $rows, $replay);
                """);
            insert.Bind(database, "caller", run.Caller).Bind(database, "model", run.Model).Bind(database, "version", run.ModelVersion).Bind(database, "kind", run.Kind)
                .Bind(database, "text", run.Text).Bind(database, "json", run.Request?.ToJsonString()).Bind(database, "hash", run.RequestHash).Bind(database, "status", run.Status)
                .Bind(database, "code", Clip(run.ErrorCode, 120)).Bind(database, "message", run.Message).Bind(database, "warnings", run.Warnings).Bind(database, "plan", run.Plan)
                .Bind(database, "started", run.StartedAt).Bind(database, "duration", run.DurationMs).Bind(database, "rows", run.Rows).Bind(database, "replay", run.ReplayOf);
            insert.ExecuteNonQuery();
            // the id of the row just written: the newest of this caller (ids rise with time on every engine, and the transaction keeps the lookup to this write)
            using var lookup = database.Command(Module, connection, transaction, "SELECT MAX(id) FROM {table:run} WHERE caller = $caller;");
            lookup.Bind(database, "caller", run.Caller);
            var id = Convert.ToInt64(lookup.ExecuteScalar());
            transaction.Commit();
            return id;
        });

    private static string? Clip(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    /// <summary>The newest records first: the caller's own, or everyone's when <paramref name="all"/> (an administrator's view). <paramref name="model"/> narrows to one model.</summary>
    public IReadOnlyList<SemanticRunRecord> List(string caller, bool all, string? model = null, int limit = 100) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var where = new List<string>();
            if (!all) where.Add("caller = $caller");
            if (model is not null) where.Add("model = $model");
            using var command = database.Command(Module, connection, $"{Select}{(where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))} ORDER BY id DESC;");
            if (!all) command.Bind(database, "caller", caller);
            if (model is not null) command.Bind(database, "model", model);
            return ReadAll(command, Math.Clamp(limit, 1, 1000));
        });

    /// <summary>One record, when it is the caller's own or <paramref name="all"/>; null otherwise (a record of someone else is not found, not refused: its existence is not told).</summary>
    public SemanticRunRecord? Get(long id, string caller, bool all) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"{Select} WHERE id = $id;");
            command.Bind(database, "id", id);
            return ReadAll(command, 1).FirstOrDefault(r => all || string.Equals(r.Run.Caller, caller, StringComparison.Ordinal));
        });

    /// <summary>How many statements ended with each error code since <paramref name="since"/> (the refusals, and the partial results the engine gave a reason for), the caller's own or everyone's.</summary>
    public IReadOnlyDictionary<string, long> CountsByCode(DateTimeOffset since, string caller, bool all) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT error_code, COUNT(*) FROM {{table:run}} WHERE error_code IS NOT NULL AND started_at >= $since{(all ? "" : " AND caller = $caller")} GROUP BY error_code;");
            command.Bind(database, "since", since);
            if (!all) command.Bind(database, "caller", caller);
            using var reader = command.ExecuteReader();
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            while (reader.Read()) counts[reader.GetString(0)] = reader.Int64(1);
            return (IReadOnlyDictionary<string, long>)counts;
        });

    private static List<SemanticRunRecord> ReadAll(DbCommand command, int limit)
    {
        using var reader = command.ExecuteReader();
        var all = new List<SemanticRunRecord>();
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (all.Count < limit && reader.Read())
            all.Add(new SemanticRunRecord(reader.Int64(0), new SemanticRun(reader.GetString(1), reader.GetString(2), reader.NullableInt32(3), reader.GetString(4), reader.GetString(5),
                Text(6) is { } json ? JsonNode.Parse(json) as JsonObject : null, Text(7), reader.GetString(8), Text(9), Text(10), Text(11), Text(12), reader.Time(13), reader.Int64(14), reader.Int64(15), reader.NullableInt64(16))));
        return all;
    }
}
