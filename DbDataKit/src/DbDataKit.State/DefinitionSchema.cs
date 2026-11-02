namespace DbDataKit.State;

/// <summary>The tables of published definitions (plan 20, O03): every version of a definition, which version each channel serves, and the log of what was published or switched, which is also how an instance learns something changed.</summary>
public static class DefinitionSchema
{
    public const string Tables =
        """
        -- Every version a definition has had. A definition is a kind ('app', 'model', 'navigation') and a name; its versions count up from 1 and are never changed or removed.
        -- Hash is the SHA-256 of Content, so a version can be told from another without reading it. DependsOnJson lists what this version was published against, as 'kind:name@version'.
        CREATE TABLE DefinitionVersions (
            Kind          {{key}} NOT NULL,
            Name          {{key}} NOT NULL,
            Version       {{int}} NOT NULL,
            Hash          {{key}} NOT NULL,
            Content       {{text}} NOT NULL,
            DependsOnJson {{text}} NOT NULL,
            PublishedBy   {{key}} NOT NULL,
            PublishedAtUtc {{key}} NOT NULL,
            Note          {{text}} NULL,
            PRIMARY KEY (Kind, Name, Version)
        );

        -- The version a channel serves, and the one it served before (what a rollback returns to). One row per definition and channel; switching is one update.
        CREATE TABLE DefinitionServing (
            Kind            {{key}} NOT NULL,
            Name            {{key}} NOT NULL,
            Channel         {{key}} NOT NULL,
            Version         {{int}} NOT NULL,
            PreviousVersion {{int}} NULL,
            SwitchedAtUtc   {{key}} NOT NULL,
            SwitchedBy      {{key}} NOT NULL,
            PRIMARY KEY (Kind, Name, Channel)
        );

        -- Append-only: one row for each publish, switch and rollback. Instances poll it with WHERE Id > $last, and it is the audit of who changed what a channel serves.
        CREATE TABLE DefinitionEvents (
            {{identity:Id}},
            Action     {{key}} NOT NULL,
            Kind       {{key}} NOT NULL,
            Name       {{key}} NOT NULL,
            Channel    {{key}} NOT NULL,
            Version    {{int}} NOT NULL,
            ActorId    {{key}} NOT NULL,
            AtUtc      {{key}} NOT NULL
        );
        """;
}
