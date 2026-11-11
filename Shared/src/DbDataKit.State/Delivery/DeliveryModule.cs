namespace DbDataKit.State;

/// <summary>
/// The <c>delivery</c> module (plan 22a GP07, plan 22 section 5): the outbox of the delivery service of <c>DbDataKit.Delivery</c>. A message is written once, taken by one sender at a time under a lease, tried, and ends <c>sent</c> or <c>dead</c>;
/// every try is a row of <c>attempt</c>, so "what happened to this message" has an answer after the fact.
/// <para>
/// <b>What a row never holds:</b> a secret. A channel's address and password are references in the <c>delivery:</c> settings, resolved at the moment of the send; the message holds the words and the attachments (payloads of the
/// <c>payloads</c> module, owned by the message row) and a try's detail is scrubbed of every secret the channel resolved before it is written.
/// </para>
/// <para>
/// <b>Retention.</b> A settled message (<c>sent</c> or <c>dead</c>) and its attempts go after the state default of 30 days (<see cref="HistoryDays"/>), and the payloads that belonged to it with them (the retention job's orphan rule);
/// a message that is still <c>pending</c> or <c>sending</c> is current work and is never removed by age.
/// </para>
/// <para>Scope: one copy per install. Migrations are appended, never edited.</para>
/// </summary>
public static class DeliveryModule
{
    public const string Name = "delivery";

    /// <summary>How long a settled message and its attempts are kept (the state default; an administrator can change it with the retention settings).</summary>
    public const double HistoryDays = 30;

    /// <summary>The statuses a message has, as stored: <c>pending</c> (waiting for a sender, now or at <c>next_attempt_at</c>), <c>sending</c> (a sender holds the lease), <c>sent</c>, <c>dead</c> (parked: a permanent failure or the attempts ran out; a person can send it again).</summary>
    public static IReadOnlyList<string> MessageStatuses { get; } = ["pending", "sending", "sent", "dead"];

    private static readonly string[] Migrations =
    [
        """
        -- The outbox. channel is the name of a channel of the install's delivery: settings; recipients a JSON list of addresses (a webhook channel ignores them); body_text and body_html are the two bodies (either may be null, never both);
        -- link is the address the message points at; attachments a JSON list of {payload, name, media_type, size}, the payloads owned by this row (owner_module delivery). source says what made the message (test, subscription:<id>, alert:<kind>)
        -- and owner_person who it is for or who asked. status is pending, sending, sent or dead; attempts counts the tries made; next_attempt_at is when a sender may take it; lease_owner and lease_expires_at say who holds it and until when
        -- (a sender that stops renewing loses it). last_outcome and last_detail are the latest try's outcome (sent, temporary, permanent) and the words the channel gave, scrubbed of secrets. settled_at is set when it is sent or dead.
        CREATE TABLE {table:message} (
            id               {{uuid}} PRIMARY KEY,
            channel          {{key}} NOT NULL,
            recipients       {{json}} NULL,
            subject          {{text}} NOT NULL,
            body_text        {{text}} NULL,
            body_html        {{text}} NULL,
            link             {{text}} NULL,
            attachments      {{json}} NULL,
            source           {{key}} NULL,
            owner_person     {{key}} NULL,
            status           {{key}} NOT NULL,
            attempts         {{int}} NOT NULL,
            next_attempt_at  {{time}} NOT NULL,
            lease_owner      {{key}} NULL,
            lease_expires_at {{time}} NULL,
            last_outcome     {{key}} NULL,
            last_detail      {{text}} NULL,
            created_at       {{time}} NOT NULL,
            updated_at       {{time}} NOT NULL,
            settled_at       {{time}} NULL,
            row_version      {{int}} NOT NULL
        );
        CREATE INDEX {index:message_by_status} ON {table:message}(status, next_attempt_at);
        CREATE INDEX {index:message_by_channel} ON {table:message}(channel, created_at);
        CREATE INDEX {index:message_by_source} ON {table:message}(source, created_at);
        CREATE INDEX {index:message_by_settled} ON {table:message}(settled_at);

        -- One row per try: number counts from 1; sender is who made it (the sender's id); outcome is sent, temporary (will be tried again), permanent (parked) or error (the channel threw: treated as temporary);
        -- detail is the channel's words, scrubbed of secrets, never an address's credentials.
        CREATE TABLE {table:attempt} (
            id          {{uuid}} PRIMARY KEY,
            message_id  {{uuid}} NOT NULL,
            number      {{int}} NOT NULL,
            sender      {{key}} NOT NULL,
            outcome     {{key}} NOT NULL,
            detail      {{text}} NULL,
            started_at  {{time}} NOT NULL,
            finished_at {{time}} NOT NULL
        );
        CREATE INDEX {index:attempt_by_message} ON {table:attempt}(message_id, number);
        """,
    ];

    /// <summary>The module kept once per install.</summary>
    public static StateModule Module { get; } = new(Name, StateScopeKind.Install, Migrations,
        dependsOn: [PayloadStore.Module],
        retention: [new RetentionRule("message", "settled_at", HistoryDays, Where: "status IN ('sent', 'dead')", Children: [new RetentionChild("attempt", "message_id")])],
        payloadOwners: [new PayloadOwner("message", "id", KeyIsUuid: true)]);
}
