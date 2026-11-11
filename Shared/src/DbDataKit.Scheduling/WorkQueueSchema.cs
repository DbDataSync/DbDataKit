namespace DbDataKit.Scheduling;

/// <summary>The schema of the work queue and the run lock (plan 13, M02), a fragment a product includes in its <see cref="DbDataKit.State.StateSchema"/>.</summary>
public static class WorkQueueSchema
{
    /// <summary>The queue and the lock.</summary>
    public const string Queue =
        """
        -- Durable work for the workers of one service, shared by every process that uses the same state database.
        --
        -- Service is the service whose workers claim the row ('dbdatasync', 'dbdatabuild'): a state database that more than one
        -- service writes to keeps their queues apart, and every statement is scoped to it.
        --
        -- Owner is whoever the work belongs to and is paused, cancelled and asked about as a whole (a Sync replication, a Build
        -- project). Kind is what sort of work. Subject is the thing worked on (a mapping, a model) and Part a slice of it (a
        -- segment, a partition). Part is '' when there is none, and NOT NULL because the column is in a unique index, where every
        -- NULL counts as different and would defeat the rule below. Resource is what two items must not write at once (a
        -- target table); '' means none. Lane is the pool that runs it, capped per owner at claim time.
        --
        -- One item per (Service, Owner, Kind, Subject, Part) can be in flight (Pending, Claimed or Running). A second enqueue of
        -- the same thing finds the first. A claimed item holds a lease: a worker that stops renewing it loses the item to the
        -- next ReleaseExpired.
        CREATE TABLE WorkQueue (
            {{identity:Id}},
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            Kind {{key}} NOT NULL,
            Subject {{key}} NOT NULL,
            Part {{key}} NOT NULL DEFAULT '',
            Resource {{key}} NOT NULL DEFAULT '',
            Lane {{key}} NOT NULL DEFAULT '',
            RunId {{key}} NOT NULL,
            Status {{key}} NOT NULL,
            Priority {{int}} NOT NULL DEFAULT 0,
            PayloadJson {{text}} NULL,
            EnqueuedAtUtc {{text}} NOT NULL,
            AvailableAtUtc {{key}} NOT NULL,
            ClaimedAtUtc {{text}} NULL,
            ClaimedBy {{text}} NULL,
            LeaseExpiresUtc {{key}} NULL,
            CancelRequestedUtc {{text}} NULL
        );
        CREATE INDEX IX_WorkQueue_Claim ON WorkQueue(Service, Owner, Status, AvailableAtUtc);
        CREATE INDEX IX_WorkQueue_Run ON WorkQueue(RunId);
        CREATE UNIQUE INDEX UX_WorkQueue_InFlight
            ON WorkQueue(Service, Owner, Kind, Subject, Part)
            WHERE Status IN ('Pending','Claimed','Running');

        -- Keeps two runs of the same kind of work on the same subject from overlapping. A second acquire finds the first.
        CREATE TABLE RunLocks (
            Service {{key}} NOT NULL,
            Owner {{key}} NOT NULL,
            Kind {{key}} NOT NULL,
            Subject {{key}} NOT NULL,
            RunId {{text}} NOT NULL,
            AcquiredAtUtc {{text}} NOT NULL,
            PRIMARY KEY (Service, Owner, Kind, Subject)
        );
        """;
}
