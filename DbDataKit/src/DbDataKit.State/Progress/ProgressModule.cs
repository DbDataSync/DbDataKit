namespace DbDataKit.State;

/// <summary>
/// The <c>runner_progress</c> module (plan 29 section 4, plan 29a Q20): what a task needs to rerun without losing or repeating work. Kept per group (the runner group's instances share it). Every row is keyed
/// by the <b>package, the channel and the task</b>, so a preview's progress never mixes with production's. A <b>checkpoint</b> is where a task got to in an outside source; it moves only with the work it
/// stands for (see <see cref="ProgressStore.Advance"/>), is guarded by a <c>row_version</c>, keeps a history, and is typed, never JSON except as a declared last resort. Large bodies (a refused record, a
/// received webhook) are payloads (<see cref="PayloadStore"/>), referred to by id.
/// <para>
/// A value's type is one of text, int, decimal, time, date, bool, bytes (a payload id) or json, and lives in the column of its type; a date is its ISO text. Nothing is serialised and parsed back, and no
/// DDL is ever run for an integration: the tables never change shape per task.
/// </para>
/// </summary>
public static class ProgressModule
{
    public const string Name = "runner_progress";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- A checkpoint's head: one row per package, channel, task, name and item key (a customer, a folder: '' when the checkpoint is not kept per key), with the row_version that
            -- keeps two overlapping runs from both moving it, and the run that last did.
            CREATE TABLE {table:checkpoint_head} (
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                name          {{key}} NOT NULL,
                item_key      {{key}} NOT NULL,
                row_version   {{int}} NOT NULL,
                updated_at    {{time}} NOT NULL,
                updated_by    {{key}} NULL,
                PRIMARY KEY (package, channel, task, name, item_key)
            );

            -- The parts of a checkpoint (a time and an id to break ties): each its own typed value.
            CREATE TABLE {table:checkpoint_part} (
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                name          {{key}} NOT NULL,
                item_key      {{key}} NOT NULL,
                part          {{key}} NOT NULL,
                value_type    {{key}} NOT NULL,
                value_text    {{text}} NULL,
                value_int     {{int}} NULL,
                value_decimal {{decimal}} NULL,
                value_at    {{time}} NULL,
                value_bool    {{bool}} NULL,
                value_payload {{uuid}} NULL,
                value_json    {{json}} NULL,
                PRIMARY KEY (package, channel, task, name, item_key, part)
            );

            -- Every change to a part of a checkpoint: its value before and after in a canonical text form, the head's version it made, who and when. An operator reads it to see how far a task got and
            -- rewinds from it ("reload everything since Monday"); the values before a migration of the declaration are kept here too, so a rollback restores them.
            CREATE TABLE {table:checkpoint_history} (
                {{identity:id}},
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                name          {{key}} NOT NULL,
                item_key      {{key}} NOT NULL,
                part          {{key}} NOT NULL,
                value_type    {{key}} NOT NULL,
                old_value     {{text}} NULL,
                new_value     {{text}} NULL,
                head_version  {{int}} NOT NULL,
                run_id        {{key}} NULL,
                actor         {{key}} NULL,
                changed_at    {{time}} NOT NULL
            );
            CREATE INDEX {index:checkpoint_history_by_name} ON {table:checkpoint_history}(package, channel, task, name, item_key, id);

            -- Small named values a task keeps between runs, typed like the parts of a checkpoint.
            CREATE TABLE {table:state_value} (
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                name          {{key}} NOT NULL,
                item_key      {{key}} NOT NULL,
                row_version   {{int}} NOT NULL,
                value_type    {{key}} NOT NULL,
                value_text    {{text}} NULL,
                value_int     {{int}} NULL,
                value_decimal {{decimal}} NULL,
                value_at    {{time}} NULL,
                value_bool    {{bool}} NULL,
                value_payload {{uuid}} NULL,
                value_json    {{json}} NULL,
                updated_at    {{time}} NOT NULL,
                PRIMARY KEY (package, channel, task, name, item_key)
            );

            -- Each file a task has seen on a connection: where, how big, when modified, the hash of its content (a renamed or re-sent file is recognised by that), when first seen, which run
            -- processed it and how it went. outcome is empty until processed.
            CREATE TABLE {table:file_ledger} (
                {{identity:id}},
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                connection    {{key}} NOT NULL,
                path          {{text}} NOT NULL,
                size          {{int}} NOT NULL,
                modified_at   {{time}} NOT NULL,
                content_hash  {{key}} NOT NULL,
                first_seen_at {{time}} NOT NULL,
                run_id        {{key}} NULL,
                outcome       {{key}} NULL
            );
            CREATE UNIQUE INDEX {index:file_ledger_by_hash} ON {table:file_ledger}(package, channel, task, connection, content_hash);

            -- A key for each outbound write and its result, with an expiry, so a retried push does not create a second ticket.
            CREATE TABLE {table:idempotency_key} (
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                idem_key      {{key}} NOT NULL,
                result        {{text}} NULL,
                created_at    {{time}} NOT NULL,
                expires_at    {{time}} NOT NULL,
                PRIMARY KEY (package, channel, task, idem_key)
            );
            CREATE INDEX {index:idempotency_key_by_expiry} ON {table:idempotency_key}(expires_at);

