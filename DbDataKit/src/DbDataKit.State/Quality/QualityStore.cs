using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.State;

/// <summary>The status of an evaluation (plan 23a: <c>pass</c>, <c>fail</c>, <c>error</c>). <see cref="Error"/> is a check that could not run (QF10), not a pass and not a fail.</summary>
public enum CheckOutcome { Pass, Fail, Error }

/// <summary>One evaluation to record. <paramref name="Severity"/> (<c>warn</c> or <c>block</c>) and <paramref name="OnError"/> (<c>alert</c> or <c>block</c>) are the check's own, as the evaluator read them, and decide the block.</summary>
/// <param name="CheckId">The check's id within its definition (<c>check/1</c>).</param>
/// <param name="Dataset">The dataset's or entity's lineage identity.</param>
/// <param name="Measured">The measure in the unit of the kind: failing rows, seconds of age, percent of change.</param>
/// <param name="Threshold">What the check allows, in the same unit.</param>
/// <param name="SampleKeys">Keys of a few of the rows that failed; at most <see cref="QualityStore.MaxSampleKeys"/> are kept.</param>
/// <param name="RunId">The run that made the evaluation (Build's tracking id, a runner run), when there is one.</param>
/// <param name="EvaluatedAt">When it was evaluated, UTC.</param>
/// <param name="Message">Why a check is in error; never a secret.</param>
public sealed record CheckResultRecord(string CheckId, string Dataset, CheckOutcome Outcome, DateTimeOffset EvaluatedAt, decimal? Measured = null, decimal? Threshold = null, IReadOnlyList<string>? SampleKeys = null,
    string? RunId = null, string Severity = "warn", string OnError = "alert", string? Message = null);

/// <summary>A stored evaluation.</summary>
public sealed record StoredCheckResult(Guid Id, string CheckId, string Dataset, CheckOutcome Outcome, DateTimeOffset EvaluatedAt, decimal? Measured, decimal? Threshold, IReadOnlyList<string> SampleKeys, string? RunId, string? Message);

/// <summary>The latest result of a check, and whether it blocks now and since when.</summary>
public sealed record CheckStatusRow(string CheckId, string Dataset, string Severity, string OnError, CheckOutcome Outcome, DateTimeOffset StatusSince, Guid LastResultId, decimal? Measured, decimal? Threshold,
    DateTimeOffset EvaluatedAt, bool Blocking, DateTimeOffset? BlockedSince);

/// <summary>
/// Writes and reads the <c>quality</c> module (<see cref="QualityModule"/>). <see cref="Record"/> stores a result and moves the check's status and block in <b>one transaction</b>, so a reader never sees a block without the result that set it.
/// <para>
/// <b>When a check blocks</b> (plan 23a D7, QF10): a <c>block</c> check blocks while its latest result is <c>fail</c>, and not when it passes. A <c>block</c> check whose result is <c>error</c> does <b>not</b> start a block (it alerts) unless its
/// <c>on_error</c> is <c>block</c>; an <c>error</c> does not clear a block that stands either, because a check that cannot run has not shown the data to be good. A <c>warn</c> check never blocks (and clears a block it had, if its severity was changed).
/// A result older than the one the status holds is stored in the history and does not change the status, so two evaluators finishing out of order leave the latest one standing.
/// </para>
/// </summary>
public sealed class QualityStore(StateDatabase database)
{
    private const string Module = QualityModule.Name;

