using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.State;

/// <summary>An item as the index keeps it (plan 27a KN07): who it is for as JSON, what it is about as subject keys, and where and at which version it came from.</summary>
public sealed record StoredItem(string Id, string File, string Title, string Role, string? Owner, string Audience, bool AboutImplied, string Hash, string Source, string Version, IReadOnlyList<string> Subjects, DateTimeOffset IndexedAt);

/// <summary>A piece of an item: its place (<see cref="Seq"/>), the headings above it joined with <c>" > "</c>, its text and where it starts.</summary>
public sealed record StoredChunk(int Seq, string HeadingPath, string Text, int Tokens, string Hash, int StartLine);

/// <summary>A chunk the text search found and how well it matches (higher is better; only the order means anything, and only within one search).</summary>
public sealed record TextMatch(string ItemId, int Seq, double Score);

/// <summary>How a search by words is done on an engine.</summary>
public enum TextSearchMode
{
    /// <summary>SQLite FTS5: a table of its own, ranked by bm25.</summary>
    Fts5,

    /// <summary>PostgreSQL <c>tsvector</c> with a GIN index on an expression, ranked by <c>ts_rank_cd</c>.</summary>
    Tsvector,

    /// <summary>A portable term match, ranked by how many of the question's words a chunk has: SQL Server, whose full-text feature is an optional install, and any engine without a text search.</summary>
    TermMatch,
}

/// <summary>
/// The words of a question, as every engine's search takes them: lower case, letters and digits only, the words that carry no meaning left out, each once. A question is searched by <b>any</b> of its words and ranked by how well a chunk matches,
/// so "how is revenue recognised" finds the chunk about revenue recognition before the one that only says "revenue".
/// </summary>
public static class TextQuery
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "an", "of", "to", "is", "are", "was", "were", "in", "on", "for", "and", "or", "what", "how", "do", "does", "did", "we", "you", "it", "this", "that", "with", "by", "be", "as", "at", "from", "can", "which", "who", "when", "where", "why", "about",
    };

    public const int MaximumTerms = 12;

    public static IReadOnlyList<string> Terms(string question)
    {
        var terms = new List<string>();
        var word = new StringBuilder();
        void Flush()
        {
            if (word.Length == 0) return;
            var text = word.ToString().ToLowerInvariant();
            word.Clear();
            if (text.Length > 1 && !Stop.Contains(text) && !terms.Contains(text) && terms.Count < MaximumTerms) terms.Add(text);
        }
        foreach (var c in question) { if (char.IsLetterOrDigit(c)) word.Append(c); else Flush(); }
        Flush();
        return terms;
    }
}

/// <summary>
/// Writes and reads the <c>knowledge</c> module (<see cref="KnowledgeModule"/>) and finds chunks by their words. The store knows rows, not knowledge: the library above it maps items and chunks onto <see cref="StoredItem"/> and
/// <see cref="StoredChunk"/> and applies the access filter to what a search returns, so who may see what is never decided here.
/// </summary>
public sealed class KnowledgeStore(StateDatabase database)
{
    private const string Module = KnowledgeModule.Name;
    private readonly object _ensure = new();
    private bool _ensured;

    public TextSearchMode Mode => database.Dialect.Engine switch
    {
        StateEngineIds.Sqlite => TextSearchMode.Fts5,
        StateEngineIds.Postgres => TextSearchMode.Tsvector,
        _ => TextSearchMode.TermMatch,
    };

    /// <summary>The full-text table (SQLite): the chunk table's rendered name and <c>_fts</c>, so it lives where the chunks live (the layout does not know it: it is derived data, not part of the module).</summary>
    private string FtsTable => database.Layout!.Table(Module, "chunk") + "_fts";

    /// <summary>The name a full-text table answers to inside a query (<c>MATCH</c>, <c>bm25</c>): without the schema.</summary>
    private string FtsName => FtsTable[(FtsTable.LastIndexOf('.') + 1)..];

    /// <summary>The GIN index's name, from the chunk table's own (PostgreSQL: no schema in an index name).</summary>
    private string IndexName => database.Layout!.Table(Module, "chunk")[(database.Layout!.Table(Module, "chunk").LastIndexOf('.') + 1)..] + "_text_search";

    private bool IsMsSql => database.Dialect.Engine == StateEngineIds.MsSql;

    private string Concatenated(string alias = "") => IsMsSql ? $"{alias}heading_path + ' ' + {alias}content" : $"{alias}heading_path || ' ' || {alias}content";

