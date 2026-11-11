namespace DbDataKit.State;

/// <summary>
/// The <c>runner</c> module (plan 28a R04, decisions DR10 and DR11): what is about <b>runners</b>, kept per group (the runner group's instances share it). Package versions, artifacts,
/// bindings and per-instance preparation status are plan 30's and live in <c>definitions</c>; a task's progress is <c>runner_progress</c>'s; a run is <c>runs</c>'.
/// <list type="bullet">
/// <item><c>instance</c>: one process of a runner, with the heartbeat that is its lease. A runner that stops heartbeating is gone to the others after the lease timeout.</item>
/// <item><c>instance_label</c>: labels an administrator set (<c>admin</c>) or the runner reported about itself (<c>reported</c>); packages are routed by them.</item>
/// <item><c>capability</c>: what a runner reports about its environment (<c>network: outside</c>, <c>memory_limit: enforced|watched</c>); it reports, the suite does not enforce (plan 28 section 5).</item>
/// <item><c>posture_choice</c>: every loosened default is a recorded choice (who, when, why), for a package or for the whole install (<c>*</c>).</item>
/// <item><c>task_projection</c>: one row per package, channel and task, rebuilt from the active manifest at activation; the scheduler loop reads only this.</item>
/// <item><c>trigger_state</c>: when a trigger last fired and its cursor.</item>
/// <item><c>task_owner</c>: who schedules a task during a move from a product's own scheduler (plan 28c E09, ED6): <c>old</c> (the product does; the runner leaves the task alone), <c>observe</c> (the product does; the runner only records what it would
/// have queued) or <c>runner</c>. A task with no row is the runner's, as every task was before the move existed.</item>
/// <item><c>task_decision</c>: what an <c>observe</c> task would have queued and when, beside what the old owner did (<c>source</c> <c>runner</c> or <c>old</c>, ED7); the product compares them.</item>
/// </list>
/// Documents a task or a trigger carries (schedule, triggers, limits) are kept as the text they were written as (<c>{{text}}</c>, not <c>{{json}}</c>): PostgreSQL's jsonb would reformat them.
/// History: an instance that has not heartbeat for the retention age is removed with its labels and capabilities; the rest is current state.
/// </summary>
public static class RunnerModule
{
    public const string Name = "runner";

    /// <summary>The <c>scope</c> of a posture choice that applies to every package.</summary>
    public const string AllPackages = "*";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- One process of a runner. state is Running or Gone; heartbeat_at is the lease: an instance whose heartbeat is older than the lease timeout is not live, whatever its state says.
            CREATE TABLE {table:instance} (
                id            {{uuid}} PRIMARY KEY,
                runner        {{key}} NOT NULL,
                host          {{key}} NOT NULL,
                version       {{key}} NOT NULL,
                started_at    {{time}} NOT NULL,
                heartbeat_at  {{time}} NOT NULL,
                state         {{key}} NOT NULL
            );
            CREATE INDEX {index:instance_by_heartbeat} ON {table:instance}(state, heartbeat_at);

            -- A label of an instance: source is admin (an administrator set it) or reported (the runner says it about itself).
            CREATE TABLE {table:instance_label} (
                instance_id   {{uuid}} NOT NULL,
                label         {{key}} NOT NULL,
                source        {{key}} NOT NULL,
                PRIMARY KEY (instance_id, label)
            );

            -- What an instance reports about its environment: name and value, and when it last said so.
            CREATE TABLE {table:capability} (
                instance_id       {{uuid}} NOT NULL,
                name              {{key}} NOT NULL,
                capability_value  {{key}} NOT NULL,
                reported_at       {{time}} NOT NULL,
                PRIMARY KEY (instance_id, name)
            );

            -- A loosened default, recorded (plan 28 section 5). scope is a package name, or * for every package; setting names what was loosened.
            CREATE TABLE {table:posture_choice} (
                scope         {{key}} NOT NULL,
                setting       {{key}} NOT NULL,
                chosen_by     {{key}} NOT NULL,
                chosen_at     {{time}} NOT NULL,
                note          {{text}} NULL,
                PRIMARY KEY (scope, setting)
            );

