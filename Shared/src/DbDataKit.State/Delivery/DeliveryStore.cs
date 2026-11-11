using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.State;

/// <summary>An attachment as the outbox keeps it: the payload that holds the bytes (owned by the message row), and how it is called and typed.</summary>
public sealed record AttachmentRef(Guid Payload, string Name, string MediaType, long Size);

/// <summary>A message to write to the outbox. <paramref name="Id"/> is chosen by the caller when the attachments' payloads were written first (they name the message row as their owner).</summary>
public sealed record NewMessage(string Channel, string Subject, IReadOnlyList<string>? Recipients = null, string? BodyText = null, string? BodyHtml = null, string? Link = null,
    IReadOnlyList<AttachmentRef>? Attachments = null, string? Source = null, string? OwnerPerson = null, Guid? Id = null, DateTimeOffset? NotBefore = null);

/// <summary>A message as the outbox holds it.</summary>
public sealed record MessageRow(Guid Id, string Channel, IReadOnlyList<string> Recipients, string Subject, string? BodyText, string? BodyHtml, string? Link, IReadOnlyList<AttachmentRef> Attachments, string? Source, string? OwnerPerson,
    string Status, int Attempts, DateTimeOffset NextAttemptAt, string? LeaseOwner, DateTimeOffset? LeaseExpiresAt, string? LastOutcome, string? LastDetail, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SettledAt);

