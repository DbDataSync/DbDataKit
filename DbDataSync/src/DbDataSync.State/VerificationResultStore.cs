using System.Data.Common;

namespace DbDataSync.State;

/// <summary>
/// Where a verification result is, so the console can list and locate one without holding the result
/// itself — see phase 43. The parquet the TaskRunner wrote is the answer; this is the index.
/// </summary>
public sealed class VerificationResultStore(StateDatabase database)
{
    private const string Module = SyncStateModule.Name;

    private const string Columns = "id, run_id, task_name, mapping_name, check_name, completed_at, source_read_at, target_read_at, groups_compared, differing_groups, result_path";

    /// <summary>Records a result; the same run and check again replaces the earlier row (a journal replayed after a crash writes it a second time).</summary>
    public void Record(VerificationResultRecord result) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.Upsert(
                "{table:verification_result}",
                "run_id, task_name, mapping_name, check_name, completed_at, source_read_at, target_read_at, groups_compared, differing_groups, result_path",
                "$run, $task, $mapping, $check, $completed, $source_read, $target_read, $groups, $differing, $path",
                "run_id, check_name",
                """
                completed_at = EXCLUDED.completed_at,
                source_read_at = EXCLUDED.source_read_at,
                target_read_at = EXCLUDED.target_read_at,
                groups_compared = EXCLUDED.groups_compared,
                differing_groups = EXCLUDED.differing_groups,
                result_path = EXCLUDED.result_path
                """));
            cmd.Bind(database, "run", result.RunId);
            cmd.Bind(database, "task", result.TaskName);
            cmd.Bind(database, "mapping", result.MappingName);
            cmd.Bind(database, "check", result.CheckName);
            cmd.Bind(database, "completed", result.CompletedAtUtc);
            cmd.Bind(database, "source_read", result.SourceReadAtUtc);
            cmd.Bind(database, "target_read", result.TargetReadAtUtc);
            cmd.Bind(database, "groups", (long)result.GroupsCompared);
            cmd.Bind(database, "differing", (long)result.DifferingGroups);
            cmd.Bind(database, "path", result.ResultPath);
            cmd.ExecuteNonQuery();
        });

    /// <summary>The results of a replication, newest first, those of one mapping when <paramref name="mappingName"/> is given.</summary>
    public IReadOnlyList<VerificationResultRecord> List(string taskName, string? mappingName = null, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $$"""
                SELECT {{Columns}}
                FROM {table:verification_result}
                WHERE task_name = $task {{(mappingName is null ? "" : "AND mapping_name = $mapping")}}
                ORDER BY completed_at DESC, id DESC {{database.Limit("limit")}};
                """);
            cmd.Bind(database, "task", taskName);
            if (mappingName is not null)
                cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "limit", (long)limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<VerificationResultRecord>();
            while (reader.Read())
                results.Add(Read(reader));
            return (IReadOnlyList<VerificationResultRecord>)results;
        });

    public VerificationResultRecord? Get(long id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:verification_result}} WHERE id = $id;");
            cmd.Bind(database, "id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public bool Delete(long id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:verification_result} WHERE id = $id;");
            cmd.Bind(database, "id", id);
            return cmd.ExecuteNonQuery() == 1;
        });

    private static VerificationResultRecord Read(DbDataReader reader) => new(
        reader.Int64(0),
        reader.Uuid(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.Time(5),
        reader.Time(6),
        reader.Time(7),
        reader.Int32(8),
        reader.Int32(9),
        reader.GetString(10));
}
