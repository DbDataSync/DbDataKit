using System.Data.Common;

namespace DbDataKit.State;

/// <summary>One case of an evaluation run as it is kept (plan 27a AG23): the score, whether it passed, the answer, the result of each check as JSON, the tokens and cost, and why the case could not be run when it could not.</summary>
public sealed record EvaluationCaseRow(string RunId, string Name, string Task, decimal Score, bool Passed, string Answer, string Checks, long InputTokens, long OutputTokens, decimal Cost, string? Problem);

/// <summary>A run of an agent's evaluation set against a channel: the agent version, the model, the knowledge versions it was given (JSON), the score, the cost and the cases.</summary>
public sealed record EvaluationRunRow(string Id, string Agent, string AgentVersion, string Channel, string? Model, string KnowledgeVersions, string Trigger, decimal Score, int Passed, int Total, decimal Cost,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<EvaluationCaseRow> Cases);

/// <summary>The lowest score an agent may have before a promotion to a channel, as a recorded choice.</summary>
public sealed record EvaluationMinimumRow(string Agent, string Channel, decimal Minimum, string SetBy, DateTimeOffset SetAt);

/// <summary>
/// Writes and reads the evaluation tables of the <c>agents</c> module (plan 27a AG23): runs with their cases, and the minimum score before a promotion. A run is written whole, in one transaction, after it has finished: no one sees half of one.
/// </summary>
public sealed class EvaluationStore(StateDatabase database)
{
    private const string Module = AgentsModule.Name;

    private const string RunColumns = "id, agent, agent_version, channel, model, knowledge_versions, trigger_kind, score, passed, total, cost, started_at, finished_at";

    private const string CaseColumns = "run_id, case_name, task, score, passed, answer, checks, input_tokens, output_tokens, cost, problem";

    private static string? Text(DbDataReader r, int at) => r.IsDBNull(at) ? null : r.GetString(at);

