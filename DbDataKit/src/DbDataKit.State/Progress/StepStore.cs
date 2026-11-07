using System.Data.Common;
using System.Text;
using System.Text.Json;

namespace DbDataKit.State;

/// <summary>What a step row says. <see cref="InDoubt"/> is state <c>in_doubt</c>, or a <c>started</c> row of an earlier attempt: the body began and nothing says whether it finished.</summary>
public static class StepState
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string InDoubt = "in_doubt";
}

/// <summary>One step of an attempt chain as stored. A completed step has <see cref="ResultJson"/> or <see cref="ResultPayload"/>.</summary>
public sealed record StepRecord(
    Guid Id, string Chain, string Name, string Key, string State, int Attempt, string? ResultJson, Guid? ResultPayload,
    IReadOnlyDictionary<string, long> Counts, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error, long Version);

/// <summary>What <see cref="StepStore.Begin"/> found: <see cref="Reuse"/> (the step completed in this chain), <see cref="Run"/> (new, or failed before) or <see cref="InDoubt"/> (it started and never completed).</summary>
public abstract record StepBegin
{
    private StepBegin()
    {
    }

    /// <summary>The step already completed in this chain: use its stored result; the body does not run.</summary>
    public sealed record Reuse(StepRecord Step) : StepBegin
    {
        public string? ResultJson => Step.ResultJson;

        public Guid? ResultPayload => Step.ResultPayload;
    }

    /// <summary>Run the body, then <see cref="StepStore.CompleteValue"/>, <see cref="StepStore.CompleteStream"/> or <see cref="StepStore.Fail"/>. <paramref name="StepId"/> owns a stream's payload (<see cref="StepStore.OpenStream"/>).</summary>
    public sealed record Run(Guid StepId) : StepBegin;

    /// <summary>An earlier attempt began the step and did not complete or fail it. Only the caller knows whether the work happened: <see cref="StepStore.Retake"/> to run it again, <see cref="StepStore.CompleteValue"/> after a
    /// <see cref="StepStore.Retake"/> when a check found the result, or the operator's <see cref="StepStore.Resolve"/>.</summary>
    public sealed record InDoubt(StepRecord Step) : StepBegin;
}

/// <summary>Limits of the steps of one store.</summary>
/// <param name="MaxResultBytes">The most a value result may hold as JSON (<c>step.max_result_bytes</c>, 64 KiB by default).</param>
/// <param name="MaxStreamBytes">The most a streamed result may hold; null: no cap.</param>
public sealed record StepOptions(int MaxResultBytes = StepStore.DefaultMaxResultBytes, long? MaxStreamBytes = null);

/// <summary>A value result passed the cap; the message names the step and says to declare a streaming step.</summary>
public sealed class StepResultTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// Steps of an attempt chain in the <c>runner_progress</c> module (plan 28b section 4): memoized by (task, chain, name, key) and never replayed. A step is complete only when its result is stored, a streamed result
/// only when the payload write committed. A step that started and did not complete when a later attempt begins it is <b>in doubt</b>: this store reports it, and the caller (the kit's recovery, or an operator) decides.
/// </summary>
public sealed class StepStore(StateDatabase database, ProgressKey key, StepOptions? options = null)
{
    public const int DefaultMaxResultBytes = 64 * 1024;

    private const string Module = ProgressModule.Name;
    private const string Where = "package = $package AND channel = $channel AND task = $task AND chain = $chain AND step_name = $step_name AND step_key = $step_key";
    private const string Columns = "id, chain, step_name, step_key, state, attempt, result_json, result_payload, counts, started_at, completed_at, error, row_version";

    private readonly StepOptions _options = options ?? new StepOptions();

    private DbCommand Bind(DbCommand cmd, string chain, string name, string stepKey) =>
        cmd.Bind(database, "package", key.Package).Bind(database, "channel", key.Channel).Bind(database, "task", key.Task)
            .Bind(database, "chain", chain).Bind(database, "step_name", name).Bind(database, "step_key", stepKey);

