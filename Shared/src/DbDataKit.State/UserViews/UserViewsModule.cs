namespace DbDataKit.State;

/// <summary>
/// The <c>user_views</c> module (plan 32 DBR12): the dashboards and reports **people make for themselves** in the serving layer, kept per group so that every instance of a group shares them (the same scope as the
/// published definitions they sit beside, plan 29a Q16). Nothing here is a published definition: no registry, no channel, no approval; a person's document is theirs until they share it, and it is resolved as whoever opens it.
/// <para>
/// <b>Three tables.</b> <c>user_view</c> holds the <b>current</b> version of each document, keyed by its owner, kind and name (unique), with the text as saved, its title and who changed it last.
/// <c>user_view_version</c> holds the versions that were <b>replaced</b> (so an edit can be undone and the history only grows): it is history, so it has a retention rule (a replaced version is kept for
/// <see cref="HistoryDays"/> days, then removed by the shared retention job; an administrator can lengthen or shorten it with the retention settings, and a document's current version is never removed because it is not in this table).
/// <c>user_view_share</c> says who else may open a document: <c>user:alice</c>, <c>group:sales</c> (a role of the caller) or <c>*</c> (anyone who can sign in); a share goes with the document and has no retention of its own.
/// </para>
/// <para>Migrations are appended, never edited; a program that reads a database whose <c>user_views</c> version is newer than its own refuses it by the state module version check.</para>
/// </summary>
public static class UserViewsModule
{
    public const string Name = "user_views";

    /// <summary>How long a replaced version is kept (a year: long enough to undo a change somebody notices late, short enough that a dashboard edited every day does not grow for ever).</summary>
    public const double HistoryDays = 365;

    private static readonly string[] Migrations =
    [
        """
        -- A person's dashboard or report as it is now. kind is dashboard or report; owner is the person's id (what the identity home calls them, lower case: never a display name, which two people may share), owner_name
        -- their name as of the last save (for lists); name is the document's name among that owner's. text is the document as saved
        -- (YAML or JSON, validated when it was saved); title is read from it for the lists. version counts saves from 1; a save quotes the version it read and a stale one is refused.
        CREATE TABLE {table:user_view} (
            id          {{uuid}} PRIMARY KEY,
            owner       {{key}} NOT NULL,
            owner_name  {{key}} NOT NULL,
            kind        {{key}} NOT NULL,
            name        {{key}} NOT NULL,
            version     {{int}} NOT NULL,
            title       {{text}} NOT NULL,
            doc_text    {{text}} NOT NULL,
            created_at  {{time}} NOT NULL,
            updated_at  {{time}} NOT NULL,
            updated_by  {{key}} NOT NULL
        );
        CREATE UNIQUE INDEX {index:user_view_by_name} ON {table:user_view}(owner, kind, name);

        -- The versions a document had before its current one (history, retention: replaced versions older than a year go). saved_at is when that version was saved and saved_by who saved it.
        CREATE TABLE {table:user_view_version} (
            view_id   {{uuid}} NOT NULL,
            version   {{int}} NOT NULL,
            doc_text  {{text}} NOT NULL,
            saved_by  {{key}} NOT NULL,
            saved_at  {{time}} NOT NULL,
            PRIMARY KEY (view_id, version)
        );
        CREATE INDEX {index:user_view_version_by_time} ON {table:user_view_version}(saved_at);

        -- Who else may open a document: 'user:<id or handle>' (the person's id, or their name in lower case with spaces as '-'), 'group:sales' or '*'. Sharing shares no data: the document runs as whoever opens it. No retention: a share goes with its document.
        CREATE TABLE {table:user_view_share} (
            view_id    {{uuid}} NOT NULL,
            grantee    {{key}} NOT NULL,
            shared_by  {{key}} NOT NULL,
            shared_at  {{time}} NOT NULL,
            PRIMARY KEY (view_id, grantee)
        );
        CREATE INDEX {index:user_view_share_by_grantee} ON {table:user_view_share}(grantee);
        """,
    ];

    /// <summary>The module, kept per group: the instances of a group share one set of personal documents, and two groups on one database each have their own.</summary>
    public static StateModule Module { get; } = new(Name, StateScopeKind.Group, Migrations, retention: [new RetentionRule("user_view_version", "saved_at", HistoryDays)]);
}
