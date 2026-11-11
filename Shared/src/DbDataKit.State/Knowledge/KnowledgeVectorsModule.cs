namespace DbDataKit.State;

/// <summary>
/// The <c>knowledge_vectors</c> module (plan 27a KN07 and KN08, owner decision 2026-11-11): meaning-based search for the knowledge of a channel, <b>beside</b> the <c>knowledge</c> module and not part of it. It requires the engine feature
/// <see cref="EngineFeature.Vector"/> (SQL Server 2025's <c>vector</c> type, PostgreSQL's <c>pgvector</c>), so it refuses to be created where that is missing, with the refusal of plan 29b U01; the <c>knowledge</c> module and the search by words need no feature and
/// run on every engine, SQL Server 2022 and SQLite included. SQLite has no vector index here (<c>sqlite-vec</c> is a prerelease package and is skipped).
/// <para>
/// <b>One model per index.</b> <c>vector_index</c> records, per channel, the embedding model's id and the number of dimensions, and whether the index is <c>building</c> or <c>ready</c>. A channel searches the ready one; binding another model starts a second,
/// building index beside it, and the switch is one transaction that makes the new one ready and removes the old (<see cref="KnowledgeVectorStore.Complete"/>).
/// </para>
/// <para>
/// <b>The vectors themselves</b> are in a table per dimension, <c>chunk_vector_&lt;n&gt;</c>, made on first use of that dimension (<c>vector(n)</c> cannot be written into a migration that every model and every engine shares). Like the full-text structures of the
/// <c>knowledge</c> module they are <i>derived</i> data: every row names the hash of the chunk text it was made from, a search joins it to the chunk on that hash (so a chunk that changed has no vector until it is embedded again, and a removed item has none),
/// and <see cref="KnowledgeVectorStore.Sweep"/> removes what no longer matches a chunk: that is the retention of a vector, it lives exactly as long as its chunk's text. Embedding is a step of its own (<c>KnowledgeEmbedder</c>) and never blocks saving an item.
/// </para>
/// </summary>
public static class KnowledgeVectorsModule
{
    public const string Name = "knowledge_vectors";

    private static readonly string[] Migrations =
    [
        """
        {{feature:vector}};

        -- One row per embedding model of a channel's index: the model's id as the install knows it (connection/model), the number of dimensions of its vectors and whether it is the index searches use (ready) or one being built beside it (building).
        -- ready_at is when it became ready. A channel has at most one ready row and one building row.
        CREATE TABLE {table:vector_index} (
            channel    {{key}} NOT NULL,
            model_id   {{key}} NOT NULL,
            dimension  {{int}} NOT NULL,
            state      {{key}} NOT NULL,
            created_at {{time}} NOT NULL,
            ready_at   {{time}} NULL,
            PRIMARY KEY (channel, model_id)
        );

        -- A chunk that could not be embedded, for the model being built or in use: why, how many times it was tried and when last. A chunk with three failures is left to the search by words until someone asks to try again.
        CREATE TABLE {table:vector_failure} (
            channel   {{key}} NOT NULL,
            model_id  {{key}} NOT NULL,
            item_id   {{key}} NOT NULL,
            seq       {{int}} NOT NULL,
            attempts  {{int}} NOT NULL,
            message   {{text}} NOT NULL,
            failed_at {{time}} NOT NULL,
            PRIMARY KEY (channel, model_id, item_id, seq)
        );
        """,
    ];

    private static readonly RetentionRule[] Rules = [new RetentionRule("vector_failure", "failed_at")];

    private static StateModule Make(StateScopeKind scope, StateModule knowledge) => new(Name, scope, Migrations, dependsOn: [knowledge], retention: Rules, requiresFeatures: [EngineFeature.Vector]);

    /// <summary>The module kept once per install, beside <see cref="KnowledgeModule.Module"/>.</summary>
    public static StateModule Module { get; } = Make(StateScopeKind.Install, KnowledgeModule.Module);

    /// <summary>The module kept per serving group, beside <see cref="KnowledgeModule.GroupModule"/>.</summary>
    public static StateModule GroupModule { get; } = Make(StateScopeKind.Group, KnowledgeModule.GroupModule);
}
