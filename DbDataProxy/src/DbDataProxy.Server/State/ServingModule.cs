using DbDataKit.State;

namespace DbDataProxy.Server.State;

/// <summary>
/// The Proxy's own module (plan 29a Q16), kept per group: what the instances of a serving group share. <c>client_session</c> is a client's session (valid on any instance): the hash of its
/// token (the token itself is never kept), who it is (a document, with the database credentials inside it encrypted with the group's key), and its expiry and last use.
/// <c>serving_instance</c> is the instances running against the state, each refreshing its row while it runs (a second one is refused on SQLite). The three <c>saved_query*</c> tables are the
/// queries people keep on a model: the current version, every version it has had, and who else may call it. Everything else the Proxy keeps is a shared module: identity, notifications,
/// definitions, app storage, trust and audit.
/// </summary>
public static class ServingModule
{
    public const string Name = "serving";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:client_session} (
                token_hash    {{key}} PRIMARY KEY,
                principal     {{text}} NOT NULL,
                expires_at    {{time}} NOT NULL,
                last_used_at  {{time}} NOT NULL
            );
            CREATE INDEX {index:client_session_by_expiry} ON {table:client_session}(expires_at);

            CREATE TABLE {table:serving_instance} (
                instance_key  {{key}} PRIMARY KEY,
                host          {{key}} NOT NULL,
                process_id    {{int}} NOT NULL,
                started_at    {{time}} NOT NULL,
                heartbeat_at  {{time}} NOT NULL
            );

            -- A saved query a person keeps on a model: the current version. One row per owner, model and name. query_sql or request_json is set, not both;
            -- parameters_json is the parameter definitions (for, type, many, default, required, empty).
            CREATE TABLE {table:saved_query} (
                {{identity:id}},
                owner            {{key}} NOT NULL,
                model            {{key}} NOT NULL,
                name             {{key}} NOT NULL,
                version          {{int}} NOT NULL,
                description      {{text}} NULL,
                query_sql        {{text}} NULL,
                request_json     {{text}} NULL,
                parameters_json  {{text}} NOT NULL,
                created_at       {{time}} NOT NULL,
                updated_at       {{time}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:saved_query_by_name} ON {table:saved_query}(owner, model, name);

            -- Every version a saved query has had, so an edit can be undone. The current one is also here.
            CREATE TABLE {table:saved_query_version} (
                query_id         {{int}} NOT NULL,
                version          {{int}} NOT NULL,
                description      {{text}} NULL,
                query_sql        {{text}} NULL,
                request_json     {{text}} NULL,
                parameters_json  {{text}} NOT NULL,
                saved_by         {{key}} NOT NULL,
                saved_at         {{time}} NOT NULL,
                PRIMARY KEY (query_id, version)
            );

            -- Who else can call it: 'user:alice', 'group:sales' (a role of the caller) or '*' (anyone who can query the model).
            CREATE TABLE {table:saved_query_share} (
                query_id  {{int}} NOT NULL,
                grantee   {{key}} NOT NULL,
                PRIMARY KEY (query_id, grantee)
            );
            """,
            // plan 20 O08: the store module's change requests, a write to a published entity model that waits for a person, kept where any instance can list, approve or reject it
            """
            CREATE TABLE {table:store_change_request} (
                id               {{key}} PRIMARY KEY,
                model            {{key}} NOT NULL,
                state            {{key}} NOT NULL,
                proposed_by      {{key}} NOT NULL,
                proposed_by_name {{text}} NULL,
                by_agent         {{bool}} NOT NULL,
                proposed_at      {{time}} NOT NULL,
                description      {{text}} NULL,
                changes          {{text}} NOT NULL,
                decided_by       {{key}} NULL,
                decided_at       {{time}} NULL,
                note             {{text}} NULL
            );
            CREATE INDEX {index:store_change_request_by_model} ON {table:store_change_request}(model, state);
            """,
        ]);
}
