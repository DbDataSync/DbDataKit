namespace DbDataKit.State;

/// <summary>
/// The <c>identity</c> module (plan 29 section 3.4): who may use the suite and how they prove it. One copy per install, so one identity home serves every service that shares the
/// database. <b>People</b> are in the first migration: accounts, their credentials, server-side sessions, invitations and the one-time values a sign-in holds between two requests. The
/// identity home's directory, clients, tokens and federation are appended as further migrations (plan 29a Q09, second half).
/// <para>
/// Names differ from the older tables in two ways the engines force or the conventions ask for: a table called <c>user</c> or <c>group</c> is reserved on SQL Server and PostgreSQL, so
/// they are <c>account</c> and <c>user_group</c>; and times, booleans and the lower-case column names follow plan 29 section 3.3. Ids stay opaque text (the home's subject ids are shared by
/// every console and carried in tokens), generated as 32 hex digits of a UUID v7 (plan 29a decision D15).
/// </para>
/// </summary>
public static class IdentityModule
{
    public const string Name = "identity";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- Who may use this, and how they prove it. In the state store rather than the config repo: accounts are runtime state, and an access-control list in a git history
            -- the UI diffs on screen would publish it to everyone who can read the repo.
            --
            -- id is opaque, never a login name, which people change. email_key is the normalised address of an account whose address a source vouches for (an administrator typed it
            -- at the invitation, the domain gave it as the UPN, the identity home's verified id token carried it): what a local account, a Windows account and a person who signs in
            -- through the home are matched on, and unique. Null for an address nobody checked. role is Admin or Viewer.
            CREATE TABLE {table:account} (
                id           {{key}} PRIMARY KEY,
                display_name {{text}} NOT NULL,
                email        {{text}} NULL,
                email_key    {{key}} NULL,
                role         {{key}} NOT NULL,
                enabled      {{bool}} NOT NULL,
                created_at   {{time}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:account_by_email_key} ON {table:account}(email_key) WHERE email_key IS NOT NULL;

            -- A credential per method, all pointing at one account: signing in is "find the credential, take its account", and adding a passkey to an account that signs in with
            -- Windows is inserting a row. subject is a Windows SID or a passkey's credential id; secret is a passkey's public key, null for Windows.
            CREATE TABLE {table:credential} (
                id           {{key}} PRIMARY KEY,
                account_id   {{key}} NOT NULL REFERENCES {table:account}(id),
                method       {{key}} NOT NULL,
                subject      {{key}} NOT NULL,
                secret       {{text}} NULL,
                label        {{text}} NULL,
                created_at   {{time}} NOT NULL,
                last_used_at {{time}} NULL
            );
            CREATE UNIQUE INDEX {index:credential_by_subject} ON {table:credential}(method, subject);
            CREATE INDEX {index:credential_by_account} ON {table:credential}(account_id);

            -- Server-side, so signing somebody out, or disabling them, takes effect on their next request rather than whenever a token would have expired.
            CREATE TABLE {table:session} (
                id           {{key}} PRIMARY KEY,
                account_id   {{key}} NOT NULL REFERENCES {table:account}(id),
                created_at   {{time}} NOT NULL,
                expires_at   {{time}} NOT NULL
            );
            CREATE INDEX {index:session_by_account} ON {table:session}(account_id);

            -- A one-time capability to create an account, or to add a credential to one. The code is never stored, only a hash of it: it is a bearer credential that arrives over
            -- chat or email and is worth what a password is worth. created_by is null for the bootstrap invite: nobody made it. email is the address the administrator invited this
            -- person at; it becomes the new account's verified address, and what the person types when redeeming is not used.
            CREATE TABLE {table:invite} (
                id           {{key}} PRIMARY KEY,
                code_hash    {{key}} NOT NULL,
                role         {{key}} NOT NULL,
                account_id   {{key}} NULL,
                created_by   {{key}} NULL,
                email        {{text}} NULL,
                created_at   {{time}} NOT NULL,
                expires_at   {{time}} NOT NULL,
                redeemed_at  {{time}} NULL,
                redeemed_by  {{key}} NULL
            );
            CREATE UNIQUE INDEX {index:invite_by_code} ON {table:invite}(code_hash);

            -- What a sign-in or a call holds between two requests and may use once: a passkey ceremony's challenge, an upstream sign-in's verifier and nonce, a management token's id
            -- that has been used. In the state rather than one process's memory so every instance sharing it can finish what another started and refuses what another accepted.
            -- key_hash is the SHA-256 of the purpose and the key the browser or caller holds, so the table names nothing a caller could present.
            CREATE TABLE {table:one_time_value} (
                key_hash    {{key}} PRIMARY KEY,
                purpose     {{key}} NOT NULL,
                value       {{text}} NULL,
                expires_at  {{time}} NOT NULL
            );
            CREATE INDEX {index:one_time_value_by_expiry} ON {table:one_time_value}(expires_at);
            """,
        ],
        retention:
        [
            // a session or an invitation that ran out long ago is history; one still valid is current state
            new RetentionRule("session", "expires_at"),
            new RetentionRule("invite", "expires_at"),
            new RetentionRule("one_time_value", "expires_at"),
        ]);
}

/// <summary>What the sign-in code needs of a store of accounts: the older <see cref="UserStore"/> and the module's <see cref="IdentityUserStore"/> are both one, so <see cref="AccountLinker"/> and <see cref="SessionStore"/> work with either.</summary>
public interface IUserAccounts
{
    UserRecord CreateUser(string displayName, string? email, UserRole role, bool emailVerified = false);

    UserRecord? FindByEmail(string? email);

    UserRecord SetVerifiedEmail(string userId, string email);

    UserRecord Rekey(string oldId, string newId);

    void AddCredential(string userId, string method, string subject, string? secret = null, string? label = null);

    UserRecord? FindByCredential(string method, string subject);

    UserRecord? Get(string id);

    IReadOnlyList<UserCredentialRecord> CredentialsOf(string userId);

    UserRecord UpsertFederated(string id, string displayName, string? email, UserRole role, bool emailVerified = false);

    void SetRole(string userId, UserRole role);
}
