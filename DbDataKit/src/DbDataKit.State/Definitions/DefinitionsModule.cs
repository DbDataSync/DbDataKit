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
        ]);
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
