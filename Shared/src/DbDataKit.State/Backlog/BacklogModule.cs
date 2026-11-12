namespace DbDataKit.State;

/// <summary>
/// The <c>backlog</c> module (plan 27a BK01, plan 27 section 5.1): what DbDataBacklog keeps, the application's own structure and never a DbDataStore model (Store models are authored after install and can only <i>augment</i> an item with custom attributes, EA01).
/// One copy per install (<see cref="Module"/>: the console hosts the first screens); the serving layer's <c>backlog</c> module (BK02) keeps <see cref="GroupModule"/>, the same migrations.
/// <para>
/// <b>What is in it.</b> A <c>project</c> is a backlog of its own (its key, such as <c>DATA</c>, names its items <c>DATA-12</c>; <c>next_number</c> is the counter, taken inside the transaction that makes the item). An <c>item</c> is an epic, story, task or bug with its state,
/// priority, estimate, assignee, the epic it belongs to, the sprint it is planned in and its place in the backlog (<c>sort_key</c>, spaced so a move rewrites one row). A <c>sprint</c> is planned, active or closed. A <c>board</c> maps columns to states. <c>comment</c>, <c>link</c>
/// (to another item, or to a suite object, a workspace or a pull request by the identity the other side uses; <c>state</c> says whether the target still exists) and <c>attachment</c> (the bytes are a payload of the payload store; the row names it) belong to an item.
/// <c>history</c> is the record of who changed what and when: one row per change, in the same transaction as the change.
/// </para>
/// <para>
/// <b>Retention.</b> No age rule. Everything here is current state or the record of how it came to be; an item's rows go with the item (<see cref="BacklogStore.DeleteItem"/>, an act a lead does). An attachment's payload is owned by its row (<see cref="PayloadOwner"/>): the shared retention job removes a payload whose attachment row has gone.
/// The rules (who may move an item to which state, what a transition requires) are not in the module: they are the library's (<c>DbDataKit.Backlog</c>), so a rule can change in a release without a migration. Migrations are appended, never edited.
/// </para>
/// </summary>
public static class BacklogModule
{
    public const string Name = "backlog";

    /// <summary>The states a sprint has, as stored.</summary>
    public static IReadOnlyList<string> SprintStates { get; } = ["planned", "active", "closed"];

    /// <summary>The states of a link's target, as stored: <c>ok</c>, or <c>dropped</c> when the thing it names is gone (the link stays, flagged).</summary>
    public static IReadOnlyList<string> LinkStates { get; } = ["ok", "dropped"];

