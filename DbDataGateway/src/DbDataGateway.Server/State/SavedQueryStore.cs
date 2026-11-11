using System.Data.Common;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;

namespace DbDataGateway.Server.State;

/// <summary>A saved query as stored: who owns it, which version is current, and the definition.</summary>
public sealed record SavedQueryRecord(long Id, string Owner, string Model, string Name, int Version, SavedQueryDef Query, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? OwnerName = null);

/// <summary>One version of a saved query's history.</summary>
public sealed record SavedQueryVersion(int Version, SavedQueryDef Query, string SavedBy, DateTimeOffset SavedAt);

/// <summary>The saved query was changed since the version the caller read, or already exists when it was to be created.</summary>
public sealed class SavedQueryConflictException(string message, int? currentVersion) : Exception(message)
{
    public int? CurrentVersion { get; } = currentVersion;
}

/// <summary>
/// User-owned saved queries (plan 19, J27) in the Gateway's state database, on whatever engine that is (P11). A person saves a query on a model, shares it with a user, a group or everyone, edits it (every version is kept, so an edit can be
/// undone) and deletes it. The store is also what the model source consults (<see cref="IUserSavedQueries"/>) when a statement selects from a name the model does not own: the caller's own first, then one shared with them. Sharing a query
/// shares no data: it runs as whoever calls it. The surface that lets people do this (the saving API) is the apps' and comes with them; this is the storage and the lookup.
/// </summary>
public sealed partial class SavedQueryStore(StateDatabase database, TimeProvider? clock = null) : IUserSavedQueries
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Saves a new version of the owner's query, or creates it. <paramref name="expectedVersion"/> is the version the caller read (null to create; a query that exists is then a conflict).</summary>
    public SavedQueryRecord Save(string owner, string model, string name, SavedQueryDef query, string savedBy, int? expectedVersion = null, string? ownerName = null)
    {
        if (!NamePattern().IsMatch(name)) throw new ArgumentException("A saved query's name is lowercase letters, digits and underscores, starting with a letter.", nameof(name));
        if ((query.Sql is null) == (query.Request is null)) throw new ArgumentException("A saved query has a sql text or a request, and not both.", nameof(query));
        var now = _clock.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = Read(connection, transaction, owner, model, name);
            if (current is null && expectedVersion is not null) throw new SavedQueryConflictException($"There is no saved query '{name}' of {owner} on {model} to change.", null);
            if (current is not null && current.Version != expectedVersion) throw new SavedQueryConflictException($"'{name}' is at version {current.Version}; read it again and save on that.", current.Version);

            long id;
            var version = (current?.Version ?? 0) + 1;
            if (current is null)
            {
                using var insert = database.Command("serving", connection, transaction, """
                    INSERT INTO {table:saved_query} (owner, model, name, version, description, query_sql, request_json, parameters_json, created_at, updated_at, owner_name)
                    VALUES ($owner, $model, $name, $version, $description, $sql, $request, $parameters, $now, $now, $ownerName);
                    """);
                BindDefinition(insert, owner, model, name, version, query).Bind(database, "now", now).Bind(database, "ownerName", ownerName);
                insert.ExecuteNonQuery();
                using var lookup = database.Command("serving", connection, transaction, "SELECT id FROM {table:saved_query} WHERE owner = $owner AND model = $model AND name = $name;");
                lookup.Bind(database, "owner", owner).Bind(database, "model", model).Bind(database, "name", name);
                id = Convert.ToInt64(lookup.ExecuteScalar());
            }
            else
            {
                id = current.Id;
                using var update = database.Command("serving", connection, transaction, """
                    UPDATE {table:saved_query} SET version = $version, description = $description, query_sql = $sql, request_json = $request, parameters_json = $parameters, updated_at = $now, owner_name = COALESCE($ownerName, owner_name)
                    WHERE id = $id;
                    """);
                BindDefinition(update, owner, model, name, version, query).Bind(database, "now", now).Bind(database, "id", id).Bind(database, "ownerName", ownerName, System.Data.DbType.String);
                update.ExecuteNonQuery();
            }
            using var history = database.Command("serving", connection, transaction, """
                INSERT INTO {table:saved_query_version} (query_id, version, description, query_sql, request_json, parameters_json, saved_by, saved_at)
                VALUES ($id, $version, $description, $sql, $request, $parameters, $savedBy, $now);
                """);
            BindDefinition(history, owner, model, name, version, query).Bind(database, "id", id).Bind(database, "savedBy", savedBy).Bind(database, "now", now);
            history.ExecuteNonQuery();
            transaction.Commit();
            return Read(connection, null, owner, model, name)!;
        });
    }

    public SavedQueryRecord? Get(string owner, string model, string name) =>
        database.Retry(() => { using var connection = database.OpenConnection(); return Read(connection, null, owner, model, name); });

    /// <summary>The owner's saved queries, on one model or all.</summary>
    public IReadOnlyList<SavedQueryRecord> ListOwned(string owner, string? model = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command("serving", connection, $"{Select} WHERE owner = $owner{(model is null ? "" : " AND model = $model")} ORDER BY model, name;");
            command.Bind(database, "owner", owner);
            if (model is not null) command.Bind(database, "model", model);
            return ReadAll(command);
        });

    /// <summary>Every version the query has had, oldest first.</summary>
    public IReadOnlyList<SavedQueryVersion> History(string owner, string model, string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command("serving", connection, """
                SELECT v.version, v.description, v.query_sql, v.request_json, v.parameters_json, v.saved_by, v.saved_at
                FROM {table:saved_query_version} v JOIN {table:saved_query} q ON q.id = v.query_id
                WHERE q.owner = $owner AND q.model = $model AND q.name = $name ORDER BY v.version;
                """);
            command.Bind(database, "owner", owner).Bind(database, "model", model).Bind(database, "name", name);
            using var reader = command.ExecuteReader();
            var versions = new List<SavedQueryVersion>();
            while (reader.Read())
                versions.Add(new SavedQueryVersion(reader.Int32(0), Definition(reader, 1), reader.GetString(5), reader.Time(6)));
            return (IReadOnlyList<SavedQueryVersion>)versions;
        });

    /// <summary>Makes an earlier version current again, as a new version (so the history only grows and an undo can be undone).</summary>
    public SavedQueryRecord Restore(string owner, string model, string name, int version, string savedBy)
    {
        var current = Get(owner, model, name) ?? throw new SavedQueryConflictException($"There is no saved query '{name}' of {owner} on {model}.", null);
        var earlier = History(owner, model, name).FirstOrDefault(v => v.Version == version) ?? throw new SavedQueryConflictException($"'{name}' has no version {version}.", current.Version);
        return Save(owner, model, name, earlier.Query, savedBy, current.Version);
    }

    public bool Delete(string owner, string model, string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = Read(connection, transaction, owner, model, name);
            if (current is null) return false;
            foreach (var table in new[] { "saved_query_share", "saved_query_version" })
            {
                using var children = database.Command("serving", connection, transaction, $"DELETE FROM {{table:{table}}} WHERE query_id = $id;");
                children.Bind(database, "id", current.Id).ExecuteNonQuery();
            }
            using var query = database.Command("serving", connection, transaction, "DELETE FROM {table:saved_query} WHERE id = $id;");
            query.Bind(database, "id", current.Id).ExecuteNonQuery();
            transaction.Commit();
            return true;
        });

    /// <summary>Shares the query with a grantee: <c>user:alice</c>, <c>group:sales</c> or <c>*</c>.</summary>
    public void Share(string owner, string model, string name, string grantee)
    {
        if (!GranteePattern().IsMatch(grantee)) throw new ArgumentException("A grantee is user:<name>, group:<name> or *.", nameof(grantee));
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var current = Read(connection, null, owner, model, name) ?? throw new SavedQueryConflictException($"There is no saved query '{name}' of {owner} on {model}.", null);
            using var existing = database.Command("serving", connection, "SELECT COUNT(*) FROM {table:saved_query_share} WHERE query_id = $id AND grantee = $grantee;");
            existing.Bind(database, "id", current.Id).Bind(database, "grantee", grantee);
            if (Convert.ToInt32(existing.ExecuteScalar()) > 0) return;
            using var insert = database.Command("serving", connection, "INSERT INTO {table:saved_query_share} (query_id, grantee) VALUES ($id, $grantee);");
            insert.Bind(database, "id", current.Id).Bind(database, "grantee", grantee).ExecuteNonQuery();
        });
    }

    public void Unshare(string owner, string model, string name, string grantee) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command("serving", connection, """
                DELETE FROM {table:saved_query_share} WHERE grantee = $grantee AND query_id IN (SELECT id FROM {table:saved_query} WHERE owner = $owner AND model = $model AND name = $name);
                """);
            command.Bind(database, "grantee", grantee).Bind(database, "owner", owner).Bind(database, "model", model).Bind(database, "name", name).ExecuteNonQuery();
        });

    public IReadOnlyList<string> Shares(string owner, string model, string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command("serving", connection, """
                SELECT s.grantee FROM {table:saved_query_share} s JOIN {table:saved_query} q ON q.id = s.query_id
                WHERE q.owner = $owner AND q.model = $model AND q.name = $name ORDER BY s.grantee;
                """);
            command.Bind(database, "owner", owner).Bind(database, "model", model).Bind(database, "name", name);
            using var reader = command.ExecuteReader();
            var grantees = new List<string>();
            while (reader.Read()) grantees.Add(reader.GetString(0));
            return (IReadOnlyList<string>)grantees;
        });

    // ---------- what the model source asks ----------

    public SavedQueryDef? Find(ServingCaller caller, string model, string name) =>
        Visible(caller, model).Where(v => string.Equals(v.Record.Name, name, StringComparison.Ordinal)).Select(v => v.Record.Query).FirstOrDefault();

    public IReadOnlyList<UserSavedQuery> List(ServingCaller caller, string model) =>
        [.. Visible(caller, model).Select(v => new UserSavedQuery(v.Record.Name, v.Record.Owner, v.Record.Query, v.Record.OwnerName))];

    /// <summary>The caller's own queries on the model, then those shared with them, a name once (their own first, then the oldest share).</summary>
    private List<(SavedQueryRecord Record, bool Own)> Visible(ServingCaller caller, string model) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var key = SavedQueryOwner.Of(caller);
            var grantees = new List<string> { "*", "user:" + key };
            grantees.AddRange(caller.Roles.Select(role => "group:" + role.ToLowerInvariant()));
            var names = string.Join(", ", grantees.Select((_, i) => "$g" + i));
            using var command = database.Command("serving", connection, $$"""
                {{Select}}
                WHERE model = $model AND (owner = $owner OR id IN (SELECT query_id FROM {table:saved_query_share} WHERE LOWER(grantee) IN ({{names}})))
                ORDER BY created_at, id;
                """);
            command.Bind(database, "model", model).Bind(database, "owner", key);
            for (int i = 0; i < grantees.Count; i++) command.Bind(database, "g" + i, grantees[i]);
            var found = ReadAll(command);
            var ordered = found.Select(r => (Record: r, Own: string.Equals(r.Owner, key, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.Own).ThenBy(x => x.Record.CreatedAt).ToList();
            return ordered.GroupBy(x => x.Record.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(x => x.Record.Name, StringComparer.Ordinal).ToList();
        });

    // ---------- rows ----------

    private const string Select = "SELECT id, owner, model, name, version, description, query_sql, request_json, parameters_json, created_at, updated_at, owner_name FROM {table:saved_query}";

    private SavedQueryRecord? Read(DbConnection connection, DbTransaction? transaction, string owner, string model, string name)
    {
        using var command = database.Command("serving", connection, transaction, $"{Select} WHERE owner = $owner AND model = $model AND name = $name;");
        command.Bind(database, "owner", owner).Bind(database, "model", model).Bind(database, "name", name);
        return ReadAll(command).FirstOrDefault();
    }

    private static List<SavedQueryRecord> ReadAll(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var all = new List<SavedQueryRecord>();
        while (reader.Read())
            all.Add(new SavedQueryRecord(reader.Int64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.Int32(4), Definition(reader, 5),
                reader.Time(9), reader.Time(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        return all;
    }

    /// <summary>The definition from the columns Description, Sql, RequestJson, ParametersJson starting at <paramref name="first"/>.</summary>
    private static SavedQueryDef Definition(DbDataReader reader, int first) => new()
    {
        Description = reader.IsDBNull(first) ? null : reader.GetString(first),
        Sql = reader.IsDBNull(first + 1) ? null : reader.GetString(first + 1),
        Request = reader.IsDBNull(first + 2) ? null : JsonNode.Parse(reader.GetString(first + 2)) as JsonObject,
        Parameters = JsonNode.Parse(reader.GetString(first + 3)) as JsonObject ?? new JsonObject(),
    };

    private DbCommand BindDefinition(DbCommand command, string owner, string model, string name, int version, SavedQueryDef query) =>
        command.Bind(database, "owner", owner).Bind(database, "model", model).Bind(database, "name", name).Bind(database, "version", version)
            .Bind(database, "description", query.Description).Bind(database, "sql", query.Sql).Bind(database, "request", query.Request?.ToJsonString())
            .Bind(database, "parameters", query.Parameters.ToJsonString());

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(\*|user:[^\s]+|group:[^\s]+)$")]
    private static partial Regex GranteePattern();
}

/// <summary>
/// Who owns a person's saved queries (plan 14a D35): the email or UPN the identity home knows them by, trimmed and lower case, never the display name (two people may share one). A caller with no email (a pass-through or
/// Windows sign-in the home does not know) is keyed by their name in the same form, which is as exact as that name is.
/// </summary>
public static class SavedQueryOwner
{
    public static string Of(ServingCaller caller) => SemanticCallers.KeyOf(caller);

    public static string Normalize(string key) => SemanticCallers.Normalize(key);

    /// <summary>The display name beside the key, when it is not the key itself.</summary>
    public static string? DisplayName(ServingCaller caller) => string.Equals(Of(caller), Normalize(caller.Name), StringComparison.Ordinal) ? null : caller.Name;
}
