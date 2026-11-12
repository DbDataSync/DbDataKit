using System.Data.Common;
using System.Text.Json.Nodes;

namespace DbDataKit.State;

/// <summary>The fixed set of alert kinds (plan 22a GP16, QG13): what the suite can tell people about by itself.</summary>
public static class AlertKinds
{
    /// <summary>A job's last run failed, was interrupted or finished with failures.</summary>
    public const string FailedRun = "failed-run";

    /// <summary>A job or dataset is overdue, behind its source or its data check failed.</summary>
    public const string LateData = "late-data";

    /// <summary>A change waits for somebody's approval.</summary>
    public const string ApprovalsWaiting = "approvals-waiting";

    /// <summary>A preview is about to expire.</summary>
    public const string ExpiringPreview = "expiring-preview";

    public static IReadOnlyList<string> All { get; } = [FailedRun, LateData, ApprovalsWaiting, ExpiringPreview];

    /// <summary>The words a person reads for a kind.</summary>
    public static string Title(string kind) => kind switch
    {
        FailedRun => "Failed runs",
        LateData => "Late data",
        ApprovalsWaiting => "Approvals waiting",
        ExpiringPreview => "Expiring previews",
        _ => kind,
    };
}

/// <summary>One alert rule: whether a kind is on, where it goes and how often one source may alert.</summary>
public sealed record AlertRule(string Kind, bool Enabled, string? Channel, IReadOnlyList<string> Recipients, int CooldownMinutes, string? ReleaseChannel, DateTimeOffset UpdatedAt, long Version)
{
    public const int DefaultCooldownMinutes = 60;

    /// <summary>The rule of a kind nobody has set: off, feed only.</summary>
    public static AlertRule Default(string kind) => new(kind, false, null, [], DefaultCooldownMinutes, null, DateTimeOffset.MinValue, 0);
}

/// <summary>What was last sent for one source to one recipient.</summary>
public sealed record AlertStateRow(string Source, string Recipient, string Kind, string Status, DateTimeOffset LastSentAt, string? LastMessage, long Version)
{
    public const string Failing = "failing";
    public const string Ok = "ok";

    /// <summary>The recipient name of a channel's own address (a webhook has no per-person recipient).</summary>
    public const string ChannelRecipient = "*";
}

