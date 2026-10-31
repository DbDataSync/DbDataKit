namespace DbDataKit.State;

/// <summary>The schema for sign-in: users, their credentials, sessions and invites (plan 13, U04). Written once in the tokens <see cref="StateTemplates"/> renders.</summary>
public static class IdentitySchema
{
    /// <summary>Users, a credential per sign-in method, and server-side sessions.</summary>
    public const string Accounts =
        """
        -- Who may use this, and how they prove it.
        --
        -- In the state store rather than the config repo: users are runtime state, and putting an
        -- access-control list in a git history the UI diffs on screen would publish it to everyone
        -- who can read the repo.
        CREATE TABLE Users (
            Id           {{key}} PRIMARY KEY,   -- opaque; never a login name, which people change
            DisplayName  {{text}} NOT NULL,
            Email        {{text}} NULL,          -- for git attribution, where there is one
            Role         {{text}} NOT NULL,      -- Admin | Viewer
            Enabled      {{int}} NOT NULL,
            CreatedAtUtc {{text}} NOT NULL
        );

        -- A credential per method, all pointing at one user. That shape is what makes "one person,
        -- both methods" cheap: signing in is "find the credential, take its user", and adding a
        -- passkey to an account that already signs in with Windows is inserting a row. Columns named
        -- WindowsSid and PasskeyPublicKey on Users would have made the same requirement a migration.
        CREATE TABLE UserCredentials (
            Id            {{key}} PRIMARY KEY,
            UserId        {{key}} NOT NULL REFERENCES Users(Id),
            Method        {{key}} NOT NULL,      -- Windows | Passkey
            -- Windows: the account SID. Passkey: the credential id. What a sign-in is looked up by.
            Subject       {{key}} NOT NULL,
            Secret        {{text}} NULL,          -- a passkey's *public* key; null for Windows
            Label         {{text}} NULL,
            CreatedAtUtc  {{text}} NOT NULL,
            LastUsedAtUtc {{text}} NULL
        );

        CREATE UNIQUE INDEX UX_UserCredentials_Subject ON UserCredentials(Method, Subject);
        CREATE INDEX IX_UserCredentials_User ON UserCredentials(UserId);

        -- Server-side, so signing somebody out — or disabling them — takes effect on their next
        -- request rather than whenever a token would have expired.
        CREATE TABLE Sessions (
            Id           {{key}} PRIMARY KEY,
            UserId       {{key}} NOT NULL REFERENCES Users(Id),
            CreatedAtUtc {{text}} NOT NULL,
            ExpiresAtUtc {{text}} NOT NULL
        );

        CREATE INDEX IX_Sessions_User ON Sessions(UserId);
        """;

    /// <summary>One-time capabilities to create a user or add a credential to one.</summary>
    public const string Invites =
        """
        -- A one-time capability to create a user, or to add a credential to one.
        --
        -- The code itself is never stored — only a hash of it. It is a bearer credential that arrives
        -- over chat or email and is worth exactly what a password is worth, and a database somebody
        -- can read is a database somebody can sign in from.
        CREATE TABLE Invites (
            Id               {{key}} PRIMARY KEY,
            CodeHash         {{key}} NOT NULL,
            Role             {{text}} NOT NULL,   -- what the invited user becomes
            UserId           {{text}} NULL,       -- set when adding a credential to an existing user
            CreatedByUserId  {{text}} NULL,       -- null for the bootstrap invite: nobody made it
            CreatedAtUtc     {{text}} NOT NULL,
            ExpiresAtUtc     {{text}} NOT NULL,
            RedeemedAtUtc    {{text}} NULL,
            RedeemedByUserId {{text}} NULL
        );

        CREATE UNIQUE INDEX UX_Invites_CodeHash ON Invites(CodeHash);
        """;

    /// <summary>
    /// Appended after <see cref="Accounts"/> and <see cref="Invites"/>, which have shipped (plan 13a U16): the account's key and the address an invitation was made for. A host appends it to its schema list as the next entry.
    /// </summary>
    public const string AccountKeys =
        """
        -- One account per person (plan 13a U16). EmailKey is the normalised address of an account whose address a source vouches for (an administrator typed it at the invitation, the domain
        -- gave it as the UPN, the identity home's verified id token carried it). It is what a local user, a Windows account and a person who signs in through the home are matched on, and it is unique:
        -- a second account cannot hold the same address. Null for an address nobody checked (and for every account made before this column: their addresses were typed by the person redeeming the
        -- invitation, which proves nothing, so none is trusted until an administrator sets it).
        ALTER TABLE Users {{addcolumn}} EmailKey {{key}} NULL;

        CREATE UNIQUE INDEX UX_Users_EmailKey ON Users(EmailKey) WHERE EmailKey IS NOT NULL;

        -- The address the administrator invited this person at. It becomes the new account's verified address; what the person types when redeeming is not used.
        ALTER TABLE Invites {{addcolumn}} Email {{text}} NULL;
        """;
}