            -- A record an outside system refused: where from, the error, how many times it was tried, open, replayed or discarded, and its body as a payload.
            CREATE TABLE {table:dead_letter} (
                id             {{uuid}} PRIMARY KEY,
                package        {{key}} NOT NULL,
                channel        {{key}} NOT NULL,
                task           {{key}} NOT NULL,
                source         {{key}} NOT NULL,
                error          {{text}} NOT NULL,
                attempts       {{int}} NOT NULL,
                status         {{key}} NOT NULL,
                body_payload   {{uuid}} NULL,
                first_failed_at {{time}} NOT NULL,
                last_failed_at  {{time}} NOT NULL,
                resolved_at    {{time}} NULL
            );
            CREATE INDEX {index:dead_letter_by_status} ON {table:dead_letter}(package, channel, task, status);

            -- Webhooks received for a task: where from, whether the signature checked out, when, which run took it, and the body as a payload.
            CREATE TABLE {table:inbox_item} (
                id            {{uuid}} PRIMARY KEY,
                package       {{key}} NOT NULL,
                channel       {{key}} NOT NULL,
                task          {{key}} NOT NULL,
                source        {{key}} NOT NULL,
                signature_ok  {{bool}} NOT NULL,
                received_at   {{time}} NOT NULL,
                taken_by      {{key}} NULL,
                taken_at      {{time}} NULL,
                body_payload  {{uuid}} NULL
            );
            CREATE INDEX {index:inbox_item_by_task} ON {table:inbox_item}(package, channel, task, taken_by, received_at);
            """,
        ],
        dependsOn: [PayloadStore.Module],
        retention:
        [
            // history, expired keys, settled dead letters and handled webhooks go after the age; a checkpoint's head and parts, state, the ledger of what was processed and open dead letters are current state
            new RetentionRule("checkpoint_history", "changed_at"),
            new RetentionRule("idempotency_key", "expires_at"),
            new RetentionRule("dead_letter", "last_failed_at", Where: "status <> 'open'"),
            new RetentionRule("inbox_item", "received_at", Where: "taken_by IS NOT NULL"),
        ],
        payloadOwners: [new PayloadOwner("dead_letter", "id", KeyIsUuid: true), new PayloadOwner("inbox_item", "id", KeyIsUuid: true)]);
}

/// <summary>What kind of value a checkpoint part or a state value is (plan 29 section 4.3). <see cref="Json"/> is the last resort for a value that has no better type.</summary>
public enum ProgressType
{
    Text,
    Int,
    Decimal,
    Time,
    Date,
    Bool,
    Bytes,
    Json,
}

/// <summary>
/// One typed value of a task's progress. The value is held as what it is (a <see cref="DateTimeOffset"/>, a <see cref="decimal"/>), stored in the column of its type, and read back as the same type.
/// <see cref="OfBytes"/> is a payload id: bytes themselves are never a column of this module.
/// </summary>
public sealed record ProgressValue
{
    private ProgressValue(ProgressType type, object value)
    {
        Type = type;
        Value = value;
    }

    public ProgressType Type { get; }

    /// <summary>A <see cref="string"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, <see cref="bool"/>, <see cref="Guid"/> (a payload) or a JSON <see cref="string"/>, by <see cref="Type"/>.</summary>
    public object Value { get; }

    public static ProgressValue OfText(string value) => new(ProgressType.Text, value);

    public static ProgressValue OfInt(long value) => new(ProgressType.Int, value);

    public static ProgressValue OfDecimal(decimal value) => new(ProgressType.Decimal, value);

    public static ProgressValue OfTime(DateTimeOffset value) => new(ProgressType.Time, value.ToUniversalTime());

    public static ProgressValue OfDate(DateOnly value) => new(ProgressType.Date, value);

    public static ProgressValue OfBool(bool value) => new(ProgressType.Bool, value);

    /// <summary>A payload (<see cref="PayloadStore"/>), by id.</summary>
    public static ProgressValue OfBytes(Guid payloadId) => new(ProgressType.Bytes, payloadId);

    /// <summary>A JSON document, for a value that has no better type; it must parse.</summary>
    public static ProgressValue OfJson(string json)
    {
        using var _ = System.Text.Json.JsonDocument.Parse(json);
        return new(ProgressType.Json, json);
    }

    public string AsText => (string)Value;

    public long AsInt => (long)Value;

    public decimal AsDecimal => (decimal)Value;

    public DateTimeOffset AsTime => (DateTimeOffset)Value;

    public DateOnly AsDate => (DateOnly)Value;

    public bool AsBool => (bool)Value;

    public Guid AsPayload => (Guid)Value;

    /// <summary>The value as invariant text, the form the history keeps and <see cref="Parse"/> reads back.</summary>
    public string Canonical() => Value switch
    {
        string text => text,
        long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset time => StateValues.FormatTime(time),
        DateOnly date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString("D"),
        _ => throw new InvalidOperationException("Unknown progress value."),
    };

    public static ProgressValue Parse(ProgressType type, string text) => type switch
    {
        ProgressType.Text => OfText(text),
        ProgressType.Int => OfInt(long.Parse(text, System.Globalization.CultureInfo.InvariantCulture)),
        ProgressType.Decimal => OfDecimal(decimal.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)),
        ProgressType.Time => OfTime(StateValues.ToTime(text)),
        ProgressType.Date => OfDate(DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        ProgressType.Bool => OfBool(text == "true"),
        ProgressType.Bytes => OfBytes(Guid.Parse(text, System.Globalization.CultureInfo.InvariantCulture)),
        ProgressType.Json => OfJson(text),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public override string ToString() => $"{Type}:{Canonical()}";
}
