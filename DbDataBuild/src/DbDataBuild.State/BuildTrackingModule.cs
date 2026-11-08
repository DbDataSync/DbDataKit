using DbDataKit.State;
using DbDataKit.State.Tracking;

namespace DbDataBuild.State;

/// <summary>
/// The <c>build_tracking</c> module (plan 28c section 5.2, slice E16): the copy of Build's tracking tables in the group's state, one row for each row of a stream, as a document (see <see cref="BuildStreams"/>), under the <b>identity</b> of the
/// tracking store it came from. One copy per group; its follower's checkpoints are in the group's progress store and move in the same transaction as the rows.
/// <para>
/// <b>The stored format is fixed here</b> (owner decision 2026-11-07: breaking changes to stored formats are made early, and Build converts nothing; a copy that does not fit is built again from zero by the follower, in bounded batches). A migration is
/// only ever appended. Retention (owner decision, plan 28c QE9): the history of runs (<c>ddl_log</c>, <c>run_log</c>, <c>operation_interval</c>, <c>refresh_plan_member</c>, <c>plan_store</c>) goes after <see cref="RunLikeDays"/> days;
/// <c>schema_version</c>, <c>migration_log</c>, <c>block_log</c> and <c>metadata_document</c> (and the snapshots and the layout record) are never aged out. Divergences are in the shared <c>tracking</c> module.
/// </para>
/// </summary>
public static class BuildTrackingModule
{
    public const string Name = "build_tracking";

    /// <summary>How long the copy keeps the history of a run: 365 days.</summary>
    public const double RunLikeDays = 365;

    /// <summary>The streams whose rows age out, as the retention rule names them.</summary>
    public static IReadOnlyList<string> AgedStreams { get; } = [.. BuildStreams.All.Where(s => s.RunLike).Select(s => s.Name)];

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- One row of a copied tracking table. connection_identity is the tracking store the row came from (its server and database as they really are, and the data connection the rows describe: BuildTrackingIdentity),
            -- never a connection's name alone; stream is the table's name; row_key is the row's key as BuildStreams writes it; order_pos is the ticks of the stream's time column; occurred_at is that time (what retention measures).
            -- document is the row as JSON text (every column but connection), row_hash the SHA-256 of that text, and unsettled is true while a ddl_log or run_log step has not ended, so the copy knows which rows to compare with the target again.
            -- archive_id is '' for a live row; a row put aside by an archive resolution carries the marker of that divergence and is no longer part of the live stream. recorded_at is when the copy wrote it.
            CREATE TABLE {table:tracked_row} (
                connection_identity {{key}} NOT NULL,
                stream              {{key}} NOT NULL,
                row_key             {{key}} NOT NULL,
                archive_id          {{key}} NOT NULL,
                order_pos           {{int}} NOT NULL,
                occurred_at         {{time}} NOT NULL,
                row_hash            {{key}} NOT NULL,
                unsettled           {{bool}} NOT NULL,
                document            {{text}} NOT NULL,
                recorded_at         {{time}} NOT NULL,
                PRIMARY KEY (connection_identity, stream, row_key, archive_id)
            );

            CREATE INDEX {index:tracked_row_by_order} ON {table:tracked_row}(connection_identity, stream, archive_id, order_pos, row_key);
            CREATE INDEX {index:tracked_row_by_age} ON {table:tracked_row}(occurred_at);
            CREATE INDEX {index:tracked_row_unsettled} ON {table:tracked_row}(connection_identity, stream, unsettled);
            """,
        ],
        retention: [new RetentionRule("tracked_row", "occurred_at", RunLikeDays, Where: "stream IN (" + string.Join(", ", BuildStreams.All.Where(s => s.RunLike).Select(s => $"'{s.Name}'")) + ")")]);
}

/// <summary>The state Build keeps a copy of its tracking in: which modules, for which group and service.</summary>
public static class BuildTrackingState
{
    public const string ServiceId = "dbdatabuild";

    public const string DefaultGroup = "main";

    public static StateScope Scope { get; } = new(DefaultGroup, ServiceId);

    /// <summary>The progress store's key: where the follower's checkpoints are.</summary>
    public static ProgressKey ProgressKey { get; } = new(ServiceId, "main", "tracking");

    /// <summary>The modules the copy needs: its own, the shared divergence records and the progress store.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [BuildTrackingModule.Module, TrackingModule.Module, ProgressModule.Module];
}