    private static readonly string[] Migrations =
    [
        """
        -- A backlog of its own. id is its key (upper-case letters and digits, such as DATA); an item is named <id>-<number>. next_number is the number the next item gets: it moves in the transaction that makes the item, so two people never get one number.
        CREATE TABLE {table:project} (
            id          {{key}} PRIMARY KEY,
            name        {{key}} NOT NULL,
            description {{text}} NULL,
            next_number {{int}} NOT NULL,
            created_at  {{time}} NOT NULL,
            created_by  {{key}} NOT NULL
        );

        -- A board of a project: columns_doc is the columns as JSON, in order, each with a name and the states it shows (a state is in one column).
        CREATE TABLE {table:board} (
            project_id  {{key}} NOT NULL,
            id          {{key}} NOT NULL,
            name        {{key}} NOT NULL,
            columns_doc {{text}} NOT NULL,
            PRIMARY KEY (project_id, id)
        );

        -- A work item. item_type is epic, story, task or bug; state is a state of the workflow (the library's rules say which moves are allowed); priority is lowest, low, medium, high or highest. estimate is in points, null until someone gives one.
        -- epic_id is the epic it belongs to (an item of type epic in the same project), sprint_id the sprint it is planned in (null: in the backlog). sort_key orders the backlog and the columns (spaced by 1024; the library renumbers when two meet).
        -- labels is a JSON list of lower-case words. resolution says how it ended and is required by the transitions that end it. closed_at is set when the item enters a state of the done category and cleared when it leaves. row_version moves with every change.
        CREATE TABLE {table:item} (
            id           {{uuid}} PRIMARY KEY,
            project_id   {{key}} NOT NULL,
            item_number  {{int}} NOT NULL,
            item_type    {{key}} NOT NULL,
            title        {{text}} NOT NULL,
            description  {{text}} NULL,
            state        {{key}} NOT NULL,
            priority     {{key}} NOT NULL,
            estimate     {{decimal}} NULL,
            assignee     {{key}} NULL,
            reporter     {{key}} NOT NULL,
            epic_id      {{uuid}} NULL,
            sprint_id    {{uuid}} NULL,
            sort_key     {{int}} NOT NULL,
            labels       {{text}} NOT NULL,
            resolution   {{text}} NULL,
            created_at   {{time}} NOT NULL,
            updated_at   {{time}} NOT NULL,
            closed_at    {{time}} NULL,
            row_version  {{int}} NOT NULL
        );
        CREATE UNIQUE INDEX {index:item_by_number} ON {table:item}(project_id, item_number);
        CREATE INDEX {index:item_by_order} ON {table:item}(project_id, state, sort_key);
        CREATE INDEX {index:item_by_sprint} ON {table:item}(sprint_id);
        CREATE INDEX {index:item_by_epic} ON {table:item}(epic_id);
        CREATE INDEX {index:item_by_assignee} ON {table:item}(assignee, state);

        -- A sprint: planned, then active, then closed (state). A project has at most one active sprint (the library refuses a second). planned_start_at and planned_end_at are what the team intended, started_at and closed_at what happened.
        CREATE TABLE {table:sprint} (
            id               {{uuid}} PRIMARY KEY,
            project_id       {{key}} NOT NULL,
            name             {{key}} NOT NULL,
            goal             {{text}} NULL,
            state            {{key}} NOT NULL,
            planned_start_at {{time}} NULL,
            planned_end_at   {{time}} NULL,
            started_at       {{time}} NULL,
            closed_at        {{time}} NULL,
            row_version      {{int}} NOT NULL
        );
        CREATE INDEX {index:sprint_by_project} ON {table:sprint}(project_id, state);

        -- A comment on an item: who wrote it (the person's id and the name they had then) and when; edited_at is set when the author changed it.
        CREATE TABLE {table:comment} (
            id          {{uuid}} PRIMARY KEY,
            item_id     {{uuid}} NOT NULL,
            author      {{key}} NOT NULL,
            author_name {{key}} NOT NULL,
            body        {{text}} NOT NULL,
            created_at  {{time}} NOT NULL,
            edited_at   {{time}} NULL
        );
        CREATE INDEX {index:comment_by_item} ON {table:comment}(item_id, created_at);

        -- A link from an item. link_kind is relates, blocks or duplicates (to another item: target_kind item, target_ref the item's id), or object, workspace or pull-request (to something outside the module: target_kind says what, such as build-model or
        -- semantic-model, and target_ref is the identity the other side uses; plan 27a BK05, BK06). state is ok, or dropped when the target is gone: the link is flagged, never deleted.
        CREATE TABLE {table:link} (
            id          {{uuid}} PRIMARY KEY,
            item_id     {{uuid}} NOT NULL,
            link_kind   {{key}} NOT NULL,
            target_kind {{key}} NOT NULL,
            target_ref  {{key}} NOT NULL,
            label       {{text}} NULL,
            state       {{key}} NOT NULL,
            created_by  {{key}} NOT NULL,
            created_at  {{time}} NOT NULL
        );
        CREATE UNIQUE INDEX {index:link_unique} ON {table:link}(item_id, link_kind, target_kind, target_ref);
        CREATE INDEX {index:link_by_target} ON {table:link}(target_kind, target_ref);

        -- An attachment: the bytes are a payload of the payload store (payload_id; the payload's owner row is this row's id), written before this row; the row names the file, its type and size and who added it.
        CREATE TABLE {table:attachment} (
            id         {{uuid}} PRIMARY KEY,
            item_id    {{uuid}} NOT NULL,
            file_name  {{key}} NOT NULL,
            media_type {{key}} NOT NULL,
            size       {{int}} NOT NULL,
            payload_id {{uuid}} NOT NULL,
            added_by   {{key}} NOT NULL,
            added_at   {{time}} NOT NULL
        );
        CREATE INDEX {index:attachment_by_item} ON {table:attachment}(item_id);

        -- Who changed what, one row per change in the order it happened (seq counts per item). kind is created, field (a field changed: field_name, old_value, new_value), transition, comment, link, attachment, sprint or rank. actor is the person's id,
        -- actor_name the name they had then. The row is written in the transaction of the change, so there is no change without it.
        CREATE TABLE {table:history} (
            item_id     {{uuid}} NOT NULL,
            seq         {{int}} NOT NULL,
            actor       {{key}} NOT NULL,
            actor_name  {{key}} NOT NULL,
            kind        {{key}} NOT NULL,
            field_name  {{key}} NULL,
            old_value   {{text}} NULL,
            new_value   {{text}} NULL,
            occurred_at {{time}} NOT NULL,
            PRIMARY KEY (item_id, seq)
        );
        CREATE INDEX {index:history_by_actor} ON {table:history}(actor, occurred_at);
        """,
    ];

    private static StateModule Make(StateScopeKind scope) => new(Name, scope, Migrations, dependsOn: [PayloadStore.Module], payloadOwners: [new PayloadOwner("attachment", "id", KeyIsUuid: true)]);

    /// <summary>The module kept once per install: the console's backlog.</summary>
    public static StateModule Module { get; } = Make(StateScopeKind.Install);

    /// <summary>The same module kept per group, for the serving layer's <c>backlog</c> module (BK02).</summary>
    public static StateModule GroupModule { get; } = Make(StateScopeKind.Group);
}