/// <summary>One try of a message.</summary>
public sealed record AttemptRow(Guid Id, Guid MessageId, int Number, string Sender, string Outcome, string? Detail, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

/// <summary>How a try ended, as the store records it: <c>sent</c>, <c>temporary</c> (to be tried again), <c>permanent</c> (parked), <c>error</c> (the channel threw; treated like temporary).</summary>
public static class AttemptOutcomes
{
    public const string Sent = "sent";
    public const string Temporary = "temporary";
    public const string Permanent = "permanent";
    public const string Error = "error";
}

/// <summary>
/// Writes and reads the <c>delivery</c> module's outbox (<see cref="DeliveryModule"/>). <see cref="Claim"/> takes a message for one sender under a lease with a guarded update (<c>row_version</c>), so two senders on one database never hold the
/// same message; <see cref="Complete"/> records the try and moves the message in <b>one transaction</b>, and only for the sender that holds the lease, so a sender that lost its lease cannot settle a message another one now has.
/// </summary>
public sealed class DeliveryStore(StateDatabase database)
{
    private const string Module = DeliveryModule.Name;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The most a message keeps of a channel's words in a row.</summary>
    public const int MaxDetail = 2000;

    public static string Clip(string? detail) => detail is null ? "" : detail.Length <= MaxDetail ? detail : detail[..MaxDetail] + "...";

    /// <summary>Writes a message as <c>pending</c>, to be taken at <see cref="NewMessage.NotBefore"/> (now by default). Returns its id.</summary>
    public Guid Enqueue(NewMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Subject);
        if (string.IsNullOrEmpty(message.BodyText) && string.IsNullOrEmpty(message.BodyHtml) && message.Link is null && message.Attachments is not { Count: > 0 })
            throw new ArgumentException("A message has a body, a link or an attachment.", nameof(message));
        var id = message.Id ?? database.NewId();
        var now = database.Time.GetUtcNow();
        var recipients = message.Recipients is { Count: > 0 } r ? JsonSerializer.Serialize(r, Json) : null;
        var attachments = message.Attachments is { Count: > 0 } a ? JsonSerializer.Serialize(a.Select(x => new { payload = x.Payload.ToString("D"), name = x.Name, media_type = x.MediaType, size = x.Size }), Json) : null;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:message} (id, channel, recipients, subject, body_text, body_html, link, attachments, source, owner_person, status, attempts, next_attempt_at, created_at, updated_at, row_version) " +
                "VALUES ($id, $channel, CAST($recipients AS {{json}}), $subject, $text, $html, $link, CAST($attachments AS {{json}}), $source, $person, 'pending', 0, $next, $now, $now, 1);");
            insert.Bind(database, "id", id).Bind(database, "channel", message.Channel).Bind(database, "recipients", recipients, System.Data.DbType.String).Bind(database, "subject", message.Subject)
                .Bind(database, "text", message.BodyText, System.Data.DbType.String).Bind(database, "html", message.BodyHtml, System.Data.DbType.String).Bind(database, "link", message.Link, System.Data.DbType.String)
                .Bind(database, "attachments", attachments, System.Data.DbType.String).Bind(database, "source", message.Source, System.Data.DbType.String).Bind(database, "person", message.OwnerPerson, System.Data.DbType.String)
                .Bind(database, "next", message.NotBefore ?? now).Bind(database, "now", now);
            insert.ExecuteNonQuery();
        });
        return id;
    }

    /// <summary>
    /// Takes up to <paramref name="max"/> messages for <paramref name="sender"/>: the pending ones whose time has come and the ones whose lease ran out, oldest first, each by a guarded update, so a message another sender took first is skipped.
    /// A claimed message is <c>sending</c> until <paramref name="lease"/> from now.
    /// </summary>
    public IReadOnlyList<MessageRow> Claim(string sender, TimeSpan lease, int max = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            var candidates = new List<(Guid Id, long Version)>();
            using (var connection = database.OpenConnection())
            {
                using var select = database.Command(Module, connection,
                    $"SELECT id, row_version FROM {{table:message}} WHERE (status = 'pending' AND next_attempt_at <= $now) OR (status = 'sending' AND lease_expires_at < $now) ORDER BY next_attempt_at, id {database.Limit("limit")};");
                select.Bind(database, "now", now).Bind(database, "limit", (long)max * 4);
                using var reader = select.ExecuteReader();
                while (reader.Read()) candidates.Add((reader.Uuid(0), reader.Int64(1)));
            }

            var claimed = new List<MessageRow>();
            foreach (var (id, version) in candidates)
            {
                if (claimed.Count >= max) break;
                using var connection = database.OpenConnection();
                if (!database.UpdateVersioned(Module, connection, null, "message", "status = 'sending', lease_owner = $sender, lease_expires_at = $lease, updated_at = $now", "id = $id", version,
                        c => c.Bind(database, "sender", sender).Bind(database, "lease", now + lease).Bind(database, "now", now).Bind(database, "id", id)))
                    continue;
                if (Find(id) is { } row) claimed.Add(row);
            }
            return (IReadOnlyList<MessageRow>)claimed;
        });
    }

    /// <summary>
    /// Records a try and moves the message, in one transaction, when <paramref name="sender"/> still holds it: <c>sent</c> settles it; anything else with <paramref name="retryAt"/> sets the time for the next try and puts it back to <c>pending</c>;
    /// with no <paramref name="retryAt"/> it is parked as <c>dead</c>. False when the sender no longer holds the message (its lease ran out and another sender took it): nothing is recorded.
    /// </summary>
    public bool Complete(Guid id, string sender, string outcome, string? detail, DateTimeOffset startedAt, DateTimeOffset? retryAt = null)
    {
        if (outcome is not (AttemptOutcomes.Sent or AttemptOutcomes.Temporary or AttemptOutcomes.Permanent or AttemptOutcomes.Error))
            throw new ArgumentException($"'{outcome}' is not an outcome of a try (sent, temporary, permanent, error).", nameof(outcome));
        var now = database.Time.GetUtcNow();
        var text = Clip(detail);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            int number;
            using (var read = database.Command(Module, connection, transaction, "SELECT attempts FROM {table:message} WHERE id = $id AND status = 'sending' AND lease_owner = $sender;"))
            {
                read.Bind(database, "id", id).Bind(database, "sender", sender);
                var value = read.ExecuteScalar();
                if (value is null or DBNull) return false;
                number = Convert.ToInt32(value, CultureInfo.InvariantCulture) + 1;
            }

            var sent = outcome == AttemptOutcomes.Sent;
            var status = sent ? "sent" : retryAt is null ? "dead" : "pending";
            using (var update = database.Command(Module, connection, transaction,
                       "UPDATE {table:message} SET status = $status, attempts = $number, next_attempt_at = $next, lease_owner = NULL, lease_expires_at = NULL, last_outcome = $outcome, last_detail = $detail, updated_at = $now, " +
                       "settled_at = $settled, row_version = row_version + 1 WHERE id = $id AND status = 'sending' AND lease_owner = $sender;"))
            {
                update.Bind(database, "status", status).Bind(database, "number", (long)number).Bind(database, "next", retryAt ?? now).Bind(database, "outcome", outcome == AttemptOutcomes.Error ? AttemptOutcomes.Temporary : outcome)
                    .Bind(database, "detail", text, System.Data.DbType.String).Bind(database, "now", now).Bind(database, "settled", status is "sent" or "dead" ? now : null, System.Data.DbType.DateTimeOffset)
                    .Bind(database, "id", id).Bind(database, "sender", sender);
                if (update.ExecuteNonQuery() != 1) return false;
            }

            using (var insert = database.Command(Module, connection, transaction,
                       "INSERT INTO {table:attempt} (id, message_id, number, sender, outcome, detail, started_at, finished_at) VALUES ($id, $message, $number, $sender, $outcome, $detail, $started, $finished);"))
            {
                insert.Bind(database, "id", database.NewId()).Bind(database, "message", id).Bind(database, "number", (long)number).Bind(database, "sender", sender).Bind(database, "outcome", outcome)
                    .Bind(database, "detail", text, System.Data.DbType.String).Bind(database, "started", startedAt).Bind(database, "finished", now);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        });
    }

    /// <summary>Gives a claimed message back without a try (a channel's limit per minute has been reached): it is pending again at <paramref name="until"/>. False when the sender no longer holds it.</summary>
    public bool Release(Guid id, string sender, DateTimeOffset until) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection,
                "UPDATE {table:message} SET status = 'pending', next_attempt_at = $until, lease_owner = NULL, lease_expires_at = NULL, updated_at = $now, row_version = row_version + 1 WHERE id = $id AND status = 'sending' AND lease_owner = $sender;");
            update.Bind(database, "until", until).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "id", id).Bind(database, "sender", sender);
            return update.ExecuteNonQuery() == 1;
        });

    /// <summary>Puts a parked (<c>dead</c>) message back to <c>pending</c> for another round of tries, as a person's act; its tries so far stay. False when it is not parked.</summary>
    public bool Requeue(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var now = database.Time.GetUtcNow();
            using var update = database.Command(Module, connection,
                "UPDATE {table:message} SET status = 'pending', next_attempt_at = $now, settled_at = NULL, updated_at = $now, row_version = row_version + 1 WHERE id = $id AND status = 'dead';");
            update.Bind(database, "now", now).Bind(database, "id", id);
            return update.ExecuteNonQuery() == 1;
        });

    private const string Columns = "id, channel, recipients, subject, body_text, body_html, link, attachments, source, owner_person, status, attempts, next_attempt_at, lease_owner, lease_expires_at, last_outcome, last_detail, created_at, updated_at, settled_at";

    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static MessageRow ReadMessage(DbDataReader r)
    {
        var recipients = Text(r, 2) is { } rj ? JsonSerializer.Deserialize<List<string>>(rj, Json) ?? [] : [];
        var attachments = new List<AttachmentRef>();
        if (Text(r, 7) is { } aj && JsonNode.Parse(aj) is JsonArray list)
            foreach (var item in list.OfType<JsonObject>())
                attachments.Add(new AttachmentRef(Guid.Parse((string)item["payload"]!), (string)item["name"]!, (string)item["media_type"]!, (long)item["size"]!));
        return new MessageRow(r.Uuid(0), r.GetString(1), recipients, r.GetString(3), Text(r, 4), Text(r, 5), Text(r, 6), attachments, Text(r, 8), Text(r, 9), r.GetString(10), r.Int32(11), r.Time(12), Text(r, 13), r.NullableTime(14),
            Text(r, 15), Text(r, 16), r.Time(17), r.Time(18), r.NullableTime(19));
    }

    /// <summary>One message, or null.</summary>
    public MessageRow? Find(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:message}} WHERE id = $id;");
            select.Bind(database, "id", id);
            using var reader = select.ExecuteReader();
            return reader.Read() ? ReadMessage(reader) : null;
        });

    /// <summary>The messages, newest first, optionally of one status, channel, source or owner.</summary>
    public IReadOnlyList<MessageRow> List(string? status = null, string? channel = null, string? source = null, string? ownerPerson = null, int limit = 100) =>
        database.Retry(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            var filters = new List<string>();
            if (status is not null) filters.Add("status = $status");
            if (channel is not null) filters.Add("channel = $channel");
            if (source is not null) filters.Add("source = $source");
            if (ownerPerson is not null) filters.Add("owner_person = $person");
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection,
                $"SELECT {Columns} FROM {{table:message}} {(filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "")} ORDER BY created_at DESC, id DESC {database.Limit("limit")};");
            if (status is not null) select.Bind(database, "status", status);
            if (channel is not null) select.Bind(database, "channel", channel);
            if (source is not null) select.Bind(database, "source", source);
            if (ownerPerson is not null) select.Bind(database, "person", ownerPerson);
            select.Bind(database, "limit", (long)limit);
            using var reader = select.ExecuteReader();
            var rows = new List<MessageRow>();
            while (reader.Read()) rows.Add(ReadMessage(reader));
            return rows;
        });

    /// <summary>The tries of a message, in order.</summary>
    public IReadOnlyList<AttemptRow> Attempts(Guid messageId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, "SELECT id, message_id, number, sender, outcome, detail, started_at, finished_at FROM {table:attempt} WHERE message_id = $id ORDER BY number;");
            select.Bind(database, "id", messageId);
            using var reader = select.ExecuteReader();
            var rows = new List<AttemptRow>();
            while (reader.Read()) rows.Add(new AttemptRow(reader.Uuid(0), reader.Uuid(1), reader.Int32(2), reader.GetString(3), reader.GetString(4), Text(reader, 5), reader.Time(6), reader.Time(7)));
            return rows;
        });

    /// <summary>How many messages are in each status.</summary>
    public IReadOnlyDictionary<string, int> Counts() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, "SELECT status, COUNT(*) FROM {table:message} GROUP BY status;");
            using var reader = select.ExecuteReader();
            var counts = DeliveryModule.MessageStatuses.ToDictionary(s => s, _ => 0);
            while (reader.Read()) counts[reader.GetString(0)] = reader.Int32(1);
            return counts;
        });
}
