using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>A conversation as the screens list it.</summary>
public sealed record ConversationRow(string Id, string Agent, string AgentVersion, string Person, string PersonName, string Origin, string? Channel, string? Workspace, string? Title, string Status, string? StopReason, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt);

/// <summary>One thing said in a conversation; <see cref="Content"/> is what was kept (history: it is gone after the retention period, the audit rows are not).</summary>
public sealed record TurnTextRow(string ConversationId, int Seq, string Role, string Content, DateTimeOffset CreatedAt);

/// <summary>A model turn, as the audit keeps it: no text, the record of what the turn was given.</summary>
public sealed record TurnRecordRow(Guid Id, string ConversationId, int Turn, string Person, string Agent, string AgentVersion, string? Model, long InputTokens, long OutputTokens, decimal Cost, string? ContextPrint, string? ContextRecord, DateTimeOffset RecordedAt);

/// <summary>Something that happened in a run (audit).</summary>
public sealed record AgentAuditRow(Guid Id, string? ConversationId, int? Turn, string Kind, string Person, string Agent, string AgentVersion, string Origin, string? Tool, string? Arguments, string? Detail, DateTimeOffset OccurredAt);

/// <summary>A step that waits for a person, or was answered (audit).</summary>
public sealed record ApprovalRow(Guid Id, string? ConversationId, string Person, string Agent, string AgentVersion, string Origin, string Tool, string Preview, string? Arguments, string Traits, string Status,
    DateTimeOffset RequestedAt, DateTimeOffset? AnsweredAt, string? AnsweredBy, string? Note);

/// <summary>What a person, an agent or the install used: the sum of the recorded turns.</summary>
public sealed record AgentUsageRow(string Key, int Turns, long InputTokens, long OutputTokens, decimal Cost);

/// <summary>
/// Writes and reads the <c>agents</c> module (<see cref="AgentsModule"/>). The store knows rows, not the runtime: the host maps the runtime's events onto <see cref="RecordAudit"/> and the model's usage onto <see cref="RecordTurn"/>. An approval is
/// answered by <see cref="Answer"/> only while it is pending, and only once: a second answer changes nothing and says so.
/// </summary>
public sealed class AgentStore(StateDatabase database)
{
    private const string Module = AgentsModule.Name;

    /// <summary>Where a conversation came from.</summary>
    public static IReadOnlyList<string> Origins { get; } = ["console", "mcp", "serving"];

    // ---- conversations ----

