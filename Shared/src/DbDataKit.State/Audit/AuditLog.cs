using System.Data.Common;
using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>
/// The <c>audit</c> module (plan 29 section 3.4): who did what, through what, and when, for every service of an install in one place. Kept until an administrator sets a limit
/// (the rule has no default age: plan 29 section 3.3). The store only appends and reads; nothing in the suite updates or deletes an entry.
/// </summary>
public static class AuditModule
{
    public const string Name = "audit";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- One row for each thing done that someone may later ask about: a management change, a publish, a grant, an elevated action's write.
            -- actor is who did it (a person, an agent, an action's own principal, or the system). requested_by is the person who asked, when the actor is not that person
            -- (an elevated action run for Alice records the action as actor and Alice here). via is what it came through: an app, an agent, the console, a command.
            -- verb is 'noun.verb' (definition.publish, grant.add). outcome is ok, refused or failed. detail is a small JSON document of context: the names and ids involved,
            -- never a value, a secret or a statement's text.
            CREATE TABLE {table:entry} (
                {{identity:id}},
                service_id    {{key}} NOT NULL,
                happened_at   {{time}} NOT NULL,
                actor_kind    {{key}} NOT NULL,
                actor_id      {{key}} NOT NULL,
                requested_by  {{key}} NULL,
                via_kind      {{key}} NULL,
                via_id        {{key}} NULL,
                verb          {{key}} NOT NULL,
                target_kind   {{key}} NULL,
                target_id     {{key}} NULL,
                outcome       {{key}} NOT NULL,
                detail        {{json}} NULL
            );

            CREATE INDEX {index:entry_by_time} ON {table:entry}(happened_at);
            CREATE INDEX {index:entry_by_actor} ON {table:entry}(actor_id, id);
            CREATE INDEX {index:entry_by_target} ON {table:entry}(target_kind, target_id, id);
            """,
        ],
        retention: [new RetentionRule("entry", "happened_at", DefaultDays: null)]);
}

/// <summary>What is recorded about one thing that was done.</summary>
/// <param name="ActorKind"><c>person</c>, <c>agent</c>, <c>action</c> or <c>system</c>.</param>
/// <param name="ActorId">The principal that acted: a subject id of the identity home, an agent's or action's principal id, or the service's name for the system.</param>
/// <param name="Verb"><c>noun.verb</c>, lower case: <c>definition.publish</c>.</param>
/// <param name="Outcome"><c>ok</c>, <c>refused</c> or <c>failed</c>.</param>
/// <param name="RequestedBy">The person who asked, when the actor is not that person.</param>
/// <param name="ViaKind">What it came through: <c>app</c>, <c>agent</c>, <c>console</c>, <c>command</c>, <c>api</c>.</param>
/// <param name="Detail">A small JSON document of context. Never a value, a secret or a statement's text.</param>
public sealed record AuditEvent(
    string ActorKind, string ActorId, string Verb, string Outcome = AuditEvent.Ok,
    string? RequestedBy = null, string? ViaKind = null, string? ViaId = null,
    string? TargetKind = null, string? TargetId = null, string? Detail = null)
{
    public const string Ok = "ok";
    public const string Refused = "refused";
    public const string Failed = "failed";
}

/// <summary>An entry as it was stored.</summary>
public sealed record AuditRecord(long Id, string ServiceId, DateTimeOffset At, AuditEvent Entry);

/// <summary>Which entries to read; every field narrows.</summary>
public sealed record AuditQuery(long? BeforeId = null, string? ActorId = null, string? TargetKind = null, string? TargetId = null, string? Verb = null, string? VerbPrefix = null, string? ServiceId = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 100);

/// <summary>
/// Writes and reads the audit for one service (<paramref name="serviceId"/> is recorded on every entry, so services that share a database share one audit). Something that must be
/// recorded atomically with the change it describes writes through <see cref="Record(StateDatabase, DbConnection, DbTransaction?, string, AuditEvent)"/> in that change's transaction.
/// </summary>
public sealed partial class AuditLog(StateDatabase database, string serviceId)
{
    private const string Module = AuditModule.Name;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex Word { get; }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex Verb { get; }

    /// <summary>Records one entry on its own.</summary>
    public long Record(AuditEvent entry) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Record(database, connection, null, serviceId, entry);
        });

    /// <summary>Records one entry on a connection the caller owns, in the caller's transaction, so the record and the change it describes stand or fall together.</summary>
    public static long Record(StateDatabase database, DbConnection connection, DbTransaction? transaction, string serviceId, AuditEvent entry)
    {
        Validate(entry);
        var sql = "INSERT INTO {table:entry} (service_id, happened_at, actor_kind, actor_id, requested_by, via_kind, via_id, verb, target_kind, target_id, outcome, detail) " +
                  "VALUES ($service, $now, $actor_kind, $actor_id, $requested_by, $via_kind, $via_id, $verb, $target_kind, $target_id, $outcome, CAST($detail AS {{json}}))";
        // the new row's id comes back in one round trip, in each engine's own words
        sql += database.Dialect.Engine == StateEngineIds.MsSql ? "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT);" : " RETURNING id;";

        using var cmd = database.Command(Module, connection, transaction, sql);
        cmd.Bind(database, "service", serviceId)
            .Bind(database, "now", database.Time.GetUtcNow())
            .Bind(database, "actor_kind", entry.ActorKind)
            .Bind(database, "actor_id", entry.ActorId)
            .Bind(database, "requested_by", entry.RequestedBy, System.Data.DbType.String)
            .Bind(database, "via_kind", entry.ViaKind, System.Data.DbType.String)
            .Bind(database, "via_id", entry.ViaId, System.Data.DbType.String)
            .Bind(database, "verb", entry.Verb)
            .Bind(database, "target_kind", entry.TargetKind, System.Data.DbType.String)
            .Bind(database, "target_id", entry.TargetId, System.Data.DbType.String)
            .Bind(database, "outcome", entry.Outcome)
            .Bind(database, "detail", entry.Detail, System.Data.DbType.String);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Entries newest first, narrowed by the query. Page by passing the last id seen as <see cref="AuditQuery.BeforeId"/>.</summary>
    public IReadOnlyList<AuditRecord> Read(AuditQuery? query = null)
    {
        query ??= new AuditQuery();
        var conditions = new List<string>();
        void Add(bool when, string condition) { if (when) conditions.Add(condition); }
        Add(query.BeforeId is not null, "id < $before");
        Add(query.ActorId is not null, "actor_id = $actor");
        Add(query.TargetKind is not null, "target_kind = $target_kind");
        Add(query.TargetId is not null, "target_id = $target_id");
        Add(query.Verb is not null, "verb = $verb");
        Add(query.VerbPrefix is not null, "verb LIKE $verb_prefix ESCAPE '\\'");
        Add(query.ServiceId is not null, "service_id = $service");
        Add(query.From is not null, "happened_at >= $from");
        Add(query.To is not null, "happened_at < $to");

        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "SELECT id, service_id, happened_at, actor_kind, actor_id, requested_by, via_kind, via_id, verb, target_kind, target_id, outcome, detail FROM {table:entry} " +
                (conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions)) + $" ORDER BY id DESC {database.Limit("limit")};");
            if (query.BeforeId is { } before) cmd.Bind(database, "before", before);
            if (query.ActorId is { } actor) cmd.Bind(database, "actor", actor);
            if (query.TargetKind is { } targetKind) cmd.Bind(database, "target_kind", targetKind);
            if (query.TargetId is { } targetId) cmd.Bind(database, "target_id", targetId);
            if (query.Verb is { } verb) cmd.Bind(database, "verb", verb);
            if (query.VerbPrefix is { } prefix) cmd.Bind(database, "verb_prefix", prefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
            if (query.ServiceId is { } service) cmd.Bind(database, "service", service);
            if (query.From is { } from) cmd.Bind(database, "from", from);
            if (query.To is { } to) cmd.Bind(database, "to", to);
            cmd.Bind(database, "limit", (long)Math.Clamp(query.Limit, 1, 1000));

            using var reader = cmd.ExecuteReader();
            var found = new List<AuditRecord>();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                found.Add(new AuditRecord(reader.Int64(0), reader.GetString(1), reader.Time(2),
                    new AuditEvent(reader.GetString(3), reader.GetString(4), reader.GetString(8), reader.GetString(11), Text(5), Text(6), Text(7), Text(9), Text(10), Text(12))));
            }

            return (IReadOnlyList<AuditRecord>)found;
        });
    }

    private static void Validate(AuditEvent entry)
    {
        if (!Word.IsMatch(entry.ActorKind)) throw new ArgumentException("An audit entry's actor kind is a lower-case word (person, agent, action, system).", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.ActorId)) throw new ArgumentException("An audit entry names who acted.", nameof(entry));
        if (!Verb.IsMatch(entry.Verb)) throw new ArgumentException($"An audit entry's verb is lower case noun.verb (definition.publish); '{entry.Verb}' is not.", nameof(entry));
        if (!Word.IsMatch(entry.Outcome)) throw new ArgumentException("An audit entry's outcome is ok, refused or failed.", nameof(entry));
        if (entry.ViaKind is not null && !Word.IsMatch(entry.ViaKind)) throw new ArgumentException("An audit entry's via kind is a lower-case word (app, agent, console, command, api).", nameof(entry));
        if (entry.Detail is not null)
        {
            try { using var _ = System.Text.Json.JsonDocument.Parse(entry.Detail); }
            catch (System.Text.Json.JsonException) { throw new ArgumentException("An audit entry's detail is a JSON document.", nameof(entry)); }
        }
    }
}