            -- What the scheduler loop reads for a task: the active manifest's schedule, triggers and limits as the text they were written as, the labels it requires (a JSON array as text) and the hash
            -- of its progress declaration. Rebuilt wholesale for a package and channel at activation.
            CREATE TABLE {table:task_projection} (
                package          {{key}} NOT NULL,
                channel          {{key}} NOT NULL,
                task             {{key}} NOT NULL,
                version          {{key}} NOT NULL,
                schedule         {{text}} NULL,
                triggers         {{text}} NULL,
                limits           {{text}} NULL,
                required_labels  {{text}} NULL,
                progress_hash    {{key}} NULL,
                rebuilt_at       {{time}} NOT NULL,
                PRIMARY KEY (package, channel, task)
            );

            -- When a trigger of a task last fired and where it got to (a position in what it watches), as opaque text.
            CREATE TABLE {table:trigger_state} (
                package        {{key}} NOT NULL,
                channel        {{key}} NOT NULL,
                task           {{key}} NOT NULL,
                trigger_id     {{key}} NOT NULL,
                last_fired_at  {{time}} NULL,
                cursor_text    {{text}} NULL,
                PRIMARY KEY (package, channel, task, trigger_id)
            );
            """,
            // plan 29b U02: what an instance records about its binary, so a migration can tell whether a live instance still works with it (module_versions is name=version;name=version)
            """
            ALTER TABLE {table:instance} {{addcolumn}} software_version {{key}} NULL;
            ALTER TABLE {table:instance} {{addcolumn}} module_versions {{text}} NULL;
            """,
            // plan 24a UC01: which formats of each kind of published definition the instance reads (kind=previous-current;kind=previous-current), so a publish can be refused while a live instance cannot read it
            """
            ALTER TABLE {table:instance} {{addcolumn}} definition_formats {{text}} NULL;
            """,
            // plan 24a UC05: the compatibility generation the instance's binary was built with (plan 24 section 2), so a console and the group's tools can tell which instances are further apart than N and N-1
            // plan 24a UC05 (the generation column) and plan 28c E09 (who schedules a task, what the runner would have queued): no release holds the runner module yet, so the E09 tables are part of this newest migration
            """
            ALTER TABLE {table:instance} {{addcolumn}} generation {{int}} NULL;

            -- owner is old, observe or runner. A task with no row is scheduled by the runner. changed_by and reason are the audit's words, kept beside the setting so a console can show them.
            CREATE TABLE {table:task_owner} (
                package     {{key}} NOT NULL,
                channel     {{key}} NOT NULL,
                task        {{key}} NOT NULL,
                owner       {{key}} NOT NULL,
                changed_by  {{key}} NOT NULL,
                changed_at  {{time}} NOT NULL,
                reason      {{text}} NULL,
                PRIMARY KEY (package, channel, task)
            );

            -- One scheduling decision of an observed task: source runner is what the runner would have queued at decided_at (paused: it would have passed the time over), source old is what the product's own scheduler reported queuing.
            CREATE TABLE {table:task_decision} (
                package     {{key}} NOT NULL,
                channel     {{key}} NOT NULL,
                task        {{key}} NOT NULL,
                source      {{key}} NOT NULL,
                decided_at  {{time}} NOT NULL,
                missed      {{int}} NOT NULL,
                paused      {{int}} NOT NULL,
                PRIMARY KEY (package, channel, task, source, decided_at)
            );
            """,
        ],
        // versions 2 to 4 only add nullable columns and new tables, which the code of the version before never reads or writes: an instance of the previous version still works with them
        compatibleWithPrevious: 1,
        retention:
        [
            // an instance that went quiet is history; its labels and capabilities go with it
            new RetentionRule("instance", "heartbeat_at", Children: [new RetentionChild("instance_label", "instance_id"), new RetentionChild("capability", "instance_id")]),
        ]);
}
