namespace DbDataKit.State;

/// <summary>
/// The <c>agents</c> module (plan 27a AG06, plan 27 section 3): what the agent runtime of <c>DbDataKit.Agents</c> keeps in a state database, for the console's back-end agents and, later, a group's front-end ones.
/// <para>
/// <b>Two kinds of row.</b> The <b>audit rows</b> say what happened and who did it, and are kept like the audit (no retention rule until an administrator sets one): <c>audit_event</c> (tool calls with their arguments and outcome, approvals,
/// denials, refusals, the context given to a turn), <c>turn_record</c> (a model turn: the model, tokens, cost, the record of what the turn was given by item and version, never the text) and <c>approval</c> (a step that waits for a person,
/// and how it was answered). The <b>conversation text</b> is history: <c>turn_text</c> keeps what was said for <see cref="TextDays"/> days (the state default) and the shared retention job then removes it, while the audit rows stay. A row names the person, the agent
/// and the agent's version, so "who let it do that" has an answer after the words are gone. <c>conversation</c> is the list the screens open (one row each, with its status).
/// </para>
/// <para>
/// <b>Scope.</b> One copy per install (<see cref="Module"/>): the console hosts the back-end agents. A group's front-end agents (AG25) keep <see cref="GroupModule"/>, the same migrations.
/// Migrations are appended, never edited; the third adds the memories of AG19.
/// </para>
/// </summary>
public static class AgentsModule
{
    public const string Name = "agents";

    /// <summary>How long what was said is kept (the state default of 30 days, plan 27a AG06); the audit rows have no age limit.</summary>
    public const double TextDays = 30;

    /// <summary>The statuses a conversation has, as stored.</summary>
    public static IReadOnlyList<string> ConversationStatuses { get; } = ["open", "completed", "limit-reached", "denied", "refused", "failed"];

    /// <summary>The statuses an approval has, as stored.</summary>
    public static IReadOnlyList<string> ApprovalStatuses { get; } = ["pending", "approved", "denied", "expired"];

