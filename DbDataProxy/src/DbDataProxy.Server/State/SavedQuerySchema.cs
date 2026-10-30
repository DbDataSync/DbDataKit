namespace DbDataProxy.Server.State;

/// <summary>The tables of user-owned saved queries (plan 19, J27): the current version of each, every version it has had, and who it is shared with.</summary>
public static class SavedQuerySchema
{
    public const string Tables =
        """
        -- A saved query a person keeps on a model: the current version. One row per owner, model and name.
        -- Sql or RequestJson is set, not both; ParametersJson is the parameter definitions (for, type, many, default, required, empty).
        CREATE TABLE SavedQueries (
            {{identity:Id}},
            Owner          {{key}} NOT NULL,
            Model          {{key}} NOT NULL,
            Name           {{key}} NOT NULL,
            Version        {{int}} NOT NULL,
            Description    {{text}} NULL,
            Sql            {{text}} NULL,
            RequestJson    {{text}} NULL,
            ParametersJson {{text}} NOT NULL,
            CreatedAtUtc   {{text}} NOT NULL,
            UpdatedAtUtc   {{text}} NOT NULL
        );
        CREATE UNIQUE INDEX UX_SavedQueries_Name ON SavedQueries(Owner, Model, Name);

        -- Every version a saved query has had, so an edit can be undone. The current one is also here.
        CREATE TABLE SavedQueryVersions (
            QueryId        {{int}} NOT NULL,
            Version        {{int}} NOT NULL,
            Description    {{text}} NULL,
            Sql            {{text}} NULL,
            RequestJson    {{text}} NULL,
            ParametersJson {{text}} NOT NULL,
            SavedBy        {{key}} NOT NULL,
            SavedAtUtc     {{text}} NOT NULL,
            PRIMARY KEY (QueryId, Version)
        );

        -- Who else can call it: 'user:alice', 'group:sales' (a role of the caller) or '*' (anyone who can query the model).
        CREATE TABLE SavedQueryShares (
            QueryId  {{int}} NOT NULL,
            Grantee  {{key}} NOT NULL,
            PRIMARY KEY (QueryId, Grantee)
        );
        """;
}