    private static string Key(string channel, string itemId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel + "\u001f" + itemId))).ToLowerInvariant();

    private static string ChannelKey(string channel) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel))).ToLowerInvariant();

    /// <summary>Makes the engine's text-search structure the first time it is needed (derived data: it can always be made again from the chunks, see <see cref="Reindex"/>).</summary>
    private void EnsureTextIndex(DbConnection connection)
    {
        if (_ensured) return;
        lock (_ensure)
        {
            if (_ensured) return;
            switch (Mode)
            {
                case TextSearchMode.Fts5:
                    Run(connection, $"CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTable} USING fts5(text, item_key, channel_key, channel UNINDEXED, item_id UNINDEXED, seq UNINDEXED, tokenize = 'unicode61 remove_diacritics 2');");
                    break;
                case TextSearchMode.Tsvector:
                    Run(connection, $"CREATE INDEX IF NOT EXISTS {IndexName} ON {{table:chunk}} USING GIN (to_tsvector('simple', {Concatenated()}));");
                    break;
            }
            _ensured = true;
        }
    }

    private void Run(DbConnection connection, string sql)
    {
        using var command = database.Command(Module, connection, sql);
        command.ExecuteNonQuery();
    }

    // ---- writing ----

    /// <summary>
    /// Puts an item and its chunks in a channel's index, replacing what was there for it, in one transaction: a search never sees an item half written. Returns false, writing nothing, when the item is already there with the same hash
    /// and <paramref name="force"/> is not set (an item that did not change is not indexed again; the source and version it came from are brought up to date).
    /// </summary>
    public bool Put(string channel, StoredItem item, IReadOnlyList<StoredChunk> chunks, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTextIndex(connection);
            if (!force && Find(connection, channel, item.Id) is { } existing && existing.Hash == item.Hash)
            {
                // the text is the same: nothing is chunked or indexed again, but where it came from now (a new published version that did not change this item) is kept
                if (existing.Source != item.Source || existing.Version != item.Version)
                {
                    using var touch = database.Command(Module, connection, "UPDATE {table:item} SET source = $source, version = $version, indexed_at = $at WHERE channel = $channel AND id = $id;");
                    touch.Bind(database, "source", item.Source).Bind(database, "version", item.Version).Bind(database, "at", item.IndexedAt).Bind(database, "channel", channel).Bind(database, "id", item.Id);
                    touch.ExecuteNonQuery();
                }
                return false;
            }
            using var transaction = database.BeginTransaction(connection, Module);
            RemoveRows(connection, transaction, channel, item.Id);
            using (var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:item} (channel, id, source_file, title, role, owner, audience, about_implied, hash, source, version, indexed_at) VALUES ($channel, $id, $file, $title, $role, $owner, $audience, $implied, $hash, $source, $version, $at);"))
            {
                insert.Bind(database, "channel", channel).Bind(database, "id", item.Id).Bind(database, "file", item.File).Bind(database, "title", item.Title).Bind(database, "role", item.Role)
                    .Bind(database, "owner", item.Owner, System.Data.DbType.String).Bind(database, "audience", item.Audience).Bind(database, "implied", item.AboutImplied ? 1 : 0).Bind(database, "hash", item.Hash)
                    .Bind(database, "source", item.Source).Bind(database, "version", item.Version).Bind(database, "at", item.IndexedAt);
                insert.ExecuteNonQuery();
            }
            foreach (var subject in item.Subjects.Distinct(StringComparer.Ordinal))
            {
                using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:item_subject} (channel, item_id, subject) VALUES ($channel, $id, $subject);");
                insert.Bind(database, "channel", channel).Bind(database, "id", item.Id).Bind(database, "subject", subject);
                insert.ExecuteNonQuery();
            }
            foreach (var chunk in chunks)
            {
                using var insert = database.Command(Module, connection, transaction,
                    "INSERT INTO {table:chunk} (channel, item_id, seq, heading_path, content, tokens, hash, start_line) VALUES ($channel, $id, $seq, $heading, $text, $tokens, $hash, $line);");
                insert.Bind(database, "channel", channel).Bind(database, "id", item.Id).Bind(database, "seq", chunk.Seq).Bind(database, "heading", chunk.HeadingPath).Bind(database, "text", chunk.Text)
                    .Bind(database, "tokens", chunk.Tokens).Bind(database, "hash", chunk.Hash).Bind(database, "line", chunk.StartLine);
                insert.ExecuteNonQuery();
                if (Mode == TextSearchMode.Fts5) InsertFts(connection, transaction, channel, item.Id, chunk);
            }
            transaction.Commit();
            return true;
        });
    }

    private void InsertFts(DbConnection connection, DbTransaction transaction, string channel, string itemId, StoredChunk chunk)
    {
        using var insert = database.Command(Module, connection, transaction, $"INSERT INTO {FtsTable} (text, item_key, channel_key, channel, item_id, seq) VALUES ($text, $item_key, $channel_key, $channel, $id, $seq);");
        insert.Bind(database, "text", chunk.HeadingPath + " " + chunk.Text).Bind(database, "item_key", Key(channel, itemId)).Bind(database, "channel_key", ChannelKey(channel))
            .Bind(database, "channel", channel).Bind(database, "id", itemId).Bind(database, "seq", chunk.Seq);
        insert.ExecuteNonQuery();
    }

    /// <summary>Takes an item out of a channel's index; false when it was not there.</summary>
    public bool Remove(string channel, string itemId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTextIndex(connection);
            using var transaction = database.BeginTransaction(connection, Module);
            var removed = RemoveRows(connection, transaction, channel, itemId);
            transaction.Commit();
            return removed;
        });

    private bool RemoveRows(DbConnection connection, DbTransaction transaction, string channel, string itemId)
    {
        if (Mode == TextSearchMode.Fts5)
        {
            // the item's words are found by its key, an indexed token, so this does not read the whole table
            using var fts = database.Command(Module, connection, transaction, $"DELETE FROM {FtsTable} WHERE {FtsName} MATCH $match;");
            fts.Bind(database, "match", $"item_key : \"{Key(channel, itemId)}\"");
            fts.ExecuteNonQuery();
        }
        foreach (var table in new[] { "chunk", "item_subject" })
        {
            using var delete = database.Command(Module, connection, transaction, $"DELETE FROM {{table:{table}}} WHERE channel = $channel AND item_id = $id;");
            delete.Bind(database, "channel", channel).Bind(database, "id", itemId);
            delete.ExecuteNonQuery();
        }
        using var item = database.Command(Module, connection, transaction, "DELETE FROM {table:item} WHERE channel = $channel AND id = $id;");
        item.Bind(database, "channel", channel).Bind(database, "id", itemId);
        return item.ExecuteNonQuery() > 0;
    }

    /// <summary>Makes the text-search structure again from the chunks of every channel (after a restore, or if it is ever doubted). A no-op on engines whose search needs no structure.</summary>
    public void Reindex() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTextIndex(connection);
            if (Mode != TextSearchMode.Fts5) return;
            using var transaction = database.BeginTransaction(connection, Module);
            using (var clear = database.Command(Module, connection, transaction, $"DELETE FROM {FtsTable};")) clear.ExecuteNonQuery();
            using var read = database.Command(Module, connection, transaction, "SELECT channel, item_id, seq, heading_path, content FROM {table:chunk};");
            var all = new List<(string Channel, string Item, StoredChunk Chunk)>();
            using (var reader = read.ExecuteReader())
                while (reader.Read()) all.Add((reader.GetString(0), reader.GetString(1), new StoredChunk(reader.GetInt32(2), reader.GetString(3), reader.GetString(4), 0, "", 0)));
            foreach (var (channel, item, chunk) in all) InsertFts(connection, transaction, channel, item, chunk);
            transaction.Commit();
        });

    // ---- reading ----

    private const string ItemColumns = "id, source_file, title, role, owner, audience, about_implied, hash, source, version, indexed_at";

    private StoredItem ReadItem(DbDataReader r, IReadOnlyList<string> subjects) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
        Convert.ToInt32(r.GetValue(6), CultureInfo.InvariantCulture) != 0, r.GetString(7), r.GetString(8), r.GetString(9), subjects, r.Time(10));

    private StoredItem? Find(DbConnection connection, string channel, string id)
    {
        using var command = database.Command(Module, connection, $"SELECT {ItemColumns} FROM {{table:item}} WHERE channel = $channel AND id = $id;");
        command.Bind(database, "channel", channel).Bind(database, "id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader, []) : null;
    }

    private Dictionary<string, List<string>> SubjectsOf(DbConnection connection, string channel)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var command = database.Command(Module, connection, "SELECT item_id, subject FROM {table:item_subject} WHERE channel = $channel ORDER BY item_id, subject;");
        command.Bind(database, "channel", channel);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(reader.GetString(1));
        }
        return result;
    }

    /// <summary>One item of a channel with its subjects, or null.</summary>
    public StoredItem? Item(string channel, string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Find(connection, channel, id) is { } item ? item with { Subjects = SubjectsOf(connection, channel).GetValueOrDefault(id) ?? [] } : null;
        });

    /// <summary>Every item of a channel, by id, with its subjects.</summary>
    public IReadOnlyList<StoredItem> Items(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var subjects = SubjectsOf(connection, channel);
            using var command = database.Command(Module, connection, $"SELECT {ItemColumns} FROM {{table:item}} WHERE channel = $channel ORDER BY id;");
            command.Bind(database, "channel", channel);
            using var reader = command.ExecuteReader();
            var items = new List<StoredItem>();
            while (reader.Read()) items.Add(ReadItem(reader, subjects.GetValueOrDefault(reader.GetString(0)) ?? []));
            return (IReadOnlyList<StoredItem>)items;
        });

    /// <summary>The ids of the items of a channel that are about a subject (its key, as the lineage spells it).</summary>
    public IReadOnlyList<string> ItemsAbout(string channel, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT item_id FROM {table:item_subject} WHERE channel = $channel AND subject = $subject ORDER BY item_id;");
            command.Bind(database, "channel", channel).Bind(database, "subject", subject);
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return (IReadOnlyList<string>)ids;
        });

    /// <summary>The chunks of one item, in order.</summary>
    public IReadOnlyList<StoredChunk> Chunks(string channel, string itemId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT seq, heading_path, content, tokens, hash, start_line FROM {table:chunk} WHERE channel = $channel AND item_id = $id ORDER BY seq;");
            command.Bind(database, "channel", channel).Bind(database, "id", itemId);
            using var reader = command.ExecuteReader();
            var chunks = new List<StoredChunk>();
            while (reader.Read()) chunks.Add(new StoredChunk(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2), Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture), reader.GetString(4), Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
            return (IReadOnlyList<StoredChunk>)chunks;
        });

    /// <summary>How many items and chunks a channel's index holds.</summary>
    public (int Items, int Chunks) Counts(string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            int Count(string table)
            {
                using var command = database.Command(Module, connection, $"SELECT COUNT(*) FROM {{table:{table}}} WHERE channel = $channel;");
                command.Bind(database, "channel", channel);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return (Count("item"), Count("chunk"));
        });

    // ---- searching ----

    /// <summary>
    /// The chunks of a channel that have any of the question's words, best first (at most <paramref name="limit"/>). No access filter: who may see an item is the caller's to apply to what comes back, per item, at the time of the question.
    /// A question with no words of its own (all of them left out by <see cref="TextQuery"/>) finds nothing.
    /// </summary>
    public IReadOnlyList<TextMatch> Search(string channel, string question, int limit = 50)
    {
        var terms = TextQuery.Terms(question);
        if (terms.Count == 0) return [];
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            EnsureTextIndex(connection);
            using var command = Mode switch
            {
                TextSearchMode.Fts5 => FtsCommand(connection, channel, terms, limit),
                TextSearchMode.Tsvector => TsvectorCommand(connection, channel, terms, limit),
                _ => TermMatchCommand(connection, channel, terms, limit),
            };
            using var reader = command.ExecuteReader();
            var matches = new List<TextMatch>();
            while (reader.Read()) matches.Add(new TextMatch(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture)));
            return (IReadOnlyList<TextMatch>)matches;
        });
    }

    private DbCommand FtsCommand(DbConnection connection, string channel, IReadOnlyList<string> terms, int limit)
    {
        var match = $"text : ({string.Join(" OR ", terms.Select(t => $"\"{t}\""))}) AND channel_key : \"{ChannelKey(channel)}\"";
        var command = database.Command(Module, connection, $"SELECT item_id, seq, -bm25({FtsName}) AS score FROM {FtsTable} WHERE {FtsName} MATCH $match ORDER BY score DESC, item_id, seq {database.Limit("limit")};");
        return command.Bind(database, "match", match).Bind(database, "limit", limit);
    }

    private DbCommand TsvectorCommand(DbConnection connection, string channel, IReadOnlyList<string> terms, int limit)
    {
        var vector = $"to_tsvector('simple', {Concatenated("c.")})";
        var command = database.Command(Module, connection,
            $"SELECT c.item_id, c.seq, ts_rank_cd({vector}, q.query) AS score FROM {{table:chunk}} c, to_tsquery('simple', $query) AS q(query) WHERE c.channel = $channel AND {vector} @@ q.query ORDER BY score DESC, c.item_id, c.seq {database.Limit("limit")};");
        return command.Bind(database, "query", string.Join(" | ", terms)).Bind(database, "channel", channel).Bind(database, "limit", limit);
    }

    private DbCommand TermMatchCommand(DbConnection connection, string channel, IReadOnlyList<string> terms, int limit)
    {
        var haystack = $"LOWER({Concatenated()})";
        var hits = terms.Select((_, i) => $"(CASE WHEN {haystack} LIKE $t{i} THEN 1 ELSE 0 END)").ToList();
        var score = string.Join(" + ", hits);
        var command = database.Command(Module, connection,
            $"SELECT item_id, seq, ({score}) AS score FROM {{table:chunk}} WHERE channel = $channel AND ({score}) > 0 ORDER BY score DESC, item_id, seq {database.Limit("limit")};");
        for (var i = 0; i < terms.Count; i++) command.Bind(database, $"t{i}", "%" + terms[i] + "%");
        return command.Bind(database, "channel", channel).Bind(database, "limit", limit);
    }
}
