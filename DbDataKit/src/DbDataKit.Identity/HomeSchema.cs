using DbDataKit.State;

namespace DbDataKit.Identity;

/// <summary>
/// What the identity home keeps beside the users, credentials and sessions of <see cref="IdentitySchema"/>: groups, who holds which roles where, the applications that sign in through it and the short-lived
/// state of a sign-in. <see cref="Schema"/> is the whole of it, for a host that has no other tables.
/// </summary>
public static class HomeSchema
{
    public const string Directory =
        """
        CREATE TABLE Groups (
            Id           {{key}} PRIMARY KEY,
            Name         {{key}} NOT NULL,
            CreatedAtUtc {{text}} NOT NULL
        );
        CREATE UNIQUE INDEX UX_Groups_Name ON Groups(Name);

        -- Slot is a hash of the pair: SQL Server limits an index key to 900 bytes, which two bounded text columns already pass, so uniqueness is one short column's.
        CREATE TABLE GroupMembers (
            Slot    {{key}} PRIMARY KEY,
            GroupId {{key}} NOT NULL REFERENCES Groups(Id),
            UserId  {{key}} NOT NULL REFERENCES Users(Id)
        );
        CREATE INDEX IX_GroupMembers_User ON GroupMembers(UserId);

        -- Roles are assigned once, here, to a user or to a group, and are scoped: a server (Target is its instance id), a Build project on a server (Target the server, Project the project)
        -- or an app (Target is the app). Roles is a comma-separated list of role names. A server's own ceiling for the console still applies on top.
        CREATE TABLE RoleAssignments (
            Id          {{key}} PRIMARY KEY,
            Slot        {{key}} NOT NULL,   -- a hash of kind, subject, scope, target and project: one assignment per place
            SubjectKind {{key}} NOT NULL,   -- user | group
            SubjectId   {{key}} NOT NULL,
            Scope       {{key}} NOT NULL,   -- server | project | app
            Target      {{key}} NOT NULL,
            Project     {{key}} NOT NULL,   -- empty unless Scope is project
            Roles       {{text}} NOT NULL
        );
        CREATE UNIQUE INDEX UX_RoleAssignments ON RoleAssignments(Slot);
        CREATE INDEX IX_RoleAssignments_Subject ON RoleAssignments(SubjectId);
        """;

    public const string Provider =
        """
        -- An application that signs people in through the home: a console, an app host, an agent. A public client (a browser app) has no secret and proves itself with PKCE alone.
        CREATE TABLE OidcClients (
            ClientId     {{key}} PRIMARY KEY,
            Name         {{text}} NOT NULL,
            SecretHash   {{text}} NULL,
            RedirectUris {{text}} NOT NULL,   -- one per line, compared exactly
            CreatedAtUtc {{text}} NOT NULL
        );

        -- One sign-in step: the code is returned once, only its hash is kept, and it works once.
        CREATE TABLE AuthorizationCodes (
            CodeHash      {{key}} PRIMARY KEY,
            ClientId      {{key}} NOT NULL,
            UserId        {{key}} NOT NULL,
            RedirectUri   {{text}} NOT NULL,
            CodeChallenge {{key}} NOT NULL,
            Nonce         {{text}} NULL,
            Scope         {{text}} NOT NULL,
            ExpiresAtUtc  {{text}} NOT NULL,
            UsedAtUtc     {{text}} NULL
        );

        -- The keys that sign tokens: the private half protected by the host, the public half published at the JWKS address. A retired key stops signing and is dropped from the JWKS.
        CREATE TABLE SigningKeys (
            Kid          {{key}} PRIMARY KEY,
            PrivateKey   {{text}} NOT NULL,
            CreatedAtUtc {{text}} NOT NULL,
            RetiredAtUtc {{text}} NULL
        );
        """;

    /// <summary>Refresh tokens and the addresses a person may be sent back to after signing out (plan 13a U17). Appended after <see cref="Provider"/>, which has shipped.</summary>
    public const string Sessions =
        """
        -- A refresh token is returned once, only its hash is kept, and it works once: using it makes the next one in the same family. A token used a second time means it was copied, so the whole family is revoked.
        CREATE TABLE RefreshTokens (
            TokenHash    {{key}} PRIMARY KEY,
            FamilyId     {{key}} NOT NULL,
            ClientId     {{key}} NOT NULL,
            UserId       {{key}} NOT NULL,
            Scope        {{text}} NOT NULL,
            ExpiresAtUtc {{text}} NOT NULL,   -- the family's, fixed when the person signed in: rotation does not extend it
            UsedAtUtc    {{text}} NULL,
            RevokedAtUtc {{text}} NULL
        );
        CREATE INDEX IX_RefreshTokens_User ON RefreshTokens(UserId);
        CREATE INDEX IX_RefreshTokens_Family ON RefreshTokens(FamilyId);

        -- Where the end-session endpoint may send a person back to (OpenID Connect RP-Initiated Logout), one per line, compared exactly. A client without a row has none.
        CREATE TABLE OidcClientLogout (
            ClientId             {{key}} PRIMARY KEY,
            PostLogoutRedirectUris {{text}} NOT NULL
        );
        """;

    /// <summary>Users, credentials, sessions and invites, then the home's own tables.</summary>
    public static StateSchema Schema { get; } = new([IdentitySchema.Accounts, IdentitySchema.Invites, Directory, Provider, Sessions]);
}