    /// <summary>Keeps a finished run with its cases.</summary>
    public void Save(EvaluationRunRow run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Id);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:evaluation_run} (id, agent, agent_version, channel, model, knowledge_versions, trigger_kind, score, passed, total, cost, started_at, finished_at) VALUES ($id, $agent, $version, $channel, $model, $knowledge, $trigger, $score, $passed, $total, $cost, $started, $finished);"))
            {
                insert.Bind(database, "id", run.Id).Bind(database, "agent", run.Agent).Bind(database, "version", run.AgentVersion).Bind(database, "channel", run.Channel).Bind(database, "model", run.Model, System.Data.DbType.String)
                    .Bind(database, "knowledge", run.KnowledgeVersions).Bind(database, "trigger", run.Trigger).Bind(database, "score", run.Score, System.Data.DbType.Decimal).Bind(database, "passed", run.Passed).Bind(database, "total", run.Total)
                    .Bind(database, "cost", run.Cost, System.Data.DbType.Decimal).Bind(database, "started", run.StartedAt).Bind(database, "finished", run.FinishedAt);
                insert.ExecuteNonQuery();
            }
            foreach (var c in run.Cases)
            {
                using var insert = database.Command(Module, connection, transaction,
                    "INSERT INTO {table:evaluation_case} (run_id, case_name, task, score, passed, answer, checks, input_tokens, output_tokens, cost, problem) VALUES ($run, $name, $task, $score, $passed, $answer, $checks, $in, $out, $cost, $problem);");
                insert.Bind(database, "run", run.Id).Bind(database, "name", c.Name).Bind(database, "task", c.Task).Bind(database, "score", c.Score, System.Data.DbType.Decimal).Bind(database, "passed", c.Passed ? 1 : 0).Bind(database, "answer", c.Answer)
                    .Bind(database, "checks", c.Checks).Bind(database, "in", c.InputTokens).Bind(database, "out", c.OutputTokens).Bind(database, "cost", c.Cost, System.Data.DbType.Decimal).Bind(database, "problem", c.Problem, System.Data.DbType.String);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        });
    }

    private static EvaluationRunRow ReadRun(DbDataReader r, IReadOnlyList<EvaluationCaseRow> cases) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Text(r, 4), r.GetString(5), r.GetString(6), r.Decimal(7), r.Int32(8), r.Int32(9), r.Decimal(10), r.Time(11), r.Time(12), cases);

    private IReadOnlyList<EvaluationCaseRow> Cases(DbConnection connection, string runId)
    {
        using var command = database.Command(Module, connection, $"SELECT {CaseColumns} FROM {{table:evaluation_case}} WHERE run_id = $run ORDER BY case_name;");
        command.Bind(database, "run", runId);
        using var reader = command.ExecuteReader();
        var cases = new List<EvaluationCaseRow>();
        while (reader.Read())
            cases.Add(new EvaluationCaseRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.Decimal(3), reader.Int32(4) != 0, reader.GetString(5), reader.GetString(6), reader.Int64(7), reader.Int64(8), reader.Decimal(9), Text(reader, 10)));
        return cases;
    }

    /// <summary>One run with its cases, or null.</summary>
    public EvaluationRunRow? Run(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {RunColumns} FROM {{table:evaluation_run}} WHERE id = $id;");
            command.Bind(database, "id", id);
            // the run is read and its reader closed before the cases are (SQL Server allows one open reader on a connection)
            EvaluationRunRow? run;
            using (var reader = command.ExecuteReader()) run = reader.Read() ? ReadRun(reader, []) : null;
            return run is null ? null : run with { Cases = Cases(connection, id) };
        });

    /// <summary>The runs of an agent, newest first, on one channel when it is given (without their cases: a run is opened to see them).</summary>
    public IReadOnlyList<EvaluationRunRow> Runs(string agent, string? channel = null, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT {RunColumns} FROM {{table:evaluation_run}} WHERE agent = $agent{(channel is null ? "" : " AND channel = $channel")} ORDER BY started_at DESC, id {database.Limit("limit")};");
            command.Bind(database, "agent", agent).Bind(database, "limit", limit);
            if (channel is not null) command.Bind(database, "channel", channel);
            using var reader = command.ExecuteReader();
            var runs = new List<EvaluationRunRow>();
            while (reader.Read()) runs.Add(ReadRun(reader, []));
            return (IReadOnlyList<EvaluationRunRow>)runs;
        });

    /// <summary>The latest run of exactly this version of the agent on the channel, or null (a promotion needs the evaluation of the version it promotes).</summary>
    public EvaluationRunRow? Latest(string agent, string agentVersion, string channel) =>
        Runs(agent, channel, 200).FirstOrDefault(r => r.AgentVersion == agentVersion);

    // ---- the minimum before a promotion ----

    public EvaluationMinimumRow? Minimum(string agent, string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT agent, channel, minimum, set_by, set_at FROM {table:evaluation_minimum} WHERE agent = $agent AND channel = $channel;");
            command.Bind(database, "agent", agent).Bind(database, "channel", channel);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new EvaluationMinimumRow(reader.GetString(0), reader.GetString(1), reader.Decimal(2), reader.GetString(3), reader.Time(4)) : null;
        });

    /// <summary>Records the minimum score an agent needs before it is promoted to a channel, and who chose it; null removes it.</summary>
    public void SetMinimum(string agent, string channel, decimal? minimum, string by)
    {
        if (minimum is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimum), "A minimum score is from 0 to 1.");
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:evaluation_minimum} WHERE agent = $agent AND channel = $channel;"))
            {
                delete.Bind(database, "agent", agent).Bind(database, "channel", channel);
                delete.ExecuteNonQuery();
            }
            if (minimum is { } value)
            {
                using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:evaluation_minimum} (agent, channel, minimum, set_by, set_at) VALUES ($agent, $channel, $minimum, $by, $at);");
                insert.Bind(database, "agent", agent).Bind(database, "channel", channel).Bind(database, "minimum", value, System.Data.DbType.Decimal).Bind(database, "by", by).Bind(database, "at", now);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        });
    }
}
