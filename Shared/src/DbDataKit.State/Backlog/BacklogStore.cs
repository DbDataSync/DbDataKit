using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>A backlog of its own; <see cref="Id"/> is its key (<c>DATA</c>) and names its items.</summary>
public sealed record BacklogProjectRow(string Id, string Name, string? Description, long NextNumber, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>A board: <see cref="ColumnsDoc"/> is the columns as JSON, in order.</summary>
public sealed record BacklogBoardRow(string ProjectId, string Id, string Name, string ColumnsDoc);

/// <summary>A work item as stored. <see cref="Labels"/> is a JSON list of words; <see cref="SortKey"/> orders the backlog and the columns.</summary>
public sealed record BacklogItemRow(Guid Id, string ProjectId, long Number, string ItemType, string Title, string? Description, string State, string Priority, decimal? Estimate, string? Assignee, string Reporter,
    Guid? EpicId, Guid? SprintId, long SortKey, string Labels, string? Resolution, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ClosedAt, long RowVersion)
{
    /// <summary>What people call it: <c>DATA-12</c>.</summary>
    public string Key => $"{ProjectId}-{Number.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A sprint as stored (<see cref="BacklogModule.SprintStates"/>).</summary>
public sealed record BacklogSprintRow(Guid Id, string ProjectId, string Name, string? Goal, string State, DateTimeOffset? PlannedStartAt, DateTimeOffset? PlannedEndAt, DateTimeOffset? StartedAt, DateTimeOffset? ClosedAt, long RowVersion);

/// <summary>A comment on an item.</summary>
public sealed record BacklogCommentRow(Guid Id, Guid ItemId, string Author, string AuthorName, string Body, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);

/// <summary>A link from an item: to another item (<c>TargetKind</c> <c>item</c>, <c>TargetRef</c> its id) or to something outside the module by the identity the other side uses.</summary>
public sealed record BacklogLinkRow(Guid Id, Guid ItemId, string LinkKind, string TargetKind, string TargetRef, string? Label, string State, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>An attachment: the bytes are the payload <see cref="PayloadId"/> of the payload store.</summary>
public sealed record BacklogAttachmentRow(Guid Id, Guid ItemId, string FileName, string MediaType, long Size, Guid PayloadId, string AddedBy, DateTimeOffset AddedAt);

/// <summary>One change of an item: who, what, when, in the order it happened.</summary>
public sealed record BacklogHistoryRow(Guid ItemId, long Seq, string Actor, string ActorName, string Kind, string? Field, string? OldValue, string? NewValue, DateTimeOffset OccurredAt);

/// <summary>A change to put in the history in the transaction that makes it.</summary>
public sealed record BacklogChange(string Kind, string? Field = null, string? OldValue = null, string? NewValue = null);

/// <summary>The person a change is written for: their id, and the name they have now (kept with the row, because names change).</summary>
public sealed record BacklogActor(string Id, string Name);

/// <summary>What an item list is narrowed to. <see cref="Sprint"/> with <see cref="NoSprint"/> asks for the items in no sprint (the backlog proper).</summary>
public sealed record BacklogItemQuery(string? Project = null, IReadOnlyList<string>? States = null, Guid? Sprint = null, bool NoSprint = false, Guid? Epic = null, string? Assignee = null, string? ItemType = null, string? Text = null, int Limit = 500);

/// <summary>
/// Writes and reads the <c>backlog</c> module (<see cref="BacklogModule"/>). The store knows rows and keeps them true (a number is never given twice, a history row goes with its change, a stale write is refused by <c>row_version</c>); it does not know the rules of a workflow,
/// which are the library's (<c>DbDataKit.Backlog</c>). Every change that has an item takes the <see cref="BacklogActor"/> and writes its history in the same transaction.
/// </summary>
public sealed class BacklogStore(StateDatabase database)
{
    private const string Module = BacklogModule.Name;

    /// <summary>The database the store writes to, for a service that needs its clock.</summary>
    public StateDatabase Database => database;

    /// <summary>Now, by the database's clock (a test's clock moves it).</summary>
    public DateTimeOffset Now => database.Time.GetUtcNow();

    private static string? Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DbCommand Make(StateDatabase database, DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = database.Command(Module, connection, transaction, sql);
        foreach (var (name, value) in parameters) command.Bind(database, name, value);
        return command;
    }

    private T Read<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] parameters) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = Make(database, connection, null, sql, parameters);
            using var reader = command.ExecuteReader();
            return map(reader);
        });

    private IReadOnlyList<T> List<T>(string sql, Func<DbDataReader, T> row, params (string Name, object? Value)[] parameters) =>
        Read<IReadOnlyList<T>>(sql, reader =>
        {
            var rows = new List<T>();
            while (reader.Read()) rows.Add(row(reader));
            return rows;
        }, parameters);

    private T? One<T>(string sql, Func<DbDataReader, T> row, params (string Name, object? Value)[] parameters) where T : class =>
        Read(sql, reader => reader.Read() ? row(reader) : null, parameters);

    private T Write<T>(Func<DbConnection, DbTransaction, T> action) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            var result = action(connection, transaction);
            transaction.Commit();
            return result;
        });

    private void Execute(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Make(database, connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private long Scalar(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Make(database, connection, transaction, sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Writes the history rows of one change, numbered after the item's last.</summary>
    private void AppendHistory(DbConnection connection, DbTransaction transaction, Guid itemId, BacklogActor actor, DateTimeOffset at, IEnumerable<BacklogChange> changes)
    {
        var seq = Scalar(connection, transaction, "SELECT COALESCE(MAX(seq), 0) FROM {table:history} WHERE item_id = $item;", ("item", itemId));
        foreach (var change in changes)
        {
            seq++;
            Execute(connection, transaction,
                "INSERT INTO {table:history} (item_id, seq, actor, actor_name, kind, field_name, old_value, new_value, occurred_at) VALUES ($item, $seq, $actor, $actor_name, $kind, $field, $old, $new, $at);",
                ("item", itemId), ("seq", seq), ("actor", actor.Id), ("actor_name", actor.Name), ("kind", change.Kind), ("field", change.Field), ("old", change.OldValue), ("new", change.NewValue), ("at", at));
        }
    }

    // ---- projects and boards ----

    private const string ProjectColumns = "id, name, description, next_number, created_at, created_by";

    private static BacklogProjectRow ReadProject(DbDataReader r) => new(r.GetString(0), r.GetString(1), Text(r, 2), r.Int64(3), r.Time(4), r.GetString(5));

    /// <summary>Makes a backlog; false when there is one of that key (nothing changes).</summary>
    public bool CreateProject(string id, string name, string? description, string createdBy)
    {
        foreach (var (label, value) in new[] { ("id", id), ("name", name), ("createdBy", createdBy) }) ArgumentException.ThrowIfNullOrWhiteSpace(value, label);
        var now = Now;
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = Make(database, connection, null, database.Dialect.InsertOrIgnore("{table:project}", "id, name, description, next_number, created_at, created_by", "$id, $name, $description, 1, $at, $by", "id"),
                ("id", id), ("name", name), ("description", description), ("at", now), ("by", createdBy));
            return insert.ExecuteNonQuery() == 1;
        });
    }

    public BacklogProjectRow? Project(string id) => One($"SELECT {ProjectColumns} FROM {{table:project}} WHERE id = $id;", ReadProject, ("id", id));

    public IReadOnlyList<BacklogProjectRow> Projects() => List($"SELECT {ProjectColumns} FROM {{table:project}} ORDER BY id;", ReadProject);

    /// <summary>Sets a board of a project (made or replaced).</summary>
    public void SaveBoard(BacklogBoardRow board) =>
        Write<object?>((connection, transaction) =>
        {
            Execute(connection, transaction, "DELETE FROM {table:board} WHERE project_id = $project AND id = $id;", ("project", board.ProjectId), ("id", board.Id));
            Execute(connection, transaction, "INSERT INTO {table:board} (project_id, id, name, columns_doc) VALUES ($project, $id, $name, $columns);",
                ("project", board.ProjectId), ("id", board.Id), ("name", board.Name), ("columns", board.ColumnsDoc));
            return null;
        });

    public IReadOnlyList<BacklogBoardRow> Boards(string projectId) =>
        List("SELECT project_id, id, name, columns_doc FROM {table:board} WHERE project_id = $project ORDER BY id;", r => new BacklogBoardRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("project", projectId));

    // ---- items ----

    private const string ItemColumns = "id, project_id, item_number, item_type, title, description, state, priority, estimate, assignee, reporter, epic_id, sprint_id, sort_key, labels, resolution, created_at, updated_at, closed_at, row_version";

    private static BacklogItemRow ReadItem(DbDataReader r) => new(r.Uuid(0), r.GetString(1), r.Int64(2), r.GetString(3), r.GetString(4), Text(r, 5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.Decimal(8), Text(r, 9), r.GetString(10),
        r.NullableUuid(11), r.NullableUuid(12), r.Int64(13), r.GetString(14), Text(r, 15), r.Time(16), r.Time(17), r.NullableTime(18), r.Int64(19));

    /// <summary>
    /// Makes an item in <paramref name="item"/>'s project: its number is the project's next, taken in this transaction, and a <c>created</c> history row is written. <paramref name="item"/>'s <c>Number</c>, times and version are ignored (the row that comes back has them).
    /// Null when the project does not exist.
    /// </summary>
    public BacklogItemRow? InsertItem(BacklogItemRow item, BacklogActor actor)
    {
        var now = Now;
        return Write<BacklogItemRow?>((connection, transaction) =>
        {
            var number = Scalar(connection, transaction, "SELECT next_number FROM {table:project} WHERE id = $id;", ("id", item.ProjectId));
            if (number == 0) return null;
            Execute(connection, transaction, "UPDATE {table:project} SET next_number = next_number + 1 WHERE id = $id;", ("id", item.ProjectId));
            var made = item with { Number = number, CreatedAt = now, UpdatedAt = now, ClosedAt = item.ClosedAt, RowVersion = 1 };
            Execute(connection, transaction,
                $"INSERT INTO {{table:item}} ({ItemColumns}) VALUES ($id, $project, $number, $type, $title, $description, $state, $priority, $estimate, $assignee, $reporter, $epic, $sprint, $sort, $labels, $resolution, $created, $updated, $closed, 1);",
                ("id", made.Id), ("project", made.ProjectId), ("number", made.Number), ("type", made.ItemType), ("title", made.Title), ("description", made.Description), ("state", made.State), ("priority", made.Priority), ("estimate", made.Estimate),
                ("assignee", made.Assignee), ("reporter", made.Reporter), ("epic", made.EpicId), ("sprint", made.SprintId), ("sort", made.SortKey), ("labels", made.Labels), ("resolution", made.Resolution), ("created", now), ("updated", now), ("closed", made.ClosedAt));
            AppendHistory(connection, transaction, made.Id, actor, now, [new BacklogChange("created", NewValue: made.Key)]);
            return made;
        });
    }

    /// <summary>
    /// Replaces an item's fields (everything but its id, project, number, reporter and creation time) when it is still at <paramref name="expectedVersion"/>, and writes <paramref name="changes"/> to its history in the same transaction. False when someone changed it first
    /// (or it is gone): nothing is written, and the caller reads it again.
    /// </summary>
    public bool UpdateItem(BacklogItemRow item, long expectedVersion, BacklogActor actor, IReadOnlyList<BacklogChange> changes)
    {
        var now = Now;
        return Write((connection, transaction) =>
        {
            using (var update = Make(database, connection, transaction,
                "UPDATE {table:item} SET item_type = $type, title = $title, description = $description, state = $state, priority = $priority, estimate = $estimate, assignee = $assignee, epic_id = $epic, sprint_id = $sprint, sort_key = $sort, labels = $labels, " +
                "resolution = $resolution, updated_at = $updated, closed_at = $closed, row_version = row_version + 1 WHERE id = $id AND row_version = $version;",
                ("type", item.ItemType), ("title", item.Title), ("description", item.Description), ("state", item.State), ("priority", item.Priority), ("estimate", item.Estimate), ("assignee", item.Assignee), ("epic", item.EpicId), ("sprint", item.SprintId),
                ("sort", item.SortKey), ("labels", item.Labels), ("resolution", item.Resolution), ("updated", now), ("closed", item.ClosedAt), ("id", item.Id), ("version", expectedVersion)))
            {
                if (update.ExecuteNonQuery() != 1) return false;
            }

            AppendHistory(connection, transaction, item.Id, actor, now, changes);
            return true;
        });
    }

    public BacklogItemRow? Item(Guid id) => One($"SELECT {ItemColumns} FROM {{table:item}} WHERE id = $id;", ReadItem, ("id", id));

    /// <summary>An item by what people call it: the project's key and its number.</summary>
    public BacklogItemRow? Item(string projectId, long number) => One($"SELECT {ItemColumns} FROM {{table:item}} WHERE project_id = $project AND item_number = $number;", ReadItem, ("project", projectId), ("number", number));

    /// <summary>The items of a query, in backlog order (the project, then <c>sort_key</c>).</summary>
    public IReadOnlyList<BacklogItemRow> Items(BacklogItemQuery query)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        var where = new List<string>();
        var parameters = new List<(string, object?)>();
        if (query.Project is not null) { where.Add("project_id = $project"); parameters.Add(("project", query.Project)); }
        if (query.States is { Count: > 0 } states)
        {
            where.Add("state IN (" + string.Join(", ", states.Select((_, i) => "$state" + i.ToString(CultureInfo.InvariantCulture))) + ")");
            for (var i = 0; i < states.Count; i++) parameters.Add(("state" + i.ToString(CultureInfo.InvariantCulture), states[i]));
        }

        if (query.Sprint is { } sprint) { where.Add("sprint_id = $sprint"); parameters.Add(("sprint", sprint)); }
        else if (query.NoSprint) where.Add("sprint_id IS NULL");
        if (query.Epic is { } epic) { where.Add("epic_id = $epic"); parameters.Add(("epic", epic)); }
        if (query.Assignee is not null) { where.Add("assignee = $assignee"); parameters.Add(("assignee", query.Assignee)); }
        if (query.ItemType is not null) { where.Add("item_type = $type"); parameters.Add(("type", query.ItemType)); }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("LOWER(title) LIKE $text ESCAPE '\\'");
            parameters.Add(("text", "%" + query.Text.ToLowerInvariant().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }

        parameters.Add(("limit", (long)query.Limit));
        return List($"SELECT {ItemColumns} FROM {{table:item}} {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY project_id, sort_key, item_number {database.Limit("limit")};", ReadItem, [.. parameters]);
    }

    /// <summary>The largest <c>sort_key</c> in a project, 0 when it has no item: a new item goes after it.</summary>
    public long MaxSortKey(string projectId) => Read("SELECT COALESCE(MAX(sort_key), 0) FROM {table:item} WHERE project_id = $project;", r => r.Read() ? r.Int64(0) : 0, ("project", projectId));

    /// <summary>Gives the items the sort keys listed (a renumbering of the backlog, which changes no item's content and so writes no history), in one transaction.</summary>
    public void SetSortKeys(IReadOnlyList<(Guid Id, long SortKey)> keys) =>
        Write<object?>((connection, transaction) =>
        {
            var now = Now;
            foreach (var (id, key) in keys)
            {
                Execute(connection, transaction, "UPDATE {table:item} SET sort_key = $key, updated_at = $at, row_version = row_version + 1 WHERE id = $id AND sort_key <> $key;", ("key", key), ("at", now), ("id", id));
            }

            return null;
        });

    /// <summary>
    /// Removes an item with its comments, links, attachments (the rows; the payload ids are returned for the caller to remove) and history, in one transaction, and the links of other items that point at it. Null when there was no such item.
    /// </summary>
    public IReadOnlyList<Guid>? DeleteItem(Guid id) =>
        Write<IReadOnlyList<Guid>?>((connection, transaction) =>
        {
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM {table:item} WHERE id = $id;", ("id", id)) == 0) return null;
            var payloads = new List<Guid>();
            using (var command = Make(database, connection, transaction, "SELECT payload_id FROM {table:attachment} WHERE item_id = $id;", ("id", id)))
            {
                using var reader = command.ExecuteReader();
                while (reader.Read()) payloads.Add(reader.Uuid(0));
            }

            Execute(connection, transaction, "DELETE FROM {table:attachment} WHERE item_id = $id;", ("id", id));
            Execute(connection, transaction, "DELETE FROM {table:comment} WHERE item_id = $id;", ("id", id));
            Execute(connection, transaction, "DELETE FROM {table:link} WHERE item_id = $id OR (target_kind = 'item' AND target_ref = $ref);", ("id", id), ("ref", id.ToString("D", CultureInfo.InvariantCulture)));
            Execute(connection, transaction, "DELETE FROM {table:history} WHERE item_id = $id;", ("id", id));
            Execute(connection, transaction, "UPDATE {table:item} SET epic_id = NULL WHERE epic_id = $id;", ("id", id));
            Execute(connection, transaction, "DELETE FROM {table:item} WHERE id = $id;", ("id", id));
            return payloads;
        });

    // ---- sprints ----

    private const string SprintColumns = "id, project_id, name, goal, state, planned_start_at, planned_end_at, started_at, closed_at, row_version";

    private static BacklogSprintRow ReadSprint(DbDataReader r) => new(r.Uuid(0), r.GetString(1), r.GetString(2), Text(r, 3), r.GetString(4), r.NullableTime(5), r.NullableTime(6), r.NullableTime(7), r.NullableTime(8), r.Int64(9));

    public void InsertSprint(BacklogSprintRow sprint) =>
        Write<object?>((connection, transaction) =>
        {
            Execute(connection, transaction, $"INSERT INTO {{table:sprint}} ({SprintColumns}) VALUES ($id, $project, $name, $goal, $state, $planned_start, $planned_end, $started, $closed, 1);",
                ("id", sprint.Id), ("project", sprint.ProjectId), ("name", sprint.Name), ("goal", sprint.Goal), ("state", sprint.State), ("planned_start", sprint.PlannedStartAt), ("planned_end", sprint.PlannedEndAt), ("started", sprint.StartedAt), ("closed", sprint.ClosedAt));
            return null;
        });

    /// <summary>Replaces a sprint's fields when it is still at <paramref name="expectedVersion"/>; false when someone changed it first.</summary>
    public bool UpdateSprint(BacklogSprintRow sprint, long expectedVersion) =>
        Write((connection, transaction) =>
        {
            using var update = Make(database, connection, transaction,
                "UPDATE {table:sprint} SET name = $name, goal = $goal, state = $state, planned_start_at = $planned_start, planned_end_at = $planned_end, started_at = $started, closed_at = $closed, row_version = row_version + 1 WHERE id = $id AND row_version = $version;",
                ("name", sprint.Name), ("goal", sprint.Goal), ("state", sprint.State), ("planned_start", sprint.PlannedStartAt), ("planned_end", sprint.PlannedEndAt), ("started", sprint.StartedAt), ("closed", sprint.ClosedAt), ("id", sprint.Id), ("version", expectedVersion));
            return update.ExecuteNonQuery() == 1;
        });

    public BacklogSprintRow? Sprint(Guid id) => One($"SELECT {SprintColumns} FROM {{table:sprint}} WHERE id = $id;", ReadSprint, ("id", id));

    /// <summary>The sprints of a project, newest first (planned and active ones before closed ones are the screen's choice).</summary>
    public IReadOnlyList<BacklogSprintRow> Sprints(string projectId) =>
        List($"SELECT {SprintColumns} FROM {{table:sprint}} WHERE project_id = $project ORDER BY COALESCE(started_at, planned_start_at, closed_at) DESC, id;", ReadSprint, ("project", projectId));

    // ---- comments ----

    private const string CommentColumns = "id, item_id, author, author_name, body, created_at, edited_at";

    private static BacklogCommentRow ReadComment(DbDataReader r) => new(r.Uuid(0), r.Uuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Time(5), r.NullableTime(6));

    /// <summary>Adds a comment (by <paramref name="actor"/>) and its history row.</summary>
    public BacklogCommentRow AddComment(Guid itemId, string body, BacklogActor actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var now = Now;
        var comment = new BacklogCommentRow(Guid.CreateVersion7(now), itemId, actor.Id, actor.Name, body, now, null);
        Write<object?>((connection, transaction) =>
        {
            Execute(connection, transaction, $"INSERT INTO {{table:comment}} ({CommentColumns}) VALUES ($id, $item, $author, $name, $body, $at, NULL);",
                ("id", comment.Id), ("item", itemId), ("author", actor.Id), ("name", actor.Name), ("body", body), ("at", now));
            AppendHistory(connection, transaction, itemId, actor, now, [new BacklogChange("comment", NewValue: comment.Id.ToString("D", CultureInfo.InvariantCulture))]);
            return null;
        });
        return comment;
    }

    /// <summary>Changes a comment's text when <paramref name="author"/> wrote it; false when there is no such comment of theirs.</summary>
    public bool EditComment(Guid id, string body, string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var now = Now;
        return Write((connection, transaction) =>
        {
            using var update = Make(database, connection, transaction, "UPDATE {table:comment} SET body = $body, edited_at = $at WHERE id = $id AND author = $author;", ("body", body), ("at", now), ("id", id), ("author", author));
            return update.ExecuteNonQuery() == 1;
        });
    }

    public IReadOnlyList<BacklogCommentRow> Comments(Guid itemId) => List($"SELECT {CommentColumns} FROM {{table:comment}} WHERE item_id = $item ORDER BY created_at, id;", ReadComment, ("item", itemId));

    public BacklogCommentRow? Comment(Guid id) => One($"SELECT {CommentColumns} FROM {{table:comment}} WHERE id = $id;", ReadComment, ("id", id));

    // ---- links ----

    private const string LinkColumns = "id, item_id, link_kind, target_kind, target_ref, label, state, created_by, created_at";

    private static BacklogLinkRow ReadLink(DbDataReader r) => new(r.Uuid(0), r.Uuid(1), r.GetString(2), r.GetString(3), r.GetString(4), Text(r, 5), r.GetString(6), r.GetString(7), r.Time(8));

    /// <summary>Adds a link and its history row; null when the item already has that link (nothing changes).</summary>
    public BacklogLinkRow? AddLink(Guid itemId, string linkKind, string targetKind, string targetRef, string? label, BacklogActor actor)
    {
        var now = Now;
        var link = new BacklogLinkRow(Guid.CreateVersion7(now), itemId, linkKind, targetKind, targetRef, label, "ok", actor.Id, now);
        return Write<BacklogLinkRow?>((connection, transaction) =>
        {
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM {table:link} WHERE item_id = $item AND link_kind = $kind AND target_kind = $target_kind AND target_ref = $target_ref;",
                ("item", itemId), ("kind", linkKind), ("target_kind", targetKind), ("target_ref", targetRef)) > 0) return null;
            Execute(connection, transaction, $"INSERT INTO {{table:link}} ({LinkColumns}) VALUES ($id, $item, $kind, $target_kind, $target_ref, $label, 'ok', $by, $at);",
                ("id", link.Id), ("item", itemId), ("kind", linkKind), ("target_kind", targetKind), ("target_ref", targetRef), ("label", label), ("by", actor.Id), ("at", now));
            AppendHistory(connection, transaction, itemId, actor, now, [new BacklogChange("link", linkKind, null, targetKind + ":" + targetRef)]);
            return link;
        });
    }

    /// <summary>Removes a link (a person's act, in the history); false when there is none.</summary>
    public bool RemoveLink(Guid id, BacklogActor actor)
    {
        var now = Now;
        return Write((connection, transaction) =>
        {
            BacklogLinkRow? link;
            using (var read = Make(database, connection, transaction, $"SELECT {LinkColumns} FROM {{table:link}} WHERE id = $id;", ("id", id)))
            {
                using var reader = read.ExecuteReader();
                link = reader.Read() ? ReadLink(reader) : null;
            }

            if (link is null) return false;
            Execute(connection, transaction, "DELETE FROM {table:link} WHERE id = $id;", ("id", id));
            AppendHistory(connection, transaction, link.ItemId, actor, now, [new BacklogChange("link", link.LinkKind, link.TargetKind + ":" + link.TargetRef, null)]);
            return true;
        });
    }

    public IReadOnlyList<BacklogLinkRow> Links(Guid itemId) => List($"SELECT {LinkColumns} FROM {{table:link}} WHERE item_id = $item ORDER BY created_at, id;", ReadLink, ("item", itemId));

    /// <summary>The links that point at a thing (a Build model, a replication, a pull request, ...): what the thing's own page shows as its open items.</summary>
    public IReadOnlyList<BacklogLinkRow> LinksTo(string targetKind, string targetRef) =>
        List($"SELECT {LinkColumns} FROM {{table:link}} WHERE target_kind = $kind AND target_ref = $ref ORDER BY created_at, id;", ReadLink, ("kind", targetKind), ("ref", targetRef));

    /// <summary>Flags (<c>dropped</c>) or unflags (<c>ok</c>) every link to a thing, without deleting any; returns how many changed.</summary>
    public int MarkTarget(string targetKind, string targetRef, string state)
    {
        if (!BacklogModule.LinkStates.Contains(state)) throw new ArgumentException($"'{state}' is not a state of a link ({string.Join(", ", BacklogModule.LinkStates)}).", nameof(state));
        return Write((connection, transaction) =>
        {
            using var update = Make(database, connection, transaction, "UPDATE {table:link} SET state = $state WHERE target_kind = $kind AND target_ref = $ref AND state <> $state;", ("state", state), ("kind", targetKind), ("ref", targetRef));
            return update.ExecuteNonQuery();
        });
    }

    // ---- attachments ----

    private const string AttachmentColumns = "id, item_id, file_name, media_type, size, payload_id, added_by, added_at";

    private static BacklogAttachmentRow ReadAttachment(DbDataReader r) => new(r.Uuid(0), r.Uuid(1), r.GetString(2), r.GetString(3), r.Int64(4), r.Uuid(5), r.GetString(6), r.Time(7));

    /// <summary>Records an attachment whose bytes are already in the payload store (written with <paramref name="id"/> as the payload's owner row), and its history row.</summary>
    public BacklogAttachmentRow AddAttachment(Guid id, Guid itemId, string fileName, string mediaType, long size, Guid payloadId, BacklogActor actor)
    {
        var now = Now;
        var row = new BacklogAttachmentRow(id, itemId, fileName, mediaType, size, payloadId, actor.Id, now);
        Write<object?>((connection, transaction) =>
        {
            Execute(connection, transaction, $"INSERT INTO {{table:attachment}} ({AttachmentColumns}) VALUES ($id, $item, $name, $type, $size, $payload, $by, $at);",
                ("id", row.Id), ("item", itemId), ("name", fileName), ("type", mediaType), ("size", size), ("payload", payloadId), ("by", actor.Id), ("at", now));
            AppendHistory(connection, transaction, itemId, actor, now, [new BacklogChange("attachment", NewValue: fileName)]);
            return null;
        });
        return row;
    }

    public IReadOnlyList<BacklogAttachmentRow> Attachments(Guid itemId) => List($"SELECT {AttachmentColumns} FROM {{table:attachment}} WHERE item_id = $item ORDER BY added_at, id;", ReadAttachment, ("item", itemId));

    public BacklogAttachmentRow? Attachment(Guid id) => One($"SELECT {AttachmentColumns} FROM {{table:attachment}} WHERE id = $id;", ReadAttachment, ("id", id));

    /// <summary>Removes an attachment's row (and writes its history); the payload is the caller's to remove. The row that was removed, or null.</summary>
    public BacklogAttachmentRow? RemoveAttachment(Guid id, BacklogActor actor)
    {
        var now = Now;
        return Write<BacklogAttachmentRow?>((connection, transaction) =>
        {
            BacklogAttachmentRow? row;
            using (var read = Make(database, connection, transaction, $"SELECT {AttachmentColumns} FROM {{table:attachment}} WHERE id = $id;", ("id", id)))
            {
                using var reader = read.ExecuteReader();
                row = reader.Read() ? ReadAttachment(reader) : null;
            }

            if (row is null) return null;
            Execute(connection, transaction, "DELETE FROM {table:attachment} WHERE id = $id;", ("id", id));
            AppendHistory(connection, transaction, row.ItemId, actor, now, [new BacklogChange("attachment", OldValue: row.FileName)]);
            return row;
        });
    }

    // ---- history ----

    /// <summary>The changes of an item in the order they happened.</summary>
    public IReadOnlyList<BacklogHistoryRow> History(Guid itemId) =>
        List("SELECT item_id, seq, actor, actor_name, kind, field_name, old_value, new_value, occurred_at FROM {table:history} WHERE item_id = $item ORDER BY seq;",
            r => new BacklogHistoryRow(r.Uuid(0), r.Int64(1), r.GetString(2), r.GetString(3), r.GetString(4), Text(r, 5), Text(r, 6), Text(r, 7), r.Time(8)), ("item", itemId));

    /// <summary>Writes history rows for an item outside a field change (a sprint moved it, a rank).</summary>
    public void RecordChanges(Guid itemId, BacklogActor actor, IReadOnlyList<BacklogChange> changes) =>
        Write<object?>((connection, transaction) =>
        {
            AppendHistory(connection, transaction, itemId, actor, Now, changes);
            return null;
        });
}
