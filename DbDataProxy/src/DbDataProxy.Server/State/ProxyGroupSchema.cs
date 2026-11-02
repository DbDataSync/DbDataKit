namespace DbDataProxy.Server.State;

/// <summary>
/// What instances of a group share in the state (plan 20 O02): the client sessions (valid on any instance; a pass-through session's database credentials encrypted with the group's key), the management audit, the
/// check value that says which key the group's sessions are encrypted with, and the instances that are running against the state (a second one is refused on SQLite). Appended after the identity home's tables.
/// </summary>
public static class ProxyGroupSchema
{
    public const string Tables =
        """
        -- A client session: the hash of its token (the token itself is never kept), who it is (JSON; database credentials inside it encrypted with the group's key), and its expiry and last use.
        CREATE TABLE ClientSessions (
            TokenHash     {{key}} PRIMARY KEY,
            Principal     {{text}} NOT NULL,
            ExpiresAtUtc  {{key}} NOT NULL,
            LastUsedAtUtc {{key}} NOT NULL
        );
        CREATE INDEX IX_ClientSessions_Expires ON ClientSessions(ExpiresAtUtc);

        -- One change made through the management API (who, which console, what, the outcome; never a value), in the order it was made.
        CREATE TABLE ManagementAudit (
            {{identity:Id}},
            AtUtc {{key}} NOT NULL,
            Entry {{text}} NOT NULL
        );

        -- Which key a group's encrypted values are protected with: a known value encrypted with it, so an instance given another key refuses to start instead of failing every session.
        CREATE TABLE GroupKeys (
            Name       {{key}} PRIMARY KEY,
            CheckValue {{text}} NOT NULL
        );

        -- The instances running against this state, each refreshing its row while it runs. On SQLite there may be one.
        CREATE TABLE ServingInstances (
            InstanceKey    {{key}} PRIMARY KEY,
            Host           {{key}} NOT NULL,
            ProcessId      {{int}} NOT NULL,
            StartedAtUtc   {{key}} NOT NULL,
            HeartbeatAtUtc {{key}} NOT NULL
        );
        """;
}
