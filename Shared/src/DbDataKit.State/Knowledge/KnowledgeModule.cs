namespace DbDataKit.State;

/// <summary>
/// The <c>knowledge</c> module (plan 27a KN07, text-search half): what the serving layer's knowledge module keeps so that knowledge can be found: the <b>items</b> of a channel (what they are about, who they are for, which version of which source
/// they came from), their <b>subjects</b> (one row each, so "everything about this model" is a lookup) and their <b>chunks</b> (the pieces a search returns, with the heading path they sit under). Everything is per <b>channel</b>, because an
/// index is the channel's: a channel is searched while another is being rebuilt.
/// <para>
/// <b>Search without a model.</b> <see cref="KnowledgeStore.Search"/> finds chunks by their words with the engine's own text search where it has one (SQLite FTS5, PostgreSQL <c>tsvector</c> with a GIN index) and by a portable term match where it
/// has none (SQL Server, whose full-text feature is an optional install this suite does not require). The text-search structures are derived data, made on first use and rebuilt from <c>chunk</c>; they are not part of the module's migrations, which
/// stay the same on every engine.
/// </para>
/// <para>
/// <b>Vectors are not here.</b> The vector index (SQL Server 2025's vector type, <c>pgvector</c>, <c>sqlite-vec</c>; KN07's other half and KN08) needs an engine feature, and a module that requires a feature refuses to be created without it. So vectors will be
/// a module of their own (<c>knowledge_vectors</c>, <c>requiresFeatures: [Vector]</c>) beside this one: search by words then works on every engine, SQL Server 2022 and the N-1 job included, and the vector half adds meaning-based search where the engine
/// can hold it. Migrations are appended, never edited.
/// </para>
/// </summary>
public static class KnowledgeModule
{
    public const string Name = "knowledge";

    private static readonly string[] Migrations =
    [
        """
        -- An item in a channel's index: one row per knowledge item (a Markdown file of a project, a generated entry). id is the item's own id; source_file where it came from, source the project or definition it was read from and version the
        -- published version (a short hash). role is reference, instruction or guardrail; audience is the item's for: block as JSON; about_implied says the subjects were given by the folder, not the file. hash is the hash of the item's text:
        -- an item whose hash did not change is not indexed again.
        CREATE TABLE {table:item} (
            channel       {{key}} NOT NULL,
            id            {{key}} NOT NULL,
            source_file   {{text}} NOT NULL,
            title         {{text}} NOT NULL,
            role          {{key}} NOT NULL,
            owner         {{key}} NULL,
            audience      {{text}} NOT NULL,
            about_implied {{int}} NOT NULL,
            hash          {{key}} NOT NULL,
            source        {{text}} NOT NULL,
            version       {{key}} NOT NULL,
            indexed_at    {{time}} NOT NULL,
            PRIMARY KEY (channel, id)
        );
        CREATE INDEX {index:item_by_owner} ON {table:item}(channel, owner);

        -- What an item is about, as the stable key of the subject (semantic-model:Sales/measure:revenue). One row per subject.
        CREATE TABLE {table:item_subject} (
            channel {{key}} NOT NULL,
            item_id {{key}} NOT NULL,
            subject {{key}} NOT NULL,
            PRIMARY KEY (channel, item_id, subject)
        );
        CREATE INDEX {index:item_subject_by_subject} ON {table:item_subject}(channel, subject);

        -- The pieces of an item a search returns: the text, the headings it sits under (joined with ' > '), its size in tokens, the hash of what is indexed and the line it starts at, so the pieces of one section can be joined again.
        CREATE TABLE {table:chunk} (
            channel      {{key}} NOT NULL,
            item_id      {{key}} NOT NULL,
            seq          {{int}} NOT NULL,
            heading_path {{text}} NOT NULL,
            content      {{text}} NOT NULL,
            tokens       {{int}} NOT NULL,
            hash         {{key}} NOT NULL,
            start_line   {{int}} NOT NULL,
            PRIMARY KEY (channel, item_id, seq)
        );
        """,
        """
        -- Verified queries (KN21): what an item of kind verified-query adds to its row. The pinned request (JSON; the request is the truth), the semantic SQL kept for reading, the model it is asked of, who verified it, when and against which model version.
        -- status is 'verified' or 'needs-reverification' (KN22: a new version of the model no longer answers it the way it did; the item is still found, no longer given as verified); status_note says why. The questions are the item's chunks.
        CREATE TABLE {table:verified_query} (
            channel       {{key}} NOT NULL,
            item_id       {{key}} NOT NULL,
            model         {{key}} NOT NULL,
            request       {{text}} NULL,
            saved_query   {{key}} NULL,
            saved_version {{key}} NULL,
            semantic_sql  {{text}} NULL,
            verified_by   {{key}} NOT NULL,
            verified_on   {{key}} NOT NULL,
            model_version {{key}} NOT NULL,
            status        {{key}} NOT NULL,
            status_note   {{text}} NULL,
            PRIMARY KEY (channel, item_id)
        );
        CREATE INDEX {index:verified_query_by_model} ON {table:verified_query}(channel, model);
        """,
        """
        -- Suggestions at the edge (KN19): a proposal to change an item or to add one, written here and nowhere else, so it is kept when the Workspace server is down. project is the knowledge project the item is in; item_id the item
        -- (the new item's id for a suggestion of a new one); kind is 'change', 'new' or 'verified-query'; proposed is the whole text of the file as proposed; base_version is the hash of the item's text it was based on (null for a new
        -- one), so a suggestion whose item moved on can be told; reason is why; suggested_by is the person, via 'person' or 'agent:<name>'; owner is the group that owns the item (the one who may decide); state is open, withdrawn,
        -- needs-another-look, approved or rejected (the last three are the Workspace server's, KN20); preview is the JSON of a verified query's result as the person saw it (KN23).
        CREATE TABLE {table:suggestion} (
            id           {{key}} NOT NULL,
            project      {{key}} NOT NULL,
            item_id      {{key}} NOT NULL,
            kind         {{key}} NOT NULL,
            title        {{text}} NOT NULL,
            proposed     {{text}} NOT NULL,
            base_version {{key}} NULL,
            reason       {{text}} NOT NULL,
            suggested_by {{key}} NOT NULL,
            via          {{key}} NOT NULL,
            owner        {{key}} NULL,
            state        {{key}} NOT NULL,
            preview      {{text}} NULL,
            decided_by   {{key}} NULL,
            decided_at   {{time}} NULL,
            note         {{text}} NULL,
            created_at   {{time}} NOT NULL,
            updated_at   {{time}} NOT NULL,
            PRIMARY KEY (id)
        );
        CREATE INDEX {index:suggestion_by_person} ON {table:suggestion}(suggested_by, state);
        CREATE INDEX {index:suggestion_by_owner} ON {table:suggestion}(owner, state);
        CREATE INDEX {index:suggestion_by_item} ON {table:suggestion}(project, item_id);
        """,
    ];

    private static StateModule Make(StateScopeKind scope) => new(Name, scope, Migrations);

    /// <summary>The module kept once per install (the console's own knowledge, when it is indexed).</summary>
    public static StateModule Module { get; } = Make(StateScopeKind.Install);

    /// <summary>The module kept per serving group: the group's knowledge module (KN11).</summary>
    public static StateModule GroupModule { get; } = Make(StateScopeKind.Group);
}