    /// <summary>Opens a conversation; false when there is one of that id (nothing changes).</summary>
    public bool Start(string id, string agent, string agentVersion, string person, string personName, string origin = "console", string? channel = null, string? workspace = null, string? title = null)
    {
        foreach (var (name, value) in new[] { ("id", id), ("agent", agent), ("agentVersion", agentVersion), ("person", person), ("personName", personName) }) ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (!Origins.Contains(origin)) throw new ArgumentException($"'{origin}' is not an origin of a conversation ({string.Join(", ", Origins)}).", nameof(origin));
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection, database.Dialect.InsertOrIgnore("{table:conversation}",
                "id, agent, agent_version, person, person_name, origin, channel, workspace, title, status, stop_reason, started_at, updated_at",
                "$id, $agent, $version, $person, $person_name, $origin, $channel, $workspace, $title, $status, NULL, $at, $at", "id"));
            insert.Bind(database, "id", id).Bind(database, "agent", agent).Bind(database, "version", agentVersion).Bind(database, "person", person).Bind(database, "person_name", personName).Bind(database, "origin", origin)
                .Bind(database, "channel", channel, System.Data.DbType.String).Bind(database, "workspace", workspace, System.Data.DbType.String).Bind(database, "title", title, System.Data.DbType.String)
                .Bind(database, "status", "open").Bind(database, "at", now);
            return insert.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Sets how a conversation stands (<c>open</c> again for the next message, or how a run ended) and why.</summary>
    public void SetStatus(string id, string status, string? stopReason = null)
    {
        if (!AgentsModule.ConversationStatuses.Contains(status)) throw new ArgumentException($"'{status}' is not a status of a conversation ({string.Join(", ", AgentsModule.ConversationStatuses)}).", nameof(status));
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, "UPDATE {table:conversation} SET status = $status, stop_reason = $reason, updated_at = $at WHERE id = $id;");
            update.Bind(database, "status", status).Bind(database, "reason", stopReason, System.Data.DbType.String).Bind(database, "at", now).Bind(database, "id", id);
            update.ExecuteNonQuery();
        });
    }

    private const string ConversationColumns = "id, agent, agent_version, person, person_name, origin, channel, workspace, title, status, stop_reason, started_at, updated_at";

    private static ConversationRow ReadConversation(DbDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Text(r, 6), Text(r, 7), Text(r, 8), r.GetString(9), Text(r, 10), r.Time(11), r.Time(12));

    private static string? Text(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public ConversationRow? Find(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {ConversationColumns} FROM {{table:conversation}} WHERE id = $id;");
            command.Bind(database, "id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadConversation(reader) : null;
        });

    /// <summary>The conversations, newest first, optionally of one person or one agent.</summary>
    public IReadOnlyList<ConversationRow> Conversations(string? person = null, string? agent = null, int limit = 100) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            using var connection = database.OpenConnection();
            var where = string.Join(" AND ", new[] { person is null ? null : "person = $person", agent is null ? null : "agent = $agent" }.Where(w => w is not null));
            using var command = database.Command(Module, connection,
                $"SELECT {ConversationColumns} FROM {{table:conversation}} {(where.Length > 0 ? "WHERE " + where : "")} ORDER BY started_at DESC, id {database.Limit("limit")};");
            if (person is not null) command.Bind(database, "person", person);
            if (agent is not null) command.Bind(database, "agent", agent);
            command.Bind(database, "limit", (long)limit);
            using var reader = command.ExecuteReader();
            var rows = new List<ConversationRow>();
            while (reader.Read()) rows.Add(ReadConversation(reader));
            return rows;
        });

    // ---- what was said (history) ----

    /// <summary>Keeps one thing that was said; the sequence number is the next one of the conversation.</summary>
    public int AppendText(string conversationId, string role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            int seq;
            using (var next = database.Command(Module, connection, transaction, "SELECT COALESCE(MAX(seq), 0) FROM {table:turn_text} WHERE conversation_id = $id;"))
            {
                next.Bind(database, "id", conversationId);
                seq = Convert.ToInt32(next.ExecuteScalar(), CultureInfo.InvariantCulture) + 1;
            }
            using (var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:turn_text} (conversation_id, seq, role, content, created_at) VALUES ($id, $seq, $role, $content, $at);"))
            {
                insert.Bind(database, "id", conversationId).Bind(database, "seq", (long)seq).Bind(database, "role", role).Bind(database, "content", content).Bind(database, "at", now);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return seq;
        });
    }

    /// <summary>What is still kept of what was said, in order. After the retention period this is empty while <see cref="Turns"/> and <see cref="Audit"/> are not.</summary>
    public IReadOnlyList<TurnTextRow> Text(string conversationId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT conversation_id, seq, role, content, created_at FROM {table:turn_text} WHERE conversation_id = $id ORDER BY seq;");
            command.Bind(database, "id", conversationId);
            using var reader = command.ExecuteReader();
            var rows = new List<TurnTextRow>();
            while (reader.Read()) rows.Add(new TurnTextRow(reader.GetString(0), reader.Int32(1), reader.GetString(2), reader.GetString(3), reader.Time(4)));
            return rows;
        });

    // ---- the record of a model turn ----

    public void RecordTurn(string conversationId, int turn, string person, string agent, string agentVersion, string? model, long inputTokens, long outputTokens, decimal cost, string? contextPrint, string? contextRecord)
    {
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:turn_record} (id, conversation_id, turn, person, agent, agent_version, model, input_tokens, output_tokens, cost, context_print, context_record, recorded_at) VALUES ($id, $c, $turn, $person, $agent, $version, $model, $in, $out, $cost, $print, $record, $at);");
            insert.Bind(database, "id", database.NewId()).Bind(database, "c", conversationId).Bind(database, "turn", (long)turn).Bind(database, "person", person).Bind(database, "agent", agent).Bind(database, "version", agentVersion)
                .Bind(database, "model", model, System.Data.DbType.String).Bind(database, "in", inputTokens).Bind(database, "out", outputTokens).Bind(database, "cost", cost, System.Data.DbType.Decimal)
                .Bind(database, "print", contextPrint, System.Data.DbType.String).Bind(database, "record", contextRecord, System.Data.DbType.String).Bind(database, "at", now);
            insert.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<TurnRecordRow> Turns(string conversationId) => ReadTurns("WHERE conversation_id = $x", conversationId);

    private IReadOnlyList<TurnRecordRow> ReadTurns(string where, string? value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT id, conversation_id, turn, person, agent, agent_version, model, input_tokens, output_tokens, cost, context_print, context_record, recorded_at FROM {{table:turn_record}} {where} ORDER BY recorded_at, turn;");
            if (value is not null) command.Bind(database, "x", value);
            using var reader = command.ExecuteReader();
            var rows = new List<TurnRecordRow>();
            while (reader.Read())
                rows.Add(new TurnRecordRow(reader.Uuid(0), reader.GetString(1), reader.Int32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), Text(reader, 6), reader.Int64(7), reader.Int64(8), reader.Decimal(9), Text(reader, 10), Text(reader, 11), reader.Time(12)));
            return rows;
        });

    /// <summary>What was used, summed from the recorded turns and grouped by <c>person</c>, <c>agent</c> or <c>install</c> (one row).</summary>
    public IReadOnlyList<AgentUsageRow> Usage(string groupBy)
    {
        var turns = ReadTurns("", null);
        Func<TurnRecordRow, string> key = groupBy switch
        {
            "person" => t => t.Person,
            "agent" => t => t.Agent,
            "install" => _ => "install",
            _ => throw new ArgumentException($"Usage is grouped by person, agent or install, not '{groupBy}'.", nameof(groupBy)),
        };
        return [.. turns.GroupBy(key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new AgentUsageRow(g.Key, g.Count(), g.Sum(t => t.InputTokens), g.Sum(t => t.OutputTokens), g.Sum(t => t.Cost)))];
    }

    // ---- the audit ----

    public void RecordAudit(string? conversationId, int? turn, string kind, string person, string agent, string agentVersion, string origin, string? tool, string? arguments, string? detail, DateTimeOffset? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var when = at ?? database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:audit_event} (id, conversation_id, turn, kind, person, agent, agent_version, origin, tool, arguments, detail, occurred_at) VALUES ($id, $c, $turn, $kind, $person, $agent, $version, $origin, $tool, $arguments, $detail, $at);");
            insert.Bind(database, "id", database.NewId()).Bind(database, "c", conversationId, System.Data.DbType.String).Bind(database, "turn", turn is { } t ? (long)t : null, System.Data.DbType.Int64).Bind(database, "kind", kind)
                .Bind(database, "person", person).Bind(database, "agent", agent).Bind(database, "version", agentVersion).Bind(database, "origin", origin).Bind(database, "tool", tool, System.Data.DbType.String)
                .Bind(database, "arguments", arguments, System.Data.DbType.String).Bind(database, "detail", detail, System.Data.DbType.String).Bind(database, "at", when);
            insert.ExecuteNonQuery();
        });
    }

    /// <summary>The audit rows, newest first, filtered by any of person, agent, conversation and kind.</summary>
    public IReadOnlyList<AgentAuditRow> Audit(string? person = null, string? agent = null, string? conversationId = null, string? kind = null, string? origin = null, int limit = 500) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            var filters = new List<(string Sql, string Name, string Value)>();
            if (person is not null) filters.Add(("person = $person", "person", person));
            if (agent is not null) filters.Add(("agent = $agent", "agent", agent));
            if (conversationId is not null) filters.Add(("conversation_id = $conversation", "conversation", conversationId));
            if (kind is not null) filters.Add(("kind = $kind", "kind", kind));
            if (origin is not null) filters.Add(("origin = $origin", "origin", origin));
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT id, conversation_id, turn, kind, person, agent, agent_version, origin, tool, arguments, detail, occurred_at FROM {{table:audit_event}} {(filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters.Select(f => f.Sql)) : "")} ORDER BY occurred_at DESC, id {database.Limit("limit")};");
            foreach (var f in filters) command.Bind(database, f.Name, f.Value);
            command.Bind(database, "limit", (long)limit);
            using var reader = command.ExecuteReader();
            var rows = new List<AgentAuditRow>();
            while (reader.Read())
                rows.Add(new AgentAuditRow(reader.Uuid(0), Text(reader, 1), reader.NullableInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Text(reader, 8), Text(reader, 9), Text(reader, 10), reader.Time(11)));
            return rows;
        });

    // ---- approvals ----

    /// <summary>A step that waits for a person: written as pending, and the audit says it was asked.</summary>
    public Guid RequestApproval(string? conversationId, string person, string agent, string agentVersion, string origin, string tool, string preview, string? arguments, string traits)
    {
        var id = database.NewId();
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:approval} (id, conversation_id, person, agent, agent_version, origin, tool, preview, arguments, traits, status, requested_at) VALUES ($id, $c, $person, $agent, $version, $origin, $tool, $preview, $arguments, $traits, 'pending', $at);");
            insert.Bind(database, "id", id).Bind(database, "c", conversationId, System.Data.DbType.String).Bind(database, "person", person).Bind(database, "agent", agent).Bind(database, "version", agentVersion).Bind(database, "origin", origin)
                .Bind(database, "tool", tool).Bind(database, "preview", preview).Bind(database, "arguments", arguments, System.Data.DbType.String).Bind(database, "traits", traits).Bind(database, "at", now);
            insert.ExecuteNonQuery();
        });
        return id;
    }

    /// <summary>
    /// Answers a pending approval as <paramref name="answeredBy"/>. False when it is not pending any more (answered, expired or unknown): the first answer stands. Only the person the step is for may answer it, unless <paramref name="anyPerson"/>
    /// (an administrator's act, which the audit then shows as someone else's answer).
    /// </summary>
    public bool Answer(Guid id, bool approved, string answeredBy, string? note = null, bool anyPerson = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answeredBy);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection,
                $"UPDATE {{table:approval}} SET status = $status, answered_at = $at, answered_by = $by, note = $note WHERE id = $id AND status = 'pending'{(anyPerson ? "" : " AND person = $by")};");
            update.Bind(database, "status", approved ? "approved" : "denied").Bind(database, "at", now).Bind(database, "by", answeredBy).Bind(database, "note", note, System.Data.DbType.String).Bind(database, "id", id);
            return update.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Marks one pending approval expired (nobody answered in time); false when it was not pending.</summary>
    public bool Expire(Guid id)
    {
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, "UPDATE {table:approval} SET status = 'expired', answered_at = $now WHERE id = $id AND status = 'pending';");
            update.Bind(database, "now", now).Bind(database, "id", id);
            return update.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Makes a pending approval older than <paramref name="olderThan"/> expired (which is a no); returns how many.</summary>
    public int ExpireApprovals(TimeSpan olderThan)
    {
        var now = database.Time.GetUtcNow();
        var cutoff = now - olderThan;
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, "UPDATE {table:approval} SET status = 'expired', answered_at = $now WHERE status = 'pending' AND requested_at < $cutoff;");
            update.Bind(database, "now", now).Bind(database, "cutoff", cutoff);
            return update.ExecuteNonQuery();
        });
    }

    private const string ApprovalColumns = "id, conversation_id, person, agent, agent_version, origin, tool, preview, arguments, traits, status, requested_at, answered_at, answered_by, note";

    private static ApprovalRow ReadApproval(DbDataReader r) =>
        new(r.Uuid(0), Text(r, 1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), Text(r, 8), r.GetString(9), r.GetString(10), r.Time(11), r.NullableTime(12), Text(r, 13), Text(r, 14));

    public ApprovalRow? Approval(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {ApprovalColumns} FROM {{table:approval}} WHERE id = $id;");
            command.Bind(database, "id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadApproval(reader) : null;
        });

    /// <summary>The approvals, newest first: the ones still pending, or all; of one person, or everyone's.</summary>
    public IReadOnlyList<ApprovalRow> Approvals(bool pendingOnly, string? person = null, int limit = 200) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            var where = string.Join(" AND ", new[] { pendingOnly ? "status = 'pending'" : null, person is null ? null : "person = $person" }.Where(w => w is not null));
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection,
                $"SELECT {ApprovalColumns} FROM {{table:approval}} {(where.Length > 0 ? "WHERE " + where : "")} ORDER BY requested_at DESC, id {database.Limit("limit")};");
            if (person is not null) command.Bind(database, "person", person);
            command.Bind(database, "limit", (long)limit);
            using var reader = command.ExecuteReader();
            var rows = new List<ApprovalRow>();
            while (reader.Read()) rows.Add(ReadApproval(reader));
            return rows;
        });
}
