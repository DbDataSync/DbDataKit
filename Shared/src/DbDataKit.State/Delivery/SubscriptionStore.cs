using System.Data.Common;
using System.Text.Json.Nodes;

namespace DbDataKit.State;

/// <summary>What a person asked for, as the store keeps it. The strict reading of a request (<c>SubscriptionSpec</c> in <c>DbDataKit.Delivery</c>) happens before it gets here.</summary>
public sealed record SubscriptionFields(string Name, string Kind, string ViewName, JsonObject? Parameters, string? Layout, string ExportFormat, string? Widget, string Channel, string? Recipient, string Cron, string TimeZone, bool InlinePreview = false);

/// <summary>A subscription as the store holds it.</summary>
public sealed record SubscriptionRow(Guid Id, SubscriptionFields Fields, string Person, string? PersonName, string Owner, string Status, string? StatusDetail, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
    string? LastOutcome, string? LastDetail, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);

/// <summary>One run of a subscription.</summary>
public sealed record SubscriptionRunRow(Guid Id, Guid SubscriptionId, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Outcome, string? Detail, Guid? MessageId, string? RunId);

/// <summary>How a run ended: <c>sent</c> (a message is in the outbox), <c>skipped</c>, <c>access-lost</c> (the person lost access; the subscription is stopped) or <c>failed</c>.</summary>
public static class SubscriptionOutcomes
{
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string AccessLost = "access-lost";
    public const string Failed = "failed";
}

/// <summary>
/// Writes and reads <c>subscription</c> and <c>subscription_run</c> of the <c>delivery</c> module (plan 22a GP17). <see cref="ClaimDue"/> is how the runner takes the subscriptions whose time has come: each is moved to its next time by a guarded update
/// (<c>row_version</c>), so two runners never take one subscription for one occurrence, and a runner that was down takes it once, not once per missed time.
/// </summary>
public sealed class SubscriptionStore(StateDatabase database)
{
    private const string Module = DeliveryModule.Name;

    private const string Columns = "id, name, person, person_name, owner, kind, view_name, parameters, layout, export_format, widget, channel, recipient, cron, time_zone, status, status_detail, next_run_at, last_run_at, last_outcome, last_detail, created_at, updated_at, row_version, inline_preview";

