namespace DbDataKit.State;

/// <summary>
/// The <c>identity</c> module (plan 29 section 3.4): who may use the suite and how they prove it. One copy per install, so one identity home serves every service that shares the
/// database. <b>People</b> are in the first migration: accounts, their credentials, server-side sessions, invitations and the one-time values a sign-in holds between two requests. The
/// identity home follows as appended migrations: its directory (groups, role assignments, external group mappings), its OpenID provider (clients, codes, signing keys, refresh
/// tokens) and access tokens with service accounts.
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
            """
            -- The identity home's directory: groups, who is in them, who holds which roles where, and which external groups give which roles.
            CREATE TABLE {table:user_group} (
                id          {{key}} PRIMARY KEY,
                name        {{key}} NOT NULL,
                created_at  {{time}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:user_group_by_name} ON {table:user_group}(name);

            -- slot is a hash of the pair: SQL Server limits an index key to 900 bytes, which two bounded text columns already pass, so uniqueness is one short column's.
            CREATE TABLE {table:group_member} (
                slot        {{key}} PRIMARY KEY,
                group_id    {{key}} NOT NULL REFERENCES {table:user_group}(id),
                account_id  {{key}} NOT NULL REFERENCES {table:account}(id)
            );
            CREATE INDEX {index:group_member_by_account} ON {table:group_member}(account_id);

            -- Roles are assigned once, here, to an account or to a group, and are scoped: a server (target is its instance id), a Build project on a server (target the server,
            -- project the project) or an app (target is the app). roles is a comma-separated list of role names. A server's own ceiling for the console still applies on top.
            -- slot is a hash of kind, subject, scope, target and project: one assignment per place.
            CREATE TABLE {table:role_assignment} (
                id            {{key}} PRIMARY KEY,
                slot          {{key}} NOT NULL,
                subject_kind  {{key}} NOT NULL,
                subject_id    {{key}} NOT NULL,
                scope         {{key}} NOT NULL,
                target        {{key}} NOT NULL,
                project       {{key}} NOT NULL,
                roles         {{text}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:role_assignment_by_slot} ON {table:role_assignment}(slot);
            CREATE INDEX {index:role_assignment_by_subject} ON {table:role_assignment}(subject_id);

            -- An external group maps to roles on a server, a Build project of a server or an app, as role_assignment does for a home group. source is entra or windows.
            -- external_group is the value as written (for display); external_key is what it is compared on (trimmed, Unicode form C, lower case). slot is a hash of source, key,
            -- scope, target and project: one mapping per place.
            CREATE TABLE {table:group_mapping} (
                id              {{key}} PRIMARY KEY,
                slot            {{key}} NOT NULL,
                source          {{key}} NOT NULL,
                external_group  {{text}} NOT NULL,
                external_key    {{key}} NOT NULL,
                scope           {{key}} NOT NULL,
                target          {{key}} NOT NULL,
                project         {{key}} NOT NULL,
                roles           {{text}} NOT NULL,
                created_at      {{time}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:group_mapping_by_slot} ON {table:group_mapping}(slot);
            CREATE INDEX {index:group_mapping_by_group} ON {table:group_mapping}(source, external_key);

            -- The external groups a person was in at their last sign-in through that source, replaced whole at each one. A mapping applies to a person only through a row here.
            -- slot is a hash of account, source and key.
            CREATE TABLE {table:external_membership} (
                slot         {{key}} PRIMARY KEY,
                account_id   {{key}} NOT NULL REFERENCES {table:account}(id),
                source       {{key}} NOT NULL,
                external_key {{key}} NOT NULL,
                seen_at      {{time}} NOT NULL
            );
            CREATE INDEX {index:external_membership_by_account} ON {table:external_membership}(account_id);
            CREATE INDEX {index:external_membership_by_group} ON {table:external_membership}(source, external_key);
            """,
            """
            -- An application that signs people in through the home: a console, an app host, an agent. A public client (a browser app) has no secret and proves itself with PKCE alone.
            -- redirect_uris and post_logout_redirect_uris are one per line, compared exactly (the second is where end-session may send a person back to; a client without a row has none).
            CREATE TABLE {table:oidc_client} (
                client_id     {{key}} PRIMARY KEY,
                name          {{text}} NOT NULL,
                secret_hash   {{text}} NULL,
                redirect_uris {{text}} NOT NULL,
                created_at    {{time}} NOT NULL
            );

            CREATE TABLE {table:oidc_client_logout} (
                client_id                {{key}} PRIMARY KEY,
                post_logout_redirect_uris {{text}} NOT NULL
            );

            -- One sign-in step: the code is returned once, only its hash is kept, and it works once.
            CREATE TABLE {table:authorization_code} (
                code_hash      {{key}} PRIMARY KEY,
                client_id      {{key}} NOT NULL,
                account_id     {{key}} NOT NULL,
                redirect_uri   {{text}} NOT NULL,
                code_challenge {{key}} NOT NULL,
                nonce          {{text}} NULL,
                scope          {{text}} NOT NULL,
                expires_at     {{time}} NOT NULL,
                used_at        {{time}} NULL
            );

            -- The keys that sign tokens: the private half protected by the host, the public half published at the JWKS address. A retired key stops signing and is dropped from the JWKS.
            CREATE TABLE {table:signing_key} (
                kid         {{key}} PRIMARY KEY,
                private_key {{text}} NOT NULL,
                created_at  {{time}} NOT NULL,
                retired_at  {{time}} NULL
            );

            -- A refresh token is returned once, only its hash is kept, and it works once: using it makes the next one in the same family. A token used a second time means it was
            -- copied, so the whole family is revoked. expires_at is the family's, fixed when the person signed in: rotation does not extend it.
            CREATE TABLE {table:refresh_token} (
                token_hash  {{key}} PRIMARY KEY,
                family_id   {{key}} NOT NULL,
                client_id   {{key}} NOT NULL,
                account_id  {{key}} NOT NULL,
                scope       {{text}} NOT NULL,
                expires_at  {{time}} NOT NULL,
                used_at     {{time}} NULL,
                revoked_at  {{time}} NULL
            );
            CREATE INDEX {index:refresh_token_by_account} ON {table:refresh_token}(account_id);
            CREATE INDEX {index:refresh_token_by_family} ON {table:refresh_token}(family_id);
            """,
            """
            -- A token is ddk_<id>_<secret>: the id finds the row, only a hash of the secret is kept. expires_at is null only for a service account's token that never expires.
            -- Revoked tokens stay, listed as revoked.
            CREATE TABLE {table:access_token} (
                id           {{key}} PRIMARY KEY,
                account_id   {{key}} NOT NULL REFERENCES {table:account}(id),
                name         {{text}} NOT NULL,
                secret_hash  {{key}} NOT NULL,
                created_at   {{time}} NOT NULL,
                created_by   {{key}} NOT NULL,
                expires_at   {{time}} NULL,
                revoked_at   {{time}} NULL,
                last_used_at {{time}} NULL
            );
            CREATE INDEX {index:access_token_by_account} ON {table:access_token}(account_id);

            -- An account for unattended work: an account with no credential, signing in only with its tokens. name is the user name its tokens go with.
            CREATE TABLE {table:service_account} (
                account_id   {{key}} PRIMARY KEY REFERENCES {table:account}(id),
                name         {{key}} NOT NULL,
                description  {{text}} NULL,
                created_by   {{key}} NOT NULL,
                created_at   {{time}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:service_account_by_name} ON {table:service_account}(name);
            """,
            """
            -- A client that is an app instance (plan 13a U19): the home issues it access tokens whose audience is the instance (app:<instance>) and whose scope is the approved manifest's,
            -- narrowed by what the person may do. A client with no row here is not an app; its tokens are the home's own, as before.
            CREATE TABLE {table:oidc_client_app} (
                client_id {{key}} PRIMARY KEY,
                instance  {{key}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:oidc_client_app_by_instance} ON {table:oidc_client_app}(instance);
            """,
            """
            -- When a session was last used, for the idle timeout (plan 13a U22). Null on a session made before this column: its sign-in is its last use.
            ALTER TABLE {table:session} {{addcolumn}} seen_at {{time}} NULL;
            """,
            """
            -- Directory attributes (plan 22a GP04, D4): an external group (source entra or windows, as in group_mapping) gives its members an attribute with a value, so that a person in two groups of one
            -- attribute holds two values (region=east, region=west). A person holds an attribute only through external_membership, as with roles. attribute is a lower case name; attr_value is the
            -- value as written and is compared as written. slot is a hash of source, group key, attribute and value: one mapping per place. The tokens carry the result as the claim attrs.
            CREATE TABLE {table:attribute_mapping} (
                id              {{key}} PRIMARY KEY,
                slot            {{key}} NOT NULL,
                source          {{key}} NOT NULL,
                external_group  {{text}} NOT NULL,
                external_key    {{key}} NOT NULL,
                attribute       {{key}} NOT NULL,
                attr_value      {{text}} NOT NULL,
                created_at      {{time}} NOT NULL
            );
            CREATE UNIQUE INDEX {index:attribute_mapping_by_slot} ON {table:attribute_mapping}(slot);
            CREATE INDEX {index:attribute_mapping_by_group} ON {table:attribute_mapping}(source, external_key);
            """,
        ],
        // version 7 only adds a table that the code of version 6 never reads, and the claim it feeds is additive: an instance of the previous version still works with it
        compatibleWithPrevious: 1,
        retention:
        [
            // a session or an invitation that ran out long ago is history; one still valid is current state
            new RetentionRule("session", "expires_at"),
            new RetentionRule("invite", "expires_at"),
            new RetentionRule("one_time_value", "expires_at"),
            new RetentionRule("authorization_code", "expires_at"),
            new RetentionRule("refresh_token", "expires_at"),
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

    void SetEnabled(string userId, bool enabled);

    IReadOnlyList<UserRecord> List();

    bool Any();

    bool RemoveCredential(string userId, string credentialId);
}

/// <summary>What sign-in code needs of server-side sessions; <see cref="SessionStore"/> and <see cref="IdentitySessionStore"/> are both one.</summary>
public interface ISessionStore
{
    SessionRecord Create(string userId);

    UserRecord? Resolve(string sessionId, IUserAccounts users);

    void Delete(string sessionId);

    void DeleteAllFor(string userId);
}

/// <summary>What sign-in code needs of invitations; <see cref="InviteStore"/> and <see cref="IdentityInviteStore"/> are both one.</summary>
public interface IInviteStore
{
    MintedInvite Create(UserRole role, string? forUserId = null, string? createdByUserId = null, TimeSpan? lifetime = null, string? email = null);

    InviteRecord? Find(string code);

    bool Redeem(string inviteId, string userId);

    void DeleteBootstrapInvites();

    IReadOnlyList<InviteRecord> ListOutstanding();
}

/// <summary>What sign-in code needs of one-time values; <see cref="OneTimeValues"/> and <see cref="IdentityOneTimeValues"/> are both one.</summary>
public interface IOneTimeValues
{
    void Hold(string purpose, string key, string value, DateTimeOffset expires);

    string? Take(string purpose, string key);

    bool TryClaim(string purpose, string key, DateTimeOffset forgetAfter);
}