    /// <summary>How many sample keys a result keeps.</summary>
    public const int MaxSampleKeys = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The SHA-256 of a dataset's identity as lower-case hex: the key of its rows.</summary>
    public static string DatasetHash(string dataset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dataset)));
    }

    /// <summary>The name a status has in a row and in a file: <c>pass</c>, <c>fail</c> or <c>error</c>.</summary>
    public static string Name(CheckOutcome outcome) => QualityModule.Statuses[(int)outcome];

    private static CheckOutcome Parse(string text) => text switch
    {
        "pass" => CheckOutcome.Pass,
        "fail" => CheckOutcome.Fail,
        "error" => CheckOutcome.Error,
        _ => throw new InvalidOperationException($"The quality module holds the status '{text}'; it is pass, fail or error."),
    };

    /// <summary>
    /// Stores the result and sets the check's status in one transaction. Returns the status as it is afterwards (the existing one when the result is older than it).
    /// </summary>
    public CheckStatusRow Record(CheckResultRecord result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.CheckId);
        if (result.Severity is not ("warn" or "block")) throw new ArgumentException($"The severity of a check is warn or block, not '{result.Severity}'.", nameof(result));
        if (result.OnError is not ("alert" or "block")) throw new ArgumentException($"on_error of a check is alert or block, not '{result.OnError}'.", nameof(result));
        var hash = DatasetHash(result.Dataset);
        var id = database.NewId();
        var samples = result.SampleKeys is { Count: > 0 } keys ? JsonSerializer.Serialize(keys.Take(MaxSampleKeys), Json) : null;
        var status = Name(result.Outcome);
        // 1 sets the block, 0 clears it, 2 leaves it as it is (an error does neither unless the check says on_error: block)
        var action = result.Severity != "block" ? 0 : result.Outcome switch { CheckOutcome.Fail => 1, CheckOutcome.Pass => 0, _ => result.OnError == "block" ? 1 : 2 };
        var now = database.Time.GetUtcNow();

        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:check_result} (id, dataset_hash, check_id, dataset, status, measured, threshold, sample_keys, message, run_id, evaluated_at) VALUES ($id, $hash, $check, $dataset, $status, $measured, $threshold, CAST($samples AS {{json}}), $message, $run, $at);"))
            {
                insert.Bind(database, "id", id).Bind(database, "hash", hash).Bind(database, "check", result.CheckId).Bind(database, "dataset", result.Dataset).Bind(database, "status", status)
                    .Bind(database, "measured", result.Measured, System.Data.DbType.Decimal).Bind(database, "threshold", result.Threshold, System.Data.DbType.Decimal).Bind(database, "samples", samples, System.Data.DbType.String)
                    .Bind(database, "message", result.Message, System.Data.DbType.String).Bind(database, "run", result.RunId, System.Data.DbType.String).Bind(database, "at", result.EvaluatedAt);
                insert.ExecuteNonQuery();
            }

            // the status: a row that is there moves by one statement (so two writers never read, then overwrite, each other); a row that is not there is inserted, and a writer that loses the insert to another moves it instead
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (UpdateStatus(connection, transaction, result, hash, id, status, action, now)) break;
                if (StatusExists(connection, transaction, hash, result.CheckId)) break;   // older than the stored one: the history has it, the status stays
                using var insertStatus = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore(
                    "{table:check_status}", "dataset_hash, check_id, dataset, severity, on_error, status, status_changed_at, last_result_id, measured, threshold, evaluated_at, blocking, blocked_at, updated_at",
                    "$hash, $check, $dataset, $severity, $on_error, $status, $at, $id, $measured, $threshold, $at, $blocking, $blocked_at, $now", "dataset_hash, check_id"));
                insertStatus.Bind(database, "hash", hash).Bind(database, "check", result.CheckId).Bind(database, "dataset", result.Dataset).Bind(database, "severity", result.Severity).Bind(database, "on_error", result.OnError)
                    .Bind(database, "status", status).Bind(database, "at", result.EvaluatedAt).Bind(database, "id", id).Bind(database, "measured", result.Measured, System.Data.DbType.Decimal)
                    .Bind(database, "threshold", result.Threshold, System.Data.DbType.Decimal).Bind(database, "blocking", action == 1)
                    .Bind(database, "blocked_at", action == 1 ? result.EvaluatedAt : null, System.Data.DbType.DateTimeOffset).Bind(database, "now", now);
                if (insertStatus.ExecuteNonQuery() == 1) break;
            }

            transaction.Commit();
        });
        return Status(result.Dataset, result.CheckId)!;
    }

    private bool UpdateStatus(DbConnection connection, DbTransaction transaction, CheckResultRecord result, string hash, Guid id, string status, int action, DateTimeOffset now)
    {
        using var update = database.Command(Module, connection, transaction,
            """
            UPDATE {table:check_status} SET
                dataset = $dataset, severity = $severity, on_error = $on_error,
                status_changed_at = CASE WHEN status = $status THEN status_changed_at ELSE $at END,
                status = $status, last_result_id = $id, measured = $measured, threshold = $threshold, evaluated_at = $at,
                blocking = CASE WHEN $action = 1 THEN $true WHEN $action = 0 THEN $false ELSE blocking END,
                blocked_at = CASE WHEN $action = 1 THEN COALESCE(blocked_at, $at) WHEN $action = 0 THEN NULL ELSE blocked_at END,
                updated_at = $now
            WHERE dataset_hash = $hash AND check_id = $check AND evaluated_at <= $at;
            """);
        update.Bind(database, "dataset", result.Dataset).Bind(database, "severity", result.Severity).Bind(database, "on_error", result.OnError).Bind(database, "status", status).Bind(database, "at", result.EvaluatedAt)
            .Bind(database, "id", id).Bind(database, "measured", result.Measured, System.Data.DbType.Decimal).Bind(database, "threshold", result.Threshold, System.Data.DbType.Decimal).Bind(database, "action", (long)action)
            .Bind(database, "true", true).Bind(database, "false", false).Bind(database, "now", now).Bind(database, "hash", hash).Bind(database, "check", result.CheckId);
        return update.ExecuteNonQuery() == 1;
    }

    private bool StatusExists(DbConnection connection, DbTransaction transaction, string hash, string checkId)
    {
        using var exists = database.Command(Module, connection, transaction, "SELECT COUNT(*) FROM {table:check_status} WHERE dataset_hash = $hash AND check_id = $check;");
        exists.Bind(database, "hash", hash).Bind(database, "check", checkId);
        return Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private const string StatusColumns = "check_id, dataset, severity, on_error, status, status_changed_at, last_result_id, measured, threshold, evaluated_at, blocking, blocked_at";

    private static CheckStatusRow ReadStatus(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), r.Time(5), r.Uuid(6),
        r.IsDBNull(7) ? null : r.Decimal(7), r.IsDBNull(8) ? null : r.Decimal(8), r.Time(9), r.Bool(10), r.NullableTime(11));

    /// <summary>The status of one check, or null when it has none.</summary>
    public CheckStatusRow? Status(string dataset, string checkId) =>
        database.Retry(() => Statuses("WHERE dataset_hash = $hash AND check_id = $check", c => c.Bind(database, "hash", DatasetHash(dataset)).Bind(database, "check", checkId)).FirstOrDefault());

    /// <summary>The status of every check of a dataset, by check id.</summary>
    public IReadOnlyList<CheckStatusRow> StatusesOf(string dataset) =>
        database.Retry(() => Statuses("WHERE dataset_hash = $hash", c => c.Bind(database, "hash", DatasetHash(dataset))));

    /// <summary>The checks that block now, oldest block first: what a downstream run, a publish or a promotion asks (plan 23a FQ14).</summary>
    public IReadOnlyList<CheckStatusRow> Blocks() =>
        database.Retry(() => Statuses("WHERE blocking = $true", c => c.Bind(database, "true", true), "blocked_at, check_id"));

    /// <summary>The checks that block <paramref name="dataset"/> now.</summary>
    public IReadOnlyList<CheckStatusRow> BlocksOf(string dataset) =>
        database.Retry(() => Statuses("WHERE dataset_hash = $hash AND blocking = $true", c => c.Bind(database, "hash", DatasetHash(dataset)).Bind(database, "true", true), "blocked_at, check_id"));

    private List<CheckStatusRow> Statuses(string where, Action<DbCommand> bind, string order = "check_id")
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(Module, connection, $"SELECT {StatusColumns} FROM {{table:check_status}} {where} ORDER BY {order};");
        bind(command);
        using var reader = command.ExecuteReader();
        var rows = new List<CheckStatusRow>();
        while (reader.Read()) rows.Add(ReadStatus(reader));
        return rows;
    }

    /// <summary>The evaluations of one check, newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<StoredCheckResult> History(string dataset, string checkId, int limit = 50) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT id, check_id, dataset, status, evaluated_at, measured, threshold, sample_keys, run_id, message FROM {{table:check_result}} WHERE dataset_hash = $hash AND check_id = $check ORDER BY evaluated_at DESC, id DESC {database.Limit("limit")};");
            command.Bind(database, "hash", DatasetHash(dataset)).Bind(database, "check", checkId).Bind(database, "limit", (long)limit);
            using var reader = command.ExecuteReader();
            var rows = new List<StoredCheckResult>();
            while (reader.Read())
                rows.Add(new StoredCheckResult(reader.Uuid(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)), reader.Time(4), reader.IsDBNull(5) ? null : reader.Decimal(5), reader.IsDBNull(6) ? null : reader.Decimal(6),
                    reader.IsDBNull(7) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(7), Json) ?? [], reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            return rows;
        });

    /// <summary>Removes a check's status, and with it any block it held (the check is gone from its definition). Its history ages out under retention. False when it had none.</summary>
    public bool Forget(string dataset, string checkId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "DELETE FROM {table:check_status} WHERE dataset_hash = $hash AND check_id = $check;");
            command.Bind(database, "hash", DatasetHash(dataset)).Bind(database, "check", checkId);
            return command.ExecuteNonQuery() > 0;
        });
}