    private static readonly string[] Migrations =
    [
        """
        -- A conversation: one row, opened by a person with an agent (at the version it started with, AG21). channel is the channel whose bindings it ran against; workspace names the draft the agent works in (null when none);
        -- origin says where it came from: console (the chat screen) or mcp (an outside agent through ui mcp). status is open or how it ended; stop_reason says why in a sentence.
        CREATE TABLE {table:conversation} (
            id            {{key}} PRIMARY KEY,
            agent         {{key}} NOT NULL,
            agent_version {{key}} NOT NULL,
            person        {{key}} NOT NULL,
            person_name   {{key}} NOT NULL,
            origin        {{key}} NOT NULL,
            channel       {{key}} NULL,
            workspace     {{key}} NULL,
            title         {{key}} NULL,
            status        {{key}} NOT NULL,
            stop_reason   {{text}} NULL,
            started_at    {{time}} NOT NULL,
            updated_at    {{time}} NOT NULL
        );
        CREATE INDEX {index:conversation_by_person} ON {table:conversation}(person, started_at);
        CREATE INDEX {index:conversation_by_agent} ON {table:conversation}(agent, started_at);

        -- What was said (history, retention: 30 days). role is user, assistant or tool. A tool result is stored as the model was given it (wrapped as data). Nothing here is an audit row: when the text goes the audit rows stay.
        CREATE TABLE {table:turn_text} (
            conversation_id {{key}} NOT NULL,
            seq             {{int}} NOT NULL,
            role            {{key}} NOT NULL,
            content         {{text}} NOT NULL,
            created_at      {{time}} NOT NULL,
            PRIMARY KEY (conversation_id, seq)
        );
        CREATE INDEX {index:turn_text_by_time} ON {table:turn_text}(created_at);

        -- One model turn (audit, no retention rule): who and which agent at which version, the model that answered, the tokens the provider reported, the cost (tokens times the price on the connection) and
        -- what the turn was given, as one line of layer/id@version in order (context_record) with its fingerprint: enough to explain an answer and replay it, with no text.
        CREATE TABLE {table:turn_record} (
            id              {{uuid}} PRIMARY KEY,
            conversation_id {{key}} NOT NULL,
            turn            {{int}} NOT NULL,
            person          {{key}} NOT NULL,
            agent           {{key}} NOT NULL,
            agent_version   {{key}} NOT NULL,
            model           {{key}} NULL,
            input_tokens    {{int}} NOT NULL,
            output_tokens   {{int}} NOT NULL,
            cost            {{decimal}} NOT NULL,
            context_print   {{key}} NULL,
            context_record  {{text}} NULL,
            recorded_at     {{time}} NOT NULL
        );
        CREATE INDEX {index:turn_record_by_conversation} ON {table:turn_record}(conversation_id, turn);
        CREATE INDEX {index:turn_record_by_person} ON {table:turn_record}(person, recorded_at);
        CREATE INDEX {index:turn_record_by_agent} ON {table:turn_record}(agent, recorded_at);

        -- Something that happened (audit, no retention rule): kind is the runtime's event kind (tool-called, approval-asked, approval-approved, approval-denied, tool-refused, tool-failed, context-given, skill-loaded, limit-reached, stopped).
        -- tool and arguments say what was run (arguments are the JSON the tool was called with, never a secret: the tools take none); detail is a sentence. conversation_id is null for an outside agent's session before it has one.
        CREATE TABLE {table:audit_event} (
            id              {{uuid}} PRIMARY KEY,
            conversation_id {{key}} NULL,
            turn            {{int}} NULL,
            kind            {{key}} NOT NULL,
            person          {{key}} NOT NULL,
            agent           {{key}} NOT NULL,
            agent_version   {{key}} NOT NULL,
            origin          {{key}} NOT NULL,
            tool            {{key}} NULL,
            arguments       {{text}} NULL,
            detail          {{text}} NULL,
            occurred_at     {{time}} NOT NULL
        );
        CREATE INDEX {index:audit_event_by_conversation} ON {table:audit_event}(conversation_id, occurred_at);
        CREATE INDEX {index:audit_event_by_person} ON {table:audit_event}(person, occurred_at);
        CREATE INDEX {index:audit_event_by_agent} ON {table:audit_event}(agent, occurred_at);

        -- A step that waits for a person (audit, no retention rule). preview is the line the person reads (the command, its arguments and its traits), traits the names of the traits. status is pending, approved, denied or expired;
        -- answered_by is the person who answered (their own act: only that person's answer counts), note their reason. A step nobody answered in time is expired, which is a no.
        CREATE TABLE {table:approval} (
            id              {{uuid}} PRIMARY KEY,
            conversation_id {{key}} NULL,
            person          {{key}} NOT NULL,
            agent           {{key}} NOT NULL,
            agent_version   {{key}} NOT NULL,
            origin          {{key}} NOT NULL,
            tool            {{key}} NOT NULL,
            preview         {{text}} NOT NULL,
            arguments       {{text}} NULL,
            traits          {{key}} NOT NULL,
            status          {{key}} NOT NULL,
            requested_at    {{time}} NOT NULL,
            answered_at     {{time}} NULL,
            answered_by     {{key}} NULL,
            note            {{text}} NULL
        );
        CREATE INDEX {index:approval_by_status} ON {table:approval}(status, requested_at);
        CREATE INDEX {index:approval_by_person} ON {table:approval}(person, requested_at);
        """,
        """
        -- An outside agent's way in (plan 27a AG29): a token a person makes in the console and gives to `ui mcp --report-to`, which signs in as that person with it to post what the agent asked and did and to ask the person in the inbox.
        -- Only a hash of the secret is kept; the secret is shown once. A token reaches the outside endpoints only (the console checks the path), and it is the person's: revoking or expiring it ends the agent's way in at once.
        CREATE TABLE {table:agent_token} (
            id           {{key}} PRIMARY KEY,
            person       {{key}} NOT NULL,
            name         {{key}} NOT NULL,
            secret_hash  {{key}} NOT NULL,
            created_at   {{time}} NOT NULL,
            expires_at   {{time}} NULL,
            revoked_at   {{time}} NULL,
            last_used_at {{time}} NULL
        );
        CREATE INDEX {index:agent_token_by_person} ON {table:agent_token}(person, created_at);
        """,
        """
        -- What agents remember (plan 27a AG19): kind is person (what the agent keeps of one person: only that person sees, edits and deletes it) or work (what it keeps of a workspace or work item, in work_ref: whoever opens the work sees it).
        -- person is who the memory is about (person) or who wrote it (work); agent is the agent that wrote it. A memory never holds a secret (the runtime refuses one) and is deleted when it is forgotten: the audit rows say it was written and forgotten, not what it said.
        CREATE TABLE {table:memory} (
            id         {{key}} PRIMARY KEY,
            kind       {{key}} NOT NULL,
            person     {{key}} NOT NULL,
            work_ref   {{key}} NULL,
            agent      {{key}} NULL,
            content    {{text}} NOT NULL,
            created_at {{time}} NOT NULL,
            updated_at {{time}} NOT NULL
        );
        CREATE INDEX {index:memory_by_person} ON {table:memory}(person, kind);
        CREATE INDEX {index:memory_by_work} ON {table:memory}(work_ref);
        """,
    ];

    private static StateModule Make(StateScopeKind scope) => new(Name, scope, Migrations, retention: [new RetentionRule("turn_text", "created_at", TextDays)]);

    /// <summary>The module kept once per install: the console's back-end agents.</summary>
    public static StateModule Module { get; } = Make(StateScopeKind.Install);

    /// <summary>The same module kept per group, for the serving layer's front-end agents.</summary>
    public static StateModule GroupModule { get; } = Make(StateScopeKind.Group);
}
