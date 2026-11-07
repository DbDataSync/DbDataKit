namespace DbDataKit.State.Tracking;

/// <summary>
/// The <c>tracking</c> module (plan 29 section 4a, plan 29a Q21): the unified copy of append-only tracking tables that a product keeps where its data is (Build's tracking tables on the target, first). It holds
/// rows of any stream as documents, under the <b>identity</b> of the connection they came from (the server and database it really is, not its name), and the divergences a follower found.
/// <para>
/// <b>Scope: per group.</b> The follower's checkpoint is kept in the group's progress store (<see cref="ProgressModule"/>, per group), and the follower moves it in the same transaction as the rows it writes
/// here, so the rows and the checkpoint must be one group's: a group's follower is the only writer of its copy. Rows of different connections never mix because the identity is part of every key, so
/// the group, not the install, is the boundary that matches who copies.
/// </para>
/// <para>
/// <b>Retention.</b> <c>tracking_row</c> has a rule with <b>no default age</b>: how long the history of a connection is kept is the owner's decision (Build's own rules decide what the target keeps), so nothing is
/// removed until an administrator sets days. <c>tracking_divergence</c> has no rule: a divergence and how it was resolved are a record that is kept.
/// </para>
/// </summary>
public static class TrackingModule
{
    public const string Name = "tracking";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- One row of a copied stream. connection_identity is the server and database the row came from (never the connection's name); stream is the table's name on that side; row_key is the row's own
            -- key; order_pos is the ordering value (an id, or a time as UTC ticks). document is the whole row as text. archive_id is '' for a live row; a row put aside by an archive resolution carries
            -- the marker of that divergence and is no longer part of the live stream.
            CREATE TABLE {table:tracking_row} (
                connection_identity {{key}} NOT NULL,
                stream              {{key}} NOT NULL,
                row_key             {{key}} NOT NULL,
                archive_id          {{key}} NOT NULL,
                order_pos           {{int}} NOT NULL,
                document            {{text}} NOT NULL,
                recorded_at         {{time}} NOT NULL,
                PRIMARY KEY (connection_identity, stream, row_key, archive_id)
            );

            CREATE INDEX {index:tracking_row_by_order} ON {table:tracking_row}(connection_identity, stream, archive_id, order_pos, row_key);

            -- A connection and stream whose copy and primary part. status is diverged until an administrator resolves it (archived or reset); the common position is the last row both agree on.
            CREATE TABLE {table:tracking_divergence} (
                {{identity:id}},
                connection_identity {{key}} NOT NULL,
                stream              {{key}} NOT NULL,
                status              {{key}} NOT NULL,
                reason              {{text}} NOT NULL,
                common_order_pos    {{int}} NULL,
                common_row_key      {{key}} NULL,
                primary_order_pos   {{int}} NULL,
                primary_row_key     {{key}} NULL,
                copy_order_pos      {{int}} NULL,
                copy_row_key        {{key}} NULL,
                diverged_at         {{time}} NOT NULL,
                resolved_at         {{time}} NULL,
                resolved_by         {{key}} NULL
            );

            CREATE INDEX {index:tracking_divergence_by_identity} ON {table:tracking_divergence}(connection_identity, stream, status);
            """,
        ],
        retention: [new RetentionRule("tracking_row", "recorded_at", DefaultDays: null)]);
}
