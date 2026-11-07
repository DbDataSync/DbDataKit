using DbDataKit.State;

namespace DbDataKit.Scheduling;

/// <summary>
/// The <c>scheduling</c> module (plan 29 section 3.4): the durable work queue and the run locks that every service's workers share, and what a scheduler remembers about each job.
/// One copy per install; <c>service</c> is in every key and every statement, so services that share a database keep their queues apart. Replaces <see cref="WorkQueueSchema"/>,
/// <see cref="JobStateSchema"/> and <see cref="JobPauseSchema"/> when each product moves (plan 29a Q17, Q22).
/// </summary>
public static class SchedulingModule
{
    public const string Name = "scheduling";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- Durable work for the workers of one service, shared by every process that uses the same state database.
            --
            -- service is the service whose workers claim the row ('dbdatasync', 'dbdatabuild'): a database that more than one service writes to keeps their queues apart, and
            -- every statement is scoped to it. owner is whoever the work belongs to and is paused, cancelled and asked about as a whole (a Sync replication, a Build project).
            -- kind is what sort of work. subject is the thing worked on (a mapping, a model) and part a slice of it (a segment, a partition); part is '' when there is none,
            -- and NOT NULL because the column is in a unique index, where every NULL counts as different and would defeat the rule below. resource is what two items must not
            -- write at once (a target table); '' means none. payload is what the product needs to run the item, opaque here and handed back exactly as given (so text, not a
            -- json column, which PostgreSQL would reformat). lane is the pool that runs it, capped per owner at claim time.
            --
            -- One item per (service, owner, kind, subject, part) can be in flight (Pending, Claimed or Running). A second enqueue of the same thing finds the first. A claimed
            -- item holds a lease: a worker that stops renewing it loses the item to the next ReleaseExpired.
            CREATE TABLE {table:work_item} (
                {{identity:id}},
                service             {{key}} NOT NULL,
                owner               {{key}} NOT NULL,
                kind                {{key}} NOT NULL,
                subject             {{key}} NOT NULL,
                part                {{key}} NOT NULL DEFAULT '',
                resource            {{key}} NOT NULL DEFAULT '',
                lane                {{key}} NOT NULL DEFAULT '',
                run_id              {{uuid}} NOT NULL,
                status              {{key}} NOT NULL,
                priority            {{int}} NOT NULL DEFAULT 0,
                payload             {{text}} NULL,
                enqueued_at         {{time}} NOT NULL,
                available_at        {{time}} NOT NULL,
                claimed_at          {{time}} NULL,
                claimed_by          {{key}} NULL,
                lease_expires_at    {{time}} NULL,
                cancel_requested_at {{time}} NULL
            );
            CREATE INDEX {index:work_item_claim} ON {table:work_item}(service, owner, status, available_at);
            CREATE INDEX {index:work_item_run} ON {table:work_item}(run_id);
            CREATE UNIQUE INDEX {index:work_item_in_flight}
                ON {table:work_item}(service, owner, kind, subject, part)
                WHERE status IN ('Pending','Claimed','Running');

            -- Keeps two runs of the same kind of work on the same subject from overlapping. A second acquire finds the first.
            CREATE TABLE {table:run_lock} (
                service      {{key}} NOT NULL,
                owner        {{key}} NOT NULL,
                kind         {{key}} NOT NULL,
                subject      {{key}} NOT NULL,
                run_id       {{uuid}} NOT NULL,
                acquired_at  {{time}} NOT NULL,
                PRIMARY KEY (service, owner, kind, subject)
            );

            -- What a scheduler remembers per job: up to when its scheduled times are dealt with, when its last run began and how it ended.
            CREATE TABLE {table:job_state} (
                service       {{key}} NOT NULL,
                owner         {{key}} NOT NULL,
                job           {{key}} NOT NULL,
                handled_up_to_at {{time}} NULL,
                last_started_at {{time}} NULL,
                last_exit     {{int}} NULL,
                PRIMARY KEY (service, owner, job)
            );

            -- A job someone paused: who, and when. A job with no row here is not paused.
            CREATE TABLE {table:job_pause} (
                service    {{key}} NOT NULL,
                owner      {{key}} NOT NULL,
                job        {{key}} NOT NULL,
                paused_by  {{key}} NOT NULL,
                paused_at  {{time}} NOT NULL,
                PRIMARY KEY (service, owner, job)
            );
            """,
            """
            -- A resource is a pool of resource_limit (plan 28a DR7, R07): at most that many items holding the same resource can be Claimed or Running at once. 1, the default and what every row
            -- written before this migration has, is the exclusion the queue always had. The item being claimed names the limit.
            ALTER TABLE {table:work_item} {{addcolumn}} resource_limit {{int}} NOT NULL DEFAULT 1;
            """,
            """
            -- Why a job was paused, in the words of whoever paused it (Sync's pause note). Null when none was given; an empty note is kept as it was written.
            ALTER TABLE {table:job_pause} {{addcolumn}} note {{text}} NULL;
            """,
        ],
        // finished work is history; what is pending, claimed or running is current state and stays
        retention: [new RetentionRule("work_item", "enqueued_at", Where: "status IN ('Done','Failed','Cancelled')")]);
}