    /// <summary>
    /// Records that the body is about to run, or says what to do instead. A completed step: <see cref="StepBegin.Reuse"/>. A new step, or one that failed: <see cref="StepBegin.Run"/> (recorded <c>started</c> in
    /// <paramref name="attempt"/>). A <c>started</c> or <c>in_doubt</c> step of an earlier attempt: <see cref="StepBegin.InDoubt"/>, and the row is left as it is. Beginning a step that is started in this very
    /// attempt is a mistake (the same key twice, or a step inside a step) and throws.
    /// </summary>
    public StepBegin Begin(string chain, string name, string stepKey, int attempt)
    {
        ArgumentException.ThrowIfNullOrEmpty(chain);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(stepKey);
        return database.Retry<StepBegin>(() =>
        {
            for (var tries = 0; tries < 10; tries++)
            {
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var row = Read(connection, transaction, chain, name, stepKey);
                if (row is null)
                {
                    var id = database.NewId();
                    using var insert = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore(
                        "{table:step_result}", "id, package, channel, task, chain, step_name, step_key, state, attempt, started_at, row_version",
                        "$id, $package, $channel, $task, $chain, $step_name, $step_key, 'started', $attempt, $now, 1", "package, channel, task, chain, step_name, step_key"));
                    Bind(insert, chain, name, stepKey).Bind(database, "id", id).Bind(database, "attempt", (long)attempt).Bind(database, "now", database.Time.GetUtcNow());
                    if (insert.ExecuteNonQuery() != 1) continue;     // another caller got there first: read what it wrote
                    transaction.Commit();
                    return new StepBegin.Run(id);
                }

                switch (row.State)
                {
                    case StepState.Completed:
                        return new StepBegin.Reuse(row);
                    case StepState.Failed:
                        if (Restart(connection, transaction, row, attempt)) { transaction.Commit(); return new StepBegin.Run(row.Id); }
                        continue;
                    case StepState.Started when row.Attempt >= attempt:
                        throw new InvalidOperationException($"The step '{name}' with key '{stepKey}' is already running in attempt {row.Attempt} of this chain; a step runs once per key (is the key the same for two calls, or is one step inside another?).");
                    default:
                        return new StepBegin.InDoubt(row);
                }
            }

            throw new InvalidOperationException($"The step '{name}' with key '{stepKey}' kept changing while it was begun; try again.");
        });
    }

    /// <summary>Takes over an in-doubt step to run it again in <paramref name="attempt"/> (recovery <c>Repeatable</c>, or <c>Check</c> that found nothing). Throws when the step is not in doubt.</summary>
    public StepBegin.Run Retake(string chain, string name, string stepKey, int attempt) =>
        database.Retry(() =>
        {
            for (var tries = 0; tries < 10; tries++)
            {
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var row = Read(connection, transaction, chain, name, stepKey) ?? throw new InvalidOperationException($"There is no step '{name}' with key '{stepKey}' in this chain.");
                if (!IsInDoubt(row, attempt))
                    throw new InvalidOperationException($"The step '{name}' with key '{stepKey}' is {row.State} (attempt {row.Attempt}), not in doubt.");
                if (!Restart(connection, transaction, row, attempt)) continue;
                transaction.Commit();
                return new StepBegin.Run(row.Id);
            }

            throw new InvalidOperationException($"The step '{name}' with key '{stepKey}' kept changing while it was retaken; try again.");
        });

    private static bool IsInDoubt(StepRecord row, int attempt) => row.State == StepState.InDoubt || (row.State == StepState.Started && row.Attempt < attempt);

    private bool Restart(DbConnection connection, DbTransaction transaction, StepRecord row, int attempt)
    {
        using var update = database.Command(Module, connection, transaction,
            "UPDATE {table:step_result} SET state = 'started', attempt = $attempt, started_at = $now, completed_at = NULL, error = NULL, result_json = NULL, result_payload = NULL, counts = NULL, row_version = row_version + 1 WHERE id = $id AND row_version = $version;");
        update.Bind(database, "attempt", (long)attempt).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "id", row.Id).Bind(database, "version", row.Version);
        return update.ExecuteNonQuery() == 1;
    }

    /// <summary>Stores a value result (JSON) and completes the step. Refused over <see cref="StepOptions.MaxResultBytes"/> with a message naming the step; the step stays started, so the caller then fails it.</summary>
    public void CompleteValue(string chain, string name, string stepKey, int attempt, string resultJson, IReadOnlyDictionary<string, long>? counts = null)
    {
        ArgumentNullException.ThrowIfNull(resultJson);
        CheckValue(name, stepKey, resultJson);
        Complete(chain, name, stepKey, attempt, resultJson, null, counts);
    }

    /// <summary>
    /// Completes the step with a streamed result: the payload must be complete (its writer committed). An incomplete payload is refused and the step stays started, which is how an interrupted write reads as
    /// "nothing stored".
    /// </summary>
    public void CompleteStream(string chain, string name, string stepKey, int attempt, Guid payloadId, IReadOnlyDictionary<string, long>? counts = null)
    {
        CheckPayload(name, stepKey, payloadId);
        Complete(chain, name, stepKey, attempt, null, payloadId, counts);
    }

    private void CheckValue(string name, string stepKey, string resultJson)
    {
        var bytes = Encoding.UTF8.GetByteCount(resultJson);
        if (bytes > _options.MaxResultBytes)
            throw new StepResultTooLargeException($"The result of the step '{name}' (key '{stepKey}') is {bytes} bytes as JSON, over the {_options.MaxResultBytes}-byte cap (step.max_result_bytes); declare a streaming step for a result this large.");
        try
        {
            using var _ = JsonDocument.Parse(resultJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The result of the step '{name}' (key '{stepKey}') is not JSON: {ex.Message}", nameof(resultJson), ex);
        }
    }

    private void CheckPayload(string name, string stepKey, Guid payloadId)
    {
        var info = new PayloadStore(database).Get(payloadId);
        if (info is null) throw new InvalidOperationException($"The streamed result of the step '{name}' (key '{stepKey}') is the payload {payloadId}, which does not exist.");
        if (!info.Complete) throw new InvalidOperationException($"The streamed result of the step '{name}' (key '{stepKey}') is the payload {payloadId}, which its writer did not complete; the step is not complete until the write is committed.");
    }

    private void Complete(string chain, string name, string stepKey, int attempt, string? json, Guid? payload, IReadOnlyDictionary<string, long>? counts)
    {
        var done = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"UPDATE {{table:step_result}} SET state = 'completed', result_json = $json, result_payload = $payload, counts = $counts, completed_at = $now, error = NULL, row_version = row_version + 1 WHERE {Where} AND state = 'started' AND attempt = $attempt;");
            Bind(cmd, chain, name, stepKey).Bind(database, "json", json, System.Data.DbType.String).Bind(database, "payload", payload, System.Data.DbType.Guid)
                .Bind(database, "counts", Counts.Write(counts), System.Data.DbType.String).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "attempt", (long)attempt);
            return cmd.ExecuteNonQuery() == 1;
        });
        if (!done) throw new InvalidOperationException($"The step '{name}' with key '{stepKey}' is not started in attempt {attempt} of this chain, so it cannot be completed.");
    }

    /// <summary>Opens the payload writer of a streaming step: owned by the step's row (so retention removes it with the row), capped by <see cref="StepOptions.MaxStreamBytes"/>. Complete the writer, dispose it, then <see cref="CompleteStream"/>.</summary>
    public PayloadWriter OpenStream(StepBegin.Run run, string name, string? mediaType = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new PayloadStore(database).OpenWrite(new PayloadOptions(mediaType, Module, run.StepId.ToString("D"), MaxBytes: _options.MaxStreamBytes, Label: $"step '{name}'"));
    }

    /// <summary>Records that the body failed (an exception). A failed step runs again when the chain is retried. False when the step is not started in this attempt.</summary>
    public bool Fail(string chain, string name, string stepKey, int attempt, string error, IReadOnlyDictionary<string, long>? counts = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"UPDATE {{table:step_result}} SET state = 'failed', error = $error, counts = $counts, completed_at = $now, row_version = row_version + 1 WHERE {Where} AND state = 'started' AND attempt = $attempt;");
            Bind(cmd, chain, name, stepKey).Bind(database, "error", error ?? "").Bind(database, "counts", Counts.Write(counts), System.Data.DbType.String)
                .Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "attempt", (long)attempt);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>
    /// A run died (or was stopped by the runner's restart): every step of the chain still <c>started</c> (in <paramref name="attempt"/> only, when given) becomes <c>in_doubt</c>, so the console shows them before
    /// anyone retries. Returns them. Begin reports such a step in doubt whether or not this was called; this makes the state visible.
    /// </summary>
    public IReadOnlyList<StepRecord> MarkInDoubt(string chain, int? attempt = null)
    {
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"UPDATE {{table:step_result}} SET state = 'in_doubt', row_version = row_version + 1 WHERE package = $package AND channel = $channel AND task = $task AND chain = $chain AND state = 'started'{(attempt is null ? "" : " AND attempt = $attempt")};");
            cmd.Bind(database, "package", key.Package).Bind(database, "channel", key.Channel).Bind(database, "task", key.Task).Bind(database, "chain", chain);
            if (attempt is { } a) cmd.Bind(database, "attempt", (long)a);
            cmd.ExecuteNonQuery();
        });
        return List(chain).Where(s => s.State == StepState.InDoubt).ToList();
    }

    /// <summary>
    /// The operator's answer for an in-doubt step: it <paramref name="happened"/> (then its result, JSON or a complete payload, is stored and the step completes, so a retry reuses it) or it did not (then the step
    /// is recorded failed, so a retry runs it). False when the step is not started or in doubt.
    /// </summary>
    public bool Resolve(string chain, string name, string stepKey, bool happened, string? resultJson = null, Guid? resultPayload = null, string? actor = null)
    {
        if (happened)
        {
            if (resultJson is null && resultPayload is null) throw new ArgumentException($"A step that happened has a result: give the JSON (null for no value) or a payload for '{name}'.", nameof(resultJson));
            if (resultJson is not null && resultPayload is not null) throw new ArgumentException($"A step has a value result or a streamed one, not both ('{name}').", nameof(resultJson));
            if (resultJson is not null) CheckValue(name, stepKey, resultJson);
            else CheckPayload(name, stepKey, resultPayload!.Value);
        }

        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, happened
                ? $"UPDATE {{table:step_result}} SET state = 'completed', result_json = $json, result_payload = $payload, completed_at = $now, error = $note, row_version = row_version + 1 WHERE {Where} AND state IN ('started', 'in_doubt');"
                : $"UPDATE {{table:step_result}} SET state = 'failed', completed_at = $now, error = $note, row_version = row_version + 1 WHERE {Where} AND state IN ('started', 'in_doubt');");
            Bind(cmd, chain, name, stepKey).Bind(database, "now", database.Time.GetUtcNow())
                .Bind(database, "note", $"{(happened ? "an operator said it happened" : "an operator said it did not happen")}{(actor is null ? "" : $" ({actor})")}", System.Data.DbType.String);
            if (happened) cmd.Bind(database, "json", resultJson, System.Data.DbType.String).Bind(database, "payload", resultPayload, System.Data.DbType.Guid);
            return cmd.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>One step, or null.</summary>
    public StepRecord? Get(string chain, string name, string stepKey) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Read(connection, null, chain, name, stepKey);
        });

    /// <summary>Every step of the chain, in the order they started.</summary>
    public IReadOnlyList<StepRecord> List(string chain) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT {Columns} FROM {{table:step_result}} WHERE package = $package AND channel = $channel AND task = $task AND chain = $chain ORDER BY started_at, id;");
            cmd.Bind(database, "package", key.Package).Bind(database, "channel", key.Channel).Bind(database, "task", key.Task).Bind(database, "chain", chain);
            using var reader = cmd.ExecuteReader();
            var steps = new List<StepRecord>();
            while (reader.Read()) steps.Add(Map(reader));
            return (IReadOnlyList<StepRecord>)steps;
        });

    private StepRecord? Read(DbConnection connection, DbTransaction? transaction, string chain, string name, string stepKey)
    {
        using var cmd = database.Command(Module, connection, transaction, $"SELECT {Columns} FROM {{table:step_result}} WHERE {Where};");
        Bind(cmd, chain, name, stepKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static StepRecord Map(DbDataReader r) =>
        new(r.Uuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Int32(5), r.IsDBNull(6) ? null : r.GetString(6), r.NullableUuid(7),
            Counts.Read(r.IsDBNull(8) ? null : r.GetString(8)), r.Time(9), r.NullableTime(10), r.IsDBNull(11) ? null : r.GetString(11), r.Int64(12));
}

/// <summary>The counts a step or unit made, kept as JSON text.</summary>
internal static class Counts
{
    public static string? Write(IReadOnlyDictionary<string, long>? counts) =>
        counts is { Count: > 0 } ? JsonSerializer.Serialize(counts) : null;

    public static IReadOnlyDictionary<string, long> Read(string? json) =>
        json is null ? new Dictionary<string, long>() : JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? [];
}
