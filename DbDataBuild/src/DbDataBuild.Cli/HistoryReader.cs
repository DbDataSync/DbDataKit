using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>Reads the audit tables and builds the per-column history (DESIGN.md 12.3), including which inconsistencies an operator has acknowledged.</summary>
internal static class HistoryReader
{
    public static async Task<(IReadOnlyList<ColumnHistoryEntry> Entries, IReadOnlyList<string> UnreadablePlans)> ReadAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default)
    {
        var ddl = TrackingDdl.For(scope.Engine);
        string C(string n) => ddl.Quote(n);
        string T(string t) => $"{C(scope.SchemaName)}.{C(t)}";
        var byConnection = new[] { new GateParameter("connection", System.Data.DbType.String, scope.Connection) };
        string Cell(object? v) => v switch { null => "", string s => s.Trim(), _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };

        var planRows = await read.QueryAsync($"SELECT m.{C("plan_id")}, m.{C("applied_by")}, m.{C("applied_utc")}, m.{C("plan_text")} FROM {T("migration_log")} m WHERE m.{C("connection")} = @connection AND m.{C("status")} = 'completed' AND m.{C("plan_text")} LIKE '%Q-history-%' ORDER BY m.{C("applied_utc")}", byConnection, ct);   // only a plan that holds a history answer has anything to say here: a plan text is large, and every `plan` now reads these
        var applied = new List<AppliedPlan>();
        var unreadable = new List<string>();
        foreach (var r in planRows)
            if (PlanDocument.Parse((string)r[3]!, "migration_log", new List<Diagnostic>()) is { } p) applied.Add(new(Cell(r[0]), Cell(r[1]), (DateTime)r[2]!, p));
            else unreadable.Add(Cell(r[0]));

        // the shapes and the load ranges of the models that have a history answer, and no others: a connection's `operation_interval` grows with every load, and nothing here is about a model that never had a column added
        var models = applied.SelectMany(a => a.Plan.Answers).Where(a => a.QuestionId.StartsWith("Q-history-", StringComparison.Ordinal))
            .Select(a => a.QuestionId["Q-history-".Length..]).Where(s => s.LastIndexOf('.') > 0).Select(s => s[..s.LastIndexOf('.')]).Distinct(StringComparer.Ordinal).Take(1000).ToList();
        if (models.Count == 0) return ([], unreadable);
        var forModels = models.Select((m, i) => new GateParameter($"m{i}", System.Data.DbType.String, m)).ToList();
        var inModels = (string column) => $"{column} IN ({string.Join(", ", forModels.Select(p => "@" + p.Name))})";
        var withModels = byConnection.Concat(forModels).ToList();
        var shapeRows = await read.QueryAsync($"SELECT s.{C("object_name")}, s.{C("shape_hash")}, s.{C("first_seen_utc")}, s.{C("source")}, s.{C("plan_id")} FROM {T("schema_version")} s WHERE s.{C("connection")} = @connection AND {inModels("s." + C("object_name"))}", withModels, ct);
        var intervalRows = await read.QueryAsync($"SELECT DISTINCT i.{C("model")}, i.{C("range_start")}, i.{C("range_end")}, i.{C("operation")}, r.{C("started_utc")} FROM {T("operation_interval")} i JOIN {T("run_log")} r ON r.{C("run_id")} = i.{C("run_id")} AND r.{C("model")} = i.{C("model")} WHERE i.{C("connection")} = @connection AND {inModels("i." + C("model"))}", withModels, ct);
        var ackRows = await read.QueryAsync($"SELECT b.{C("code")}, b.{C("model")}, b.{C("detail")}, b.{C("ack_by")}, b.{C("ack_reason")}, b.{C("ack_utc")} FROM {T("block_log")} b WHERE b.{C("connection")} = @connection AND b.{C("ack_utc")} IS NOT NULL AND b.{C("code")} = 'DDB-443' ORDER BY b.{C("ack_utc")}", byConnection, ct);

        var entries = ColumnHistory.Build(applied,
            shapeRows.Select(r => new RecordedShape(Cell(r[0]), Cell(r[1]), (DateTime)r[2]!, Cell(r[3]), r[4] == null ? null : Cell(r[4]))).ToList(),
            intervalRows.Select(r => new RecordedInterval(Cell(r[0]), r[1] == null ? null : Cell(r[1]), r[2] == null ? null : Cell(r[2]), Cell(r[3]), (DateTime)r[4]!)).ToList(),
            ackRows.Select(r => new HistoryAck($"{Cell(r[0])}|{Cell(r[1])}|{Cell(r[2])}", Cell(r[3]), Cell(r[4]), (DateTime)r[5]!)).ToList());
        return (entries, unreadable);
    }
}