/// <summary>Writes and reads <c>alert_rule</c> and <c>alert_state</c> of the <c>delivery</c> module (plan 22a GP16).</summary>
public sealed class AlertStore(StateDatabase database)
{
    private const string Module = DeliveryModule.Name;

    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>The rule of every kind of <see cref="AlertKinds.All"/> (a kind nobody set is the default: off).</summary>
    public IReadOnlyList<AlertRule> Rules()
    {
        var stored = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, "SELECT kind, enabled, channel, recipients, cooldown_minutes, release_channel, updated_at, row_version FROM {table:alert_rule};");
            using var reader = select.ExecuteReader();
            var rows = new List<AlertRule>();
            while (reader.Read())
                rows.Add(new AlertRule(reader.GetString(0), reader.Int64(1) != 0, Text(reader, 2), Text(reader, 3) is { } json && JsonNode.Parse(json) is JsonArray a ? [.. a.Select(x => x!.GetValue<string>())] : [],
                    (int)reader.Int64(4), Text(reader, 5), reader.Time(6), reader.Int64(7)));
            return rows;
        });
        return [.. AlertKinds.All.Select(k => stored.FirstOrDefault(r => r.Kind == k) ?? AlertRule.Default(k))];
    }

    /// <summary>Sets a rule. False when another writer changed it first (<paramref name="expectedVersion"/>; 0 for a rule nobody wrote yet).</summary>
    public bool SetRule(string kind, bool enabled, string? channel, IReadOnlyList<string> recipients, int cooldownMinutes, string? releaseChannel, long expectedVersion)
    {
        if (!AlertKinds.All.Contains(kind)) throw new ArgumentException($"'{kind}' is not a kind of alert ({string.Join(", ", AlertKinds.All)}).", nameof(kind));
        if (cooldownMinutes is < 1 or > 7 * 24 * 60) throw new ArgumentException("The cool-down is from 1 minute to 7 days.", nameof(cooldownMinutes));
        var json = new JsonArray([.. recipients.Select(r => (JsonNode?)JsonValue.Create(r))]).ToJsonString();
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            if (expectedVersion == 0)
            {
                using var insert = database.Command(Module, connection,
                    "INSERT INTO {table:alert_rule} (kind, enabled, channel, recipients, cooldown_minutes, release_channel, updated_at, row_version) SELECT $kind, $enabled, $channel, CAST($recipients AS {{json}}), $cooldown, $release, $now, 1 WHERE NOT EXISTS (SELECT 1 FROM {table:alert_rule} WHERE kind = $kind);");
                Bind(insert, kind, enabled, channel, json, cooldownMinutes, releaseChannel, now);
                return insert.ExecuteNonQuery() == 1;
            }

            return database.UpdateVersioned(Module, connection, null, "alert_rule", "enabled = $enabled, channel = $channel, recipients = CAST($recipients AS {{json}}), cooldown_minutes = $cooldown, release_channel = $release, updated_at = $now", "kind = $kind", expectedVersion,
                c => Bind(c, kind, enabled, channel, json, cooldownMinutes, releaseChannel, now));
        });
    }

    private void Bind(DbCommand c, string kind, bool enabled, string? channel, string json, int cooldown, string? release, DateTimeOffset now) =>
        c.Bind(database, "kind", kind).Bind(database, "enabled", enabled ? 1L : 0L).Bind(database, "channel", string.IsNullOrWhiteSpace(channel) ? null : channel, System.Data.DbType.String).Bind(database, "recipients", json, System.Data.DbType.String)
            .Bind(database, "cooldown", (long)cooldown).Bind(database, "release", string.IsNullOrWhiteSpace(release) ? null : release, System.Data.DbType.String).Bind(database, "now", now);

    /// <summary>What was last sent, optionally of one kind or only the sources still failing.</summary>
    public IReadOnlyList<AlertStateRow> States(string? kind = null, bool failingOnly = false) =>
        database.Retry(() =>
        {
            var filters = new List<string>();
            if (kind is not null) filters.Add("kind = $kind");
            if (failingOnly) filters.Add("status = 'failing'");
            using var connection = database.OpenConnection();
            using var select = database.Command(Module, connection, $"SELECT source, recipient, kind, status, last_sent_at, last_message, row_version FROM {{table:alert_state}} {(filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "")} ORDER BY source, recipient;");
            if (kind is not null) select.Bind(database, "kind", kind);
            using var reader = select.ExecuteReader();
            var rows = new List<AlertStateRow>();
            while (reader.Read()) rows.Add(new AlertStateRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.Time(4), Text(reader, 5), reader.Int64(6)));
            return rows;
        });

    /// <summary>Records that <paramref name="status"/> was sent for a source to a recipient now (an insert, or an update of the row that is there).</summary>
    public void Record(string source, string recipient, string kind, string status, string? message)
    {
        var now = database.Time.GetUtcNow();
        var text = DeliveryStore.Clip(message);
        void Bind(DbCommand c) => c.Bind(database, "source", source).Bind(database, "recipient", recipient).Bind(database, "kind", kind).Bind(database, "status", status).Bind(database, "now", now).Bind(database, "message", text, System.Data.DbType.String);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var update = database.Command(Module, connection,
                       "UPDATE {table:alert_state} SET kind = $kind, status = $status, last_sent_at = $now, last_message = $message, row_version = row_version + 1 WHERE source = $source AND recipient = $recipient;"))
            {
                Bind(update);
                if (update.ExecuteNonQuery() == 1) return;
            }

            using var insert = database.Command(Module, connection,
                "INSERT INTO {table:alert_state} (source, recipient, kind, status, last_sent_at, last_message, row_version) VALUES ($source, $recipient, $kind, $status, $now, $message, 1);");
            Bind(insert);
            insert.ExecuteNonQuery();
        });
    }
}
