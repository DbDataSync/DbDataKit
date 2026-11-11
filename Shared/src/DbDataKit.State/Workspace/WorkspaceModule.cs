namespace DbDataKit.State;

/// <summary>
/// The <c>workspace</c> module (plan 22a GP31, decision D8): what the Workspace server (DbDataWorkspace) keeps in its state database. The git worktrees themselves are folders on the server's disk; this is the record of them and of what was done.
/// <para>
/// <b>Five tables.</b> <c>project</c> is each project the server hosts (an id, its kind, where its repository is); a project of any kind (an app today, an agent, an integration, a skill or a knowledge base when plan 27 adds them) is a row,
/// so the kinds need no second break. <c>workspace</c> is the drafts that exist now, one row each, removed when a draft is abandoned or merged. <c>workspace_event</c> is the history of what happened to them (created, committed, pushed,
/// merged, abandoned), kept for <see cref="EventDays"/> days and then removed by the shared retention job. <c>build</c> is each build a workspace asked for (none are made yet; the table and its store are what the build slices write to), kept for
/// <see cref="BuildDays"/> days. <c>publisher_key</c> names, for each target a version is published to, the <b>reference</b> to the secret that holds the publisher's key: the key itself is in the server's secret store, never in a row.
/// </para>
/// <para>
/// <b>Scope.</b> Kept per service: the folders belong to one server's disk, so two Workspace servers on one database each have their own copy.
/// Migrations are appended, never edited; a program that reads a database whose <c>workspace</c> version is newer than its own refuses it by the state module version check, naming the module and both versions.
/// </para>
/// </summary>
public static class WorkspaceModule
{
    public const string Name = "workspace";

    /// <summary>How long an event is kept (a year: long enough to answer who merged what, short enough that a busy server does not grow for ever).</summary>
    public const double EventDays = 365;

    /// <summary>How long a build's record is kept (90 days; the published version it made has its own history).</summary>
    public const double BuildDays = 90;

    /// <summary>The kinds of project the module knows; plan 27 adds the others by name, and a row with a kind not in this list is refused by <see cref="WorkspaceStore"/>.</summary>
    public static IReadOnlyList<string> ProjectKinds { get; } = ["app"];

    /// <summary>The statuses a build has, as stored.</summary>
    public static IReadOnlyList<string> BuildStatuses { get; } = ["queued", "running", "succeeded", "failed", "canceled"];

    private static readonly string[] Migrations =
    [
        """
        -- A project the server hosts. kind is app (plan 27 adds more). path is the repository's folder; null: the folder the server made under its own directory for it (projects/<id>).
        CREATE TABLE {table:project} (
            id          {{key}} PRIMARY KEY,
            kind        {{key}} NOT NULL,
            title       {{key}} NULL,
            path        {{text}} NULL,
            created_at  {{time}} NOT NULL,
            created_by  {{key}} NOT NULL
        );

        -- A draft that exists now: one person's git worktree of a project on the branch ws/<id>. id is what the API calls it (the owner and the name, ada-new-orders); owner is the person's id as the token carries it.
        -- base_commit is the commit the draft started at. A row goes when the draft is abandoned or merged: what happened stays in workspace_event.
        CREATE TABLE {table:workspace} (
            project      {{key}} NOT NULL,
            id           {{key}} NOT NULL,
            owner        {{key}} NOT NULL,
            owner_name   {{key}} NOT NULL,
            name         {{key}} NOT NULL,
            branch       {{key}} NOT NULL,
            base_commit  {{key}} NOT NULL,
            created_at   {{time}} NOT NULL,
            PRIMARY KEY (project, id)
        );
        CREATE INDEX {index:workspace_by_owner} ON {table:workspace}(owner, project);

        -- What happened to a draft (history, retention: a year). kind is created, committed, pushed, merged or abandoned; actor is the person's id, actor_name their name then; detail is a sentence (the commit message,
        -- the branch pushed), never a secret and never file content.
        CREATE TABLE {table:workspace_event} (
            id           {{uuid}} PRIMARY KEY,
            project      {{key}} NOT NULL,
            workspace_id {{key}} NOT NULL,
            kind         {{key}} NOT NULL,
            actor        {{key}} NOT NULL,
            actor_name   {{key}} NOT NULL,
            detail       {{text}} NULL,
            occurred_at  {{time}} NOT NULL
        );
        CREATE INDEX {index:workspace_event_by_workspace} ON {table:workspace_event}(project, workspace_id, occurred_at);
        CREATE INDEX {index:workspace_event_by_time} ON {table:workspace_event}(occurred_at);

        -- A build a draft asked for (history, retention: 90 days). status is queued, running, succeeded, failed or canceled; commit_sha is the commit built; message says why a build failed.
        CREATE TABLE {table:build} (
            id            {{uuid}} PRIMARY KEY,
            project       {{key}} NOT NULL,
            workspace_id  {{key}} NOT NULL,
            status        {{key}} NOT NULL,
            commit_sha    {{key}} NULL,
            requested_by  {{key}} NOT NULL,
            message       {{text}} NULL,
            requested_at  {{time}} NOT NULL,
            started_at    {{time}} NULL,
            finished_at   {{time}} NULL
        );
        CREATE INDEX {index:build_by_workspace} ON {table:build}(project, workspace_id, requested_at);
        CREATE INDEX {index:build_by_time} ON {table:build}(requested_at);

        -- The publisher's key reference: for a target a version is published to (a name the server's settings give it), the reference to the secret that holds the key (secret:dbdata:workspace:publisher:<name>).
        -- The key is never here. No retention: a reference stays until it is removed.
        CREATE TABLE {table:publisher_key} (
            name        {{key}} PRIMARY KEY,
            secret_ref  {{key}} NOT NULL,
            created_at  {{time}} NOT NULL,
            created_by  {{key}} NOT NULL,
            updated_at  {{time}} NOT NULL
        );
        """,
    ];

    /// <summary>The module, kept per service.</summary>
    public static StateModule Module { get; } = new(Name, StateScopeKind.Service, Migrations,
        retention: [new RetentionRule("workspace_event", "occurred_at", EventDays), new RetentionRule("build", "requested_at", BuildDays)]);
}
