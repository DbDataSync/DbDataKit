using System.Data.Common;

namespace DbDataKit.State;

/// <summary>The states of a suggestion (plan 27a KN19, KN20): the edge writes <c>open</c> and <c>withdrawn</c>; the rest are the Workspace server's and the owner's.</summary>
public static class SuggestionStates
{
    public const string Open = "open";
    public const string Withdrawn = "withdrawn";

    /// <summary>The item moved on since the suggestion was based on it (KN20): shown beside the current text, not merged.</summary>
    public const string NeedsAnotherLook = "needs-another-look";

    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly string[] All = [Open, Withdrawn, NeedsAnotherLook, Approved, Rejected];
}

/// <summary>The kinds of suggestion: a change to an item, a new item, and a verified query made from an answer (KN23).</summary>
public static class SuggestionKinds
{
    public const string Change = "change";
    public const string New = "new";
    public const string VerifiedQuery = "verified-query";

    public static readonly string[] All = [Change, New, VerifiedQuery];
}

/// <summary>A suggestion as the edge keeps it: what is proposed (the whole file), what it was based on, who proposed it and how, who owns the item and where the suggestion stands.</summary>
public sealed record SuggestionRow(string Id, string Project, string ItemId, string Kind, string Title, string Proposed, string? BaseVersion, string Reason, string SuggestedBy, string Via, string? Owner, string State,
    string? Preview, string? DecidedBy, DateTimeOffset? DecidedAt, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>The suggestion came from an agent (<c>agent:name</c>), not straight from a person.</summary>
    public bool FromAgent => Via.StartsWith("agent:", StringComparison.Ordinal);
}

/// <summary>
/// Writes and reads the suggestions of the <c>knowledge</c> module (plan 27a KN19). A suggestion is a record in the group's state and nothing else: it is written whether or not a Workspace server exists, and the store knows rows, not who may see them
/// (the library above it decides that). A person may withdraw their own suggestion while it has not been decided; withdrawing changes nothing else.
/// </summary>
public sealed class SuggestionStore(StateDatabase database)
{
    private const string Module = KnowledgeModule.Name;

    private const string Columns = "id, project, item_id, kind, title, proposed, base_version, reason, suggested_by, via, owner, state, preview, decided_by, decided_at, note, created_at, updated_at";

    private static SuggestionRow Read(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Text(r, 6), r.GetString(7), r.GetString(8), r.GetString(9), Text(r, 10),
        r.GetString(11), Text(r, 12), Text(r, 13), r.NullableTime(14), Text(r, 15), r.Time(16), r.Time(17));

    private static string? Text(DbDataReader r, int at) => r.IsDBNull(at) ? null : r.GetString(at);

    /// <summary>Records a new suggestion, open; the id and the times are made here. The text, the reason and who are required.</summary>
    public SuggestionRow Add(string project, string itemId, string kind, string title, string proposed, string? baseVersion, string reason, string suggestedBy, string via, string? owner, string? preview = null)
    {
        foreach (var (name, value) in new[] { ("project", project), ("itemId", itemId), ("title", title), ("proposed", proposed), ("reason", reason), ("suggestedBy", suggestedBy), ("via", via) }) ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (!SuggestionKinds.All.Contains(kind)) throw new ArgumentException($"'{kind}' is not a kind of suggestion ({string.Join(", ", SuggestionKinds.All)}).", nameof(kind));
        var now = database.Time.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:suggestion} (id, project, item_id, kind, title, proposed, base_version, reason, suggested_by, via, owner, state, preview, created_at, updated_at) VALUES ($id, $project, $item, $kind, $title, $proposed, $base, $reason, $by, $via, $owner, $state, $preview, $at, $at);");
            insert.Bind(database, "id", id).Bind(database, "project", project).Bind(database, "item", itemId).Bind(database, "kind", kind).Bind(database, "title", title).Bind(database, "proposed", proposed)
                .Bind(database, "base", baseVersion, System.Data.DbType.String).Bind(database, "reason", reason).Bind(database, "by", suggestedBy).Bind(database, "via", via).Bind(database, "owner", owner, System.Data.DbType.String)
                .Bind(database, "state", SuggestionStates.Open).Bind(database, "preview", preview, System.Data.DbType.String).Bind(database, "at", now);
            insert.ExecuteNonQuery();
        });
        return Get(id)!;
    }

    public SuggestionRow? Get(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:suggestion}} WHERE id = $id;");
            command.Bind(database, "id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    /// <summary>
    /// The suggestions a person may see: their own, and those for an item owned by a group they are in (<paramref name="owners"/>); newest first, those in <paramref name="state"/> when it is given. What a person may see is decided here and nowhere
    /// else, in the query, so no caller can list more by forgetting to filter.
    /// </summary>
    public IReadOnlyList<SuggestionRow> Visible(string person, IReadOnlyCollection<string> owners, string? state = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var groups = owners.Select((_, i) => $"$g{i}").ToList();
            var visible = groups.Count == 0 ? "suggested_by = $person" : $"(suggested_by = $person OR owner IN ({string.Join(", ", groups)}))";
            using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:suggestion}} WHERE {visible}{(state is null ? "" : " AND state = $state")} ORDER BY created_at DESC, id;");
            command.Bind(database, "person", person);
            var i = 0;
            foreach (var group in owners) command.Bind(database, $"g{i++}", group);
            if (state is not null) command.Bind(database, "state", state);
            using var reader = command.ExecuteReader();
            var rows = new List<SuggestionRow>();
            while (reader.Read()) rows.Add(Read(reader));
            return (IReadOnlyList<SuggestionRow>)rows;
        });

    /// <summary>Every suggestion in a state, for the Workspace server that follows them (KN20) and for counts; no one's view.</summary>
    public IReadOnlyList<SuggestionRow> InState(string state) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:suggestion}} WHERE state = $state ORDER BY created_at, id;");
            command.Bind(database, "state", state);
            using var reader = command.ExecuteReader();
            var rows = new List<SuggestionRow>();
            while (reader.Read()) rows.Add(Read(reader));
            return (IReadOnlyList<SuggestionRow>)rows;
        });

    /// <summary>Withdraws a suggestion its person made while it is open (or waiting for another look); false when it is not theirs, is not there or has been decided.</summary>
    public bool Withdraw(string id, string person)
    {
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection,
                "UPDATE {table:suggestion} SET state = $withdrawn, decided_by = $person, decided_at = $at, updated_at = $at WHERE id = $id AND suggested_by = $person AND state IN ($open, $look);");
            update.Bind(database, "withdrawn", SuggestionStates.Withdrawn).Bind(database, "person", person).Bind(database, "at", now).Bind(database, "id", id).Bind(database, "open", SuggestionStates.Open).Bind(database, "look", SuggestionStates.NeedsAnotherLook);
            return update.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>Moves a suggestion to a state with who did it and a note (the Workspace server's and the owner's steps, KN20); false when it is not in <paramref name="from"/>.</summary>
    public bool Decide(string id, string from, string to, string by, string? note)
    {
        if (!SuggestionStates.All.Contains(to)) throw new ArgumentException($"'{to}' is not a state of a suggestion.", nameof(to));
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, "UPDATE {table:suggestion} SET state = $to, decided_by = $by, decided_at = $at, note = $note, updated_at = $at WHERE id = $id AND state = $from;");
            update.Bind(database, "to", to).Bind(database, "by", by).Bind(database, "at", now).Bind(database, "note", note, System.Data.DbType.String).Bind(database, "id", id).Bind(database, "from", from);
            return update.ExecuteNonQuery() > 0;
        });
    }
}
