namespace DbDataKit.State;

/// <summary>
/// The <c>definitions</c> module (plan 29 section 3.4): published definitions, which version each channel serves, and the log of what was published or switched. Kept per group: every
/// instance of a serving or runner group sees the same versions, and two groups in one database each have their own. The event log is the record of who changed what a channel serves
/// and is how an instance learns something moved, so it has no age limit (plan 29a decision D14).
/// </summary>
public static class DefinitionsModule
{
    public const string Name = "definitions";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- Every version a definition has had. A definition is a kind ('app', 'model', 'navigation') and a name; its versions count up from 1 and are never changed or removed.
            -- hash is the SHA-256 of content, so one version can be told from another without reading it. depends_on lists what this version was published against, as
            -- 'kind:name@version' strings in a JSON array. content stays opaque text: its format is the kind's business.
            CREATE TABLE {table:definition_version} (
                kind          {{key}} NOT NULL,
                name          {{key}} NOT NULL,
                version       {{int}} NOT NULL,
                hash          {{key}} NOT NULL,
                content       {{text}} NOT NULL,
                depends_on    {{json}} NOT NULL,
                published_by  {{key}} NOT NULL,
                published_at  {{time}} NOT NULL,
                note          {{text}} NULL,
                PRIMARY KEY (kind, name, version)
            );

            -- The version a channel serves, and the one it served before (what a rollback returns to). One row per definition and channel; switching is one update.
            CREATE TABLE {table:definition_serving} (
                kind             {{key}} NOT NULL,
                name             {{key}} NOT NULL,
                channel          {{key}} NOT NULL,
                version          {{int}} NOT NULL,
                previous_version {{int}} NULL,
                switched_at      {{time}} NOT NULL,
                switched_by      {{key}} NOT NULL,
                PRIMARY KEY (kind, name, channel)
            );

