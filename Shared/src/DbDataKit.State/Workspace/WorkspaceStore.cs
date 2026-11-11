using System.Data.Common;

namespace DbDataKit.State;

/// <summary>A project the Workspace server hosts.</summary>
/// <param name="Path">The repository's folder, or null for the folder the server made for it.</param>
public sealed record WorkspaceProjectRow(string Id, string Kind, string? Title, string? Path, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>A draft that exists now.</summary>
public sealed record WorkspaceRow(string Project, string Id, string Owner, string OwnerName, string Name, string Branch, string BaseCommit, DateTimeOffset CreatedAt);

/// <summary>One thing that happened to a draft.</summary>
public sealed record WorkspaceEventRow(string Project, string WorkspaceId, string Kind, string Actor, string ActorName, string? Detail, DateTimeOffset OccurredAt);

/// <summary>A build a draft asked for.</summary>
public sealed record WorkspaceBuildRow(Guid Id, string Project, string WorkspaceId, string Status, string? CommitSha, string RequestedBy, string? Message, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>A target and the reference to the secret that holds the publisher's key for it.</summary>
public sealed record PublisherKeyRow(string Name, string SecretRef, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt);

/// <summary>
/// Writes and reads the <c>workspace</c> module (<see cref="WorkspaceModule"/>). A change to a draft and the event that says so are written in <b>one transaction</b>, so the history never lacks a draft's creation or end.
/// </summary>
public sealed class WorkspaceStore(StateDatabase database)
{
    private const string Module = WorkspaceModule.Name;

    /// <summary>The kinds of event a draft has.</summary>
    public static IReadOnlyList<string> EventKinds { get; } = ["created", "committed", "pushed", "merged", "abandoned"];

    // ---- projects ----

    /// <summary>Adds a project; false when there is one of that id already (nothing changes).</summary>
    public bool AddProject(string id, string kind, string? title, string? path, string createdBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        if (!WorkspaceModule.ProjectKinds.Contains(kind)) throw new ArgumentException($"'{kind}' is not a kind of project this server knows ({string.Join(", ", WorkspaceModule.ProjectKinds)}).", nameof(kind));
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection, database.Dialect.InsertOrIgnore("{table:project}", "id, kind, title, path, created_at, created_by", "$id, $kind, $title, $path, $at, $by", "id"));
            insert.Bind(database, "id", id).Bind(database, "kind", kind).Bind(database, "title", title, System.Data.DbType.String).Bind(database, "path", path, System.Data.DbType.String).Bind(database, "at", now).Bind(database, "by", createdBy);
            return insert.ExecuteNonQuery() == 1;
        });
    }

    public IReadOnlyList<WorkspaceProjectRow> Projects() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT id, kind, title, path, created_at, created_by FROM {table:project} ORDER BY id;");
            using var reader = command.ExecuteReader();
            var rows = new List<WorkspaceProjectRow>();
            while (reader.Read()) rows.Add(new WorkspaceProjectRow(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), reader.Time(4), reader.GetString(5)));
            return rows;
        });

    public WorkspaceProjectRow? FindProject(string id) => Projects().FirstOrDefault(p => p.Id == id);

    // ---- drafts and what happened to them ----

    /// <summary>Records a draft that was made, and the event, in one transaction. False when the draft is recorded already (nothing changes).</summary>
    public bool RecordCreated(WorkspaceRow workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var insert = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore("{table:workspace}", "project, id, owner, owner_name, name, branch, base_commit, created_at", "$project, $id, $owner, $owner_name, $name, $branch, $base, $at", "project, id")))
            {
                insert.Bind(database, "project", workspace.Project).Bind(database, "id", workspace.Id).Bind(database, "owner", workspace.Owner).Bind(database, "owner_name", workspace.OwnerName)
                    .Bind(database, "name", workspace.Name).Bind(database, "branch", workspace.Branch).Bind(database, "base", workspace.BaseCommit).Bind(database, "at", workspace.CreatedAt);
                if (insert.ExecuteNonQuery() != 1) return false;
            }

            InsertEvent(connection, transaction, new WorkspaceEventRow(workspace.Project, workspace.Id, "created", workspace.Owner, workspace.OwnerName, workspace.Branch, workspace.CreatedAt));
            transaction.Commit();
            return true;
        });
    }

    /// <summary>Records an event of a draft that is still there (<c>committed</c> or <c>pushed</c>).</summary>
    public void RecordEvent(string project, string workspaceId, string kind, string actor, string actorName, string? detail)
    {
        if (kind is not ("committed" or "pushed")) throw new ArgumentException($"'{kind}' does not leave a draft in place; use RecordEnded for merged and abandoned.", nameof(kind));
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            InsertEvent(connection, null, new WorkspaceEventRow(project, workspaceId, kind, actor, actorName, detail, now));
        });
    }

    /// <summary>Records that a draft ended (<c>merged</c> or <c>abandoned</c>): the row goes and the event is written, in one transaction. False when the draft was not recorded.</summary>
    public bool RecordEnded(string project, string workspaceId, string kind, string actor, string actorName, string? detail = null)
    {
        if (kind is not ("merged" or "abandoned")) throw new ArgumentException($"A draft ends merged or abandoned, not '{kind}'.", nameof(kind));
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:workspace} WHERE project = $project AND id = $id;"))
            {
                delete.Bind(database, "project", project).Bind(database, "id", workspaceId);
                if (delete.ExecuteNonQuery() != 1) return false;
            }

            InsertEvent(connection, transaction, new WorkspaceEventRow(project, workspaceId, kind, actor, actorName, detail, now));
            transaction.Commit();
            return true;
        });
    }

    private void InsertEvent(DbConnection connection, DbTransaction? transaction, WorkspaceEventRow e)
    {
        using var insert = database.Command(Module, connection, transaction,
            "INSERT INTO {table:workspace_event} (id, project, workspace_id, kind, actor, actor_name, detail, occurred_at) VALUES ($id, $project, $workspace, $kind, $actor, $actor_name, $detail, $at);");
        insert.Bind(database, "id", database.NewId()).Bind(database, "project", e.Project).Bind(database, "workspace", e.WorkspaceId).Bind(database, "kind", e.Kind).Bind(database, "actor", e.Actor)
            .Bind(database, "actor_name", e.ActorName).Bind(database, "detail", e.Detail, System.Data.DbType.String).Bind(database, "at", e.OccurredAt);
        insert.ExecuteNonQuery();
    }

    /// <summary>The drafts that exist now, optionally of one project, by project and id.</summary>
    public IReadOnlyList<WorkspaceRow> Workspaces(string? project = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT project, id, owner, owner_name, name, branch, base_commit, created_at FROM {{table:workspace}} {(project is null ? "" : "WHERE project = $project")} ORDER BY project, id;");
            if (project is not null) command.Bind(database, "project", project);
            using var reader = command.ExecuteReader();
            var rows = new List<WorkspaceRow>();
            while (reader.Read())
                rows.Add(new WorkspaceRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.Time(7)));
            return rows;
        });

    /// <summary>What happened to a draft (or, with no id, to every draft of the project), newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<WorkspaceEventRow> Events(string project, string? workspaceId = null, int limit = 50) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT project, workspace_id, kind, actor, actor_name, detail, occurred_at FROM {{table:workspace_event}} WHERE project = $project {(workspaceId is null ? "" : "AND workspace_id = $id")} ORDER BY occurred_at DESC, id DESC {database.Limit("limit")};");
            command.Bind(database, "project", project).Bind(database, "limit", (long)limit);
            if (workspaceId is not null) command.Bind(database, "id", workspaceId);
            using var reader = command.ExecuteReader();
            var rows = new List<WorkspaceEventRow>();
            while (reader.Read()) rows.Add(new WorkspaceEventRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Text(reader, 5), reader.Time(6)));
            return rows;
        });

    // ---- builds ----

    /// <summary>Records that a draft asked for a build; it starts <c>queued</c>.</summary>
    public WorkspaceBuildRow RequestBuild(string project, string workspaceId, string requestedBy, string? commitSha = null)
    {
        var id = database.NewId();
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:build} (id, project, workspace_id, status, commit_sha, requested_by, requested_at) VALUES ($id, $project, $workspace, $status, $sha, $by, $at);");
            insert.Bind(database, "id", id).Bind(database, "project", project).Bind(database, "workspace", workspaceId).Bind(database, "status", "queued")
                .Bind(database, "sha", commitSha, System.Data.DbType.String).Bind(database, "by", requestedBy).Bind(database, "at", now);
            insert.ExecuteNonQuery();
        });
        return new WorkspaceBuildRow(id, project, workspaceId, "queued", commitSha, requestedBy, null, now, null, null);
    }

    /// <summary>Moves a build to <paramref name="status"/> (<c>running</c> sets its start; <c>succeeded</c>, <c>failed</c> and <c>canceled</c> its end). False when there is no such build.</summary>
    public bool SetBuildStatus(Guid id, string status, string? message = null)
    {
        if (!WorkspaceModule.BuildStatuses.Contains(status) || status == "queued") throw new ArgumentException($"A build moves to running, succeeded, failed or canceled, not '{status}'.", nameof(status));
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, status == "running"
                ? "UPDATE {table:build} SET status = $status, started_at = $at, message = $message WHERE id = $id;"
                : "UPDATE {table:build} SET status = $status, finished_at = $at, message = $message WHERE id = $id;");
            update.Bind(database, "status", status).Bind(database, "at", now).Bind(database, "message", message, System.Data.DbType.String).Bind(database, "id", id);
            return update.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>The builds of a draft (or, with no id, of the project), newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<WorkspaceBuildRow> Builds(string project, string? workspaceId = null, int limit = 50) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT id, project, workspace_id, status, commit_sha, requested_by, message, requested_at, started_at, finished_at FROM {{table:build}} WHERE project = $project {(workspaceId is null ? "" : "AND workspace_id = $id")} ORDER BY requested_at DESC, id DESC {database.Limit("limit")};");
            command.Bind(database, "project", project).Bind(database, "limit", (long)limit);
            if (workspaceId is not null) command.Bind(database, "id", workspaceId);
            using var reader = command.ExecuteReader();
            var rows = new List<WorkspaceBuildRow>();
            while (reader.Read())
                rows.Add(new WorkspaceBuildRow(reader.Uuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4), reader.GetString(5), Text(reader, 6), reader.Time(7), reader.NullableTime(8), reader.NullableTime(9)));
            return rows;
        });

    // ---- the publisher's key reference ----

    /// <summary>
    /// Sets the reference to the secret that holds the publisher's key for a target. Only a <b>reference</b> is stored: text that is not one (it does not start with <c>secret:</c>) is refused, so a key pasted into the wrong field never reaches
    /// a row.
    /// </summary>
    public PublisherKeyRow SetPublisherKey(string name, string secretRef, string by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        if (secretRef is null || !secretRef.StartsWith("secret:", StringComparison.Ordinal) || secretRef.Length <= "secret:".Length || secretRef.Any(char.IsWhiteSpace) || secretRef.Length > 200)
            throw new ArgumentException("The publisher's key is kept in the server's secret store; give its reference (secret:<name>), not the key.", nameof(secretRef));
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var update = database.Command(Module, connection, transaction, "UPDATE {table:publisher_key} SET secret_ref = $ref, updated_at = $at WHERE name = $name;"))
            {
                update.Bind(database, "ref", secretRef).Bind(database, "at", now).Bind(database, "name", name);
                if (update.ExecuteNonQuery() == 0)
                {
                    using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:publisher_key} (name, secret_ref, created_at, created_by, updated_at) VALUES ($name, $ref, $at, $by, $at);");
                    insert.Bind(database, "name", name).Bind(database, "ref", secretRef).Bind(database, "at", now).Bind(database, "by", by);
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        });
        return PublisherKeys().Single(k => k.Name == name);
    }

    public IReadOnlyList<PublisherKeyRow> PublisherKeys() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT name, secret_ref, created_at, created_by, updated_at FROM {table:publisher_key} ORDER BY name;");
            using var reader = command.ExecuteReader();
            var rows = new List<PublisherKeyRow>();
            while (reader.Read()) rows.Add(new PublisherKeyRow(reader.GetString(0), reader.GetString(1), reader.Time(2), reader.GetString(3), reader.Time(4)));
            return rows;
        });

    /// <summary>Removes the reference (not the secret). False when there was none.</summary>
    public bool RemovePublisherKey(string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var delete = database.Command(Module, connection, "DELETE FROM {table:publisher_key} WHERE name = $name;");
            delete.Bind(database, "name", name);
            return delete.ExecuteNonQuery() > 0;
        });

    private static string? Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