    /// <summary>Writes a subscription as <c>active</c>, due at <paramref name="nextRunAt"/> (the first time its schedule gives).</summary>
    public Guid Create(SubscriptionFields fields, string person, string? personName, string owner, DateTimeOffset nextRunAt)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(person);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var id = database.NewId();
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection,
                $"INSERT INTO {{table:subscription}} ({Columns}) VALUES ($id, $name, $person, $person_name, $owner, $kind, $view, CAST($parameters AS {{{{json}}}}), $layout, $format, $widget, $channel, $recipient, $cron, $zone, 'active', NULL, $next, NULL, NULL, NULL, $now, $now, 1, $preview);");
            BindFields(insert, fields).Bind(database, "id", id).Bind(database, "person", person).Bind(database, "person_name", personName, System.Data.DbType.String).Bind(database, "owner", owner).Bind(database, "next", nextRunAt).Bind(database, "now", now);
            insert.ExecuteNonQuery();
        });
        return id;
    }

    private DbCommand BindFields(DbCommand c, SubscriptionFields f) =>
        c.Bind(database, "name", f.Name).Bind(database, "kind", f.Kind).Bind(database, "view", f.ViewName).Bind(database, "parameters", f.Parameters?.ToJsonString(), System.Data.DbType.String)
            .Bind(database, "layout", f.Layout, System.Data.DbType.String).Bind(database, "format", f.ExportFormat).Bind(database, "widget", f.Widget, System.Data.DbType.String).Bind(database, "channel", f.Channel)
            .Bind(database, "recipient", f.Recipient, System.Data.DbType.String).Bind(database, "cron", f.Cron).Bind(database, "zone", f.TimeZone).Bind(database, "preview", f.InlinePreview ? 1 : 0);

    /// <summary>Replaces what a subscription asks for (a person's edit), and its next time; it is <c>active</c> again. False when another writer changed it first (<paramref name="expectedVersion"/>) or it is gone.</summary>
    public bool Update(Guid id, SubscriptionFields fields, DateTimeOffset nextRunAt, long expectedVersion) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return database.UpdateVersioned(Module, connection, null, "subscription",
                "name = $name, kind = $kind, view_name = $view, parameters = CAST($parameters AS {{json}}), layout = $layout, export_format = $format, widget = $widget, channel = $channel, recipient = $recipient, cron = $cron, time_zone = $zone, inline_preview = $preview, " +
                "status = 'active', status_detail = NULL, next_run_at = $next, updated_at = $now", "id = $id", expectedVersion,
                c => BindFields(c, fields).Bind(database, "next", nextRunAt).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "id", id));
        });

    /// <summary>Pauses (<c>paused</c>: no next time) or resumes (<c>active</c> at <paramref name="nextRunAt"/>) a subscription, or stops it (<c>stopped</c>, with the reason). Returns false when it is gone.</summary>
    public bool SetStatus(Guid id, string status, string? detail, DateTimeOffset? nextRunAt)
    {
        if (!DeliveryModule.SubscriptionStatuses.Contains(status)) throw new ArgumentException($"'{status}' is not a status of a subscription ({string.Join(", ", DeliveryModule.SubscriptionStatuses)}).", nameof(status));
        if (status == "active" && nextRunAt is null) throw new ArgumentException("An active subscription has a next time.", nameof(nextRunAt));
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection,
                "UPDATE {table:subscription} SET status = $status, status_detail = $detail, next_run_at = $next, updated_at = $now, row_version = row_version + 1 WHERE id = $id;");
            update.Bind(database, "status", status).Bind(database, "detail", detail, System.Data.DbType.String).Bind(database, "next", status == "active" ? nextRunAt : null, System.Data.DbType.DateTimeOffset)
                .Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "id", id);
            return update.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Removes a subscription and its runs (a person's act).</summary>
    public bool Delete(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var runs = database.Command(Module, connection, transaction, "DELETE FROM {table:subscription_run} WHERE subscription_id = $id;"))
            {
                runs.Bind(database, "id", id);
                runs.ExecuteNonQuery();
            }

            int removed;
            using (var subscription = database.Command(Module, connection, transaction, "DELETE FROM {table:subscription} WHERE id = $id;"))
            {
                subscription.Bind(database, "id", id);
                removed = subscription.ExecuteNonQuery();
            }

            transaction.Commit();
            return removed == 1;
        });

    /// <summary>
    /// Takes up to <paramref name="max"/> active subscriptions that are due at <paramref name="now"/>, oldest due first, and moves each to the time <paramref name="next"/> gives for it (computed from its schedule by the caller), by a guarded update:
    /// a subscription another runner took first is skipped. A <c>next</c> of null pauses nothing and leaves the row due (the schedule could not be read; the caller says so).
    /// </summary>
    public IReadOnlyList<SubscriptionRow> ClaimDue(DateTimeOffset now, Func<SubscriptionRow, DateTimeOffset?> next, int max = 50) =>
        database.Retry(() =>
        {
            var due = new List<SubscriptionRow>();
            using (var connection = database.OpenConnection())
            {
                using var select = database.Command(Module, connection,
                    $"SELECT {Columns} FROM {{table:subscription}} WHERE status = 'active' AND next_run_at <= $now ORDER BY next_run_at, id {database.Limit("limit")};");
                select.Bind(database, "now", now).Bind(database, "limit", (long)max);
                using var reader = select.ExecuteReader();
                while (reader.Read()) due.Add(Read(reader));
            }

            var claimed = new List<SubscriptionRow>();
            foreach (var row in due)
            {
                var after = next(row);
                if (after is null) continue;
                using var connection = database.OpenConnection();
                if (database.UpdateVersioned(Module, connection, null, "subscription", "next_run_at = $next, updated_at = $now", "id = $id", row.Version,
                        c => c.Bind(database, "next", after.Value).Bind(database, "now", now).Bind(database, "id", row.Id)))
                    claimed.Add(row with { NextRunAt = after });
            }

            return (IReadOnlyList<SubscriptionRow>)claimed;
        });

    /// <summary>Records a run: the row of <c>subscription_run</c> and the subscription's own <c>last_*</c> in one transaction. A problem that a still-active subscription has (<paramref name="problem"/>) is kept in <c>status_detail</c> until the next good run clears it.</summary>
    public Guid RecordRun(Guid subscriptionId, DateTimeOffset startedAt, string outcome, string? detail, Guid? messageId = null, string? runId = null, string? problem = null)
    {
        if (outcome is not (SubscriptionOutcomes.Sent or SubscriptionOutcomes.Skipped or SubscriptionOutcomes.AccessLost or SubscriptionOutcomes.Failed))
            throw new ArgumentException($"'{outcome}' is not an outcome of a subscription's run.", nameof(outcome));
        var id = database.NewId();
        var now = database.Time.GetUtcNow();
        var text = DeliveryStore.Clip(detail);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var insert = database.Command(Module, connection, transaction,
                       "INSERT INTO {table:subscription_run} (id, subscription_id, started_at, finished_at, outcome, detail, message_id, run_id) VALUES ($id, $subscription, $started, $finished, $outcome, $detail, $message, $run);"))
            {
                insert.Bind(database, "id", id).Bind(database, "subscription", subscriptionId).Bind(database, "started", startedAt).Bind(database, "finished", now).Bind(database, "outcome", outcome)
                    .Bind(database, "detail", text, System.Data.DbType.String).Bind(database, "message", messageId, System.Data.DbType.Guid).Bind(database, "run", runId, System.Data.DbType.String);
                insert.ExecuteNonQuery();
            }

            using (var update = database.Command(Module, connection, transaction,
                       "UPDATE {table:subscription} SET last_run_at = $started, last_outcome = $outcome, last_detail = $detail, status_detail = CASE WHEN status = 'active' THEN $problem ELSE status_detail END, updated_at = $now, row_version = row_version + 1 WHERE id = $id;"))
            {
                update.Bind(database, "started", startedAt).Bind(database, "outcome", outcome).Bind(database, "detail", text, System.Data.DbType.String).Bind(database, "problem", problem, System.Data.DbType.String)
                    .Bind(database, "now", now).Bind(database, "id", subscriptionId);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
        });
        return id;
    }

    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static SubscriptionRow Read(DbDataReader r) => new(r.Uuid(0),
        new SubscriptionFields(r.GetString(1), r.GetString(5), r.GetString(6), Text(r, 7) is { } json ? JsonNode.Parse(json) as JsonObject : null, Text(r, 8), r.GetString(9), Text(r, 10), r.GetString(11), Text(r, 12), r.GetString(13), r.GetString(14), InlinePreview: r.FieldCount > 24 && !r.IsDBNull(24) && Convert.ToInt32(r.GetValue(24), System.Globalization.CultureInfo.InvariantCulture) != 0),
        r.GetString(2), Text(r, 3), r.GetString(4), r.GetString(15), Text(r, 16), r.NullableTime(17), r.NullableTime(18), Text(r, 19), Text(r, 20), r.Time(21), r.Time(22), r.Int64(23));

    public SubscriptionRow? Find(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:subscription}} WHERE id = $id;");
            select.Bind(database, "id", id);
            using var reader = select.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    /// <summary>The subscriptions, by name, optionally of one person (their own), one channel or one status.</summary>
    public IReadOnlyList<SubscriptionRow> List(string? person = null, string? channel = null, string? status = null) =>
        database.Retry(() =>
        {
            var filters = new List<string>();
            if (person is not null) filters.Add("person = $person");
            if (channel is not null) filters.Add("channel = $channel");
            if (status is not null) filters.Add("status = $status");
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:subscription}} {(filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "")} ORDER BY name, id;");
            if (person is not null) select.Bind(database, "person", person);
            if (channel is not null) select.Bind(database, "channel", channel);
            if (status is not null) select.Bind(database, "status", status);
            using var reader = select.ExecuteReader();
            var rows = new List<SubscriptionRow>();
            while (reader.Read()) rows.Add(Read(reader));
            return rows;
        });

    /// <summary>The runs of a subscription, newest first.</summary>
    public IReadOnlyList<SubscriptionRunRow> Runs(Guid subscriptionId, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection,
                $"SELECT id, subscription_id, started_at, finished_at, outcome, detail, message_id, run_id FROM {{table:subscription_run}} WHERE subscription_id = $id ORDER BY started_at DESC, id DESC {database.Limit("limit")};");
            select.Bind(database, "id", subscriptionId).Bind(database, "limit", (long)limit);
            using var reader = select.ExecuteReader();
            var rows = new List<SubscriptionRunRow>();
            while (reader.Read()) rows.Add(new SubscriptionRunRow(reader.Uuid(0), reader.Uuid(1), reader.Time(2), reader.Time(3), reader.GetString(4), Text(reader, 5), reader.NullableUuid(6), Text(reader, 7)));
            return rows;
        });
}