            -- Append-only: one row for each publish, switch, rollback and retirement. Instances poll it with WHERE id > $last.
            CREATE TABLE {table:definition_event} (
                {{identity:id}},
                action       {{key}} NOT NULL,
                kind         {{key}} NOT NULL,
                name         {{key}} NOT NULL,
                channel      {{key}} NOT NULL,
                version      {{int}} NOT NULL,
                actor_id     {{key}} NOT NULL,
                happened_at  {{time}} NOT NULL
            );
            """,
            """
            -- Plan 30 (R15), appended: stage then activate, the files of a version, bindings per channel, what each instance has. Nothing above is changed; a database at migration 1 gains these and
            -- the old DefinitionStore keeps working on it. A version's manifest is its content column (opaque text, the kind's business); a version published with files hashes its content and file list.
            ALTER TABLE {table:definition_version} {{addcolumn}} progress {{text}} NULL;
            ALTER TABLE {table:definition_version} {{addcolumn}} approved_by {{key}} NULL;
            ALTER TABLE {table:definition_version} {{addcolumn}} approved_at {{time}} NULL;

            -- staged_version is the version prepared but not yet active. version is 0 while a channel has a staged version and nothing active yet (no version is numbered 0).
            ALTER TABLE {table:definition_serving} {{addcolumn}} staged_version {{int}} NULL;
            ALTER TABLE {table:definition_serving} {{addcolumn}} staged_at {{time}} NULL;
            ALTER TABLE {table:definition_serving} {{addcolumn}} staged_by {{key}} NULL;

            -- note carries what an event needs to say beyond its columns: a bound slot's name, an administrator's choice to activate although instances failed.
            ALTER TABLE {table:definition_event} {{addcolumn}} note {{text}} NULL;

            -- The files of a version: path within the version, SHA-256, size, media type, and the payload that holds the bytes (PayloadStore). A file unchanged between versions is one payload.
            CREATE TABLE {table:definition_artifact} (
                kind        {{key}} NOT NULL,
                name        {{key}} NOT NULL,
                version     {{int}} NOT NULL,
                path        {{key}} NOT NULL,
                sha256      {{key}} NOT NULL,
                size        {{int}} NOT NULL,
                media_type  {{key}} NULL,
                payload_id  {{uuid}} NOT NULL,
                PRIMARY KEY (kind, name, version, path)
            );
            CREATE INDEX {index:definition_artifact_by_sha} ON {table:definition_artifact}(sha256);

            -- What a slot a definition declares is bound to in one channel, and how many times it was changed (revision, the optimistic-concurrency token of a bind).
            CREATE TABLE {table:definition_binding} (
                channel     {{key}} NOT NULL,
                kind        {{key}} NOT NULL,
                name        {{key}} NOT NULL,
                slot        {{key}} NOT NULL,
                bound_to    {{key}} NOT NULL,
                revision    {{int}} NOT NULL,
                bound_by    {{key}} NOT NULL,
                bound_at    {{time}} NOT NULL,
                PRIMARY KEY (channel, kind, name, slot)
            );

            -- The version one instance has of a definition in a channel and its state (preparing, ready, active, failed). instance is a runner instance's or a serving instance's id, as text.
            -- gone_at is set when the instance left the group (its lease expired); retention removes such rows.
            CREATE TABLE {table:definition_instance_status} (
                instance     {{key}} NOT NULL,
                kind         {{key}} NOT NULL,
                name         {{key}} NOT NULL,
                channel      {{key}} NOT NULL,
                version      {{int}} NOT NULL,
                state        {{key}} NOT NULL,
                error_text   {{text}} NULL,
                reported_at  {{time}} NOT NULL,
                gone_at      {{time}} NULL,
                PRIMARY KEY (instance, kind, name, channel)
            );
            CREATE INDEX {index:definition_instance_status_by_definition} ON {table:definition_instance_status}(kind, name, channel);
            """,
            """
            -- Plan 28a R17: what an instance found when it prepared a version beyond ready or failed. note_text is the instance's own words (for a runner: what the dry run of the progress migration
            -- would do, the losses first); has_losses is 1 when that dry run would lose progress, which keeps the version from activating by itself until an administrator confirms the losses.
            ALTER TABLE {table:definition_instance_status} {{addcolumn}} note_text {{text}} NULL;
            ALTER TABLE {table:definition_instance_status} {{addcolumn}} has_losses {{int}} NOT NULL DEFAULT 0;

            -- Plan 28a R18: while an activation waits for the runs of a package in flight, no new run of it starts. One row per definition and channel, with a lease (expires_at): a holder that dies
            -- does not hold forever.
            CREATE TABLE {table:definition_activation_hold} (
                kind        {{key}} NOT NULL,
                name        {{key}} NOT NULL,
                channel     {{key}} NOT NULL,
                held_by     {{key}} NOT NULL,
                held_at     {{time}} NOT NULL,
                expires_at  {{time}} NOT NULL,
                PRIMARY KEY (kind, name, channel)
            );
            """,
        ],
        // an instance that left the group: its status rows are history after the retention age
        retention: [new RetentionRule("definition_instance_status", "gone_at")]);
}

/// <summary>
/// The <c>app_storage</c> module (plan 26 section 2a, plan 29 section 3.4): small JSON values an app keeps through the host. Kept per group, keyed by app instance. The value stays
/// text exactly as the app wrote it (a <c>jsonb</c> column would reorder its keys and change its size, which the quota counts); <c>size</c> is its UTF-8 length so an instance's total is a
/// sum and not a scan. The store that uses it moves with the serving layer (plan 29a Q16).
/// </summary>
public static class AppStorageModule
{
    public const string Name = "app_storage";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- scope is 'user' (owner is the person's id) or 'instance' (owner is empty). instance is the app instance, so two instances of one app never share a row.
            -- row_version counts up from 1 for a key and is what an optional if_version check compares.
            CREATE TABLE {table:entry} (
                instance     {{key}} NOT NULL,
                scope        {{key}} NOT NULL,
                owner        {{key}} NOT NULL,
                stored_key   {{key}} NOT NULL,
                value        {{text}} NOT NULL,
                size         {{int}} NOT NULL,
                row_version  {{int}} NOT NULL,
                updated_at   {{time}} NOT NULL,
                PRIMARY KEY (instance, scope, owner, stored_key)
            );
            """,
        ]);
}
