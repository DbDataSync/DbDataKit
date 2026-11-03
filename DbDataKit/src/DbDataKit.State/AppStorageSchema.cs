namespace DbDataKit.State;

/// <summary>The table of app storage (plan 26, AP09): small JSON values an app keeps through the host, because a sandboxed document has no browser storage.</summary>
public static class AppStorageSchema
{
    public const string Tables =
        """
        -- Scope is 'user' (Owner is the person's id) or 'instance' (Owner is empty). Instance is the app instance, so two instances of one app never share a row.
        -- Size is the value's length in UTF-8 bytes, kept so the instance's total is a sum and not a scan. Version counts up from 1 for a key and is what an optional if_version check compares.
        CREATE TABLE AppStorage (
            Instance       {{key}} NOT NULL,
            Scope          {{key}} NOT NULL,
            Owner          {{key}} NOT NULL,
            StoredKey      {{key}} NOT NULL,
            Value          {{text}} NOT NULL,
            Size           {{int}} NOT NULL,
            Version        {{int}} NOT NULL,
            UpdatedAtUtc   {{key}} NOT NULL,
            PRIMARY KEY (Instance, Scope, Owner, StoredKey)
        );
        """;
}
