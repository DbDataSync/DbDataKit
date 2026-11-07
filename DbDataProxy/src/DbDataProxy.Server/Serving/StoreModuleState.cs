using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.State;

namespace DbDataProxy.Server.Serving;

/// <summary>A write to a published entity model that waits for a person (plan 20 O08). <see cref="Changes"/> is the body as proposed, applied unchanged when approved.</summary>
public sealed record GroupChangeRequest(
    string Id, string Model, string State, string ProposedBy, string? ProposedByName, bool ByAgent, DateTimeOffset ProposedAt, string? Description, JsonArray Changes,
    string? DecidedBy = null, DateTimeOffset? DecidedAt = null, string? Note = null)
{
    public const string Pending = "pending", Approved = "approved", Rejected = "rejected";

    /// <summary>The same shape a DbDataStore server answers with, so a client reads either.</summary>
    public JsonObject ToJson(bool withChanges = true)
    {
        var node = new JsonObject
        {
            ["id"] = Id, ["model"] = Model, ["instance"] = "default", ["state"] = State, ["proposed_by"] = ProposedBy, ["proposed_by_name"] = ProposedByName, ["by_agent"] = ByAgent,
            ["proposed_at"] = ProposedAt.ToString("O"), ["description"] = Description, ["change_count"] = Changes.Count,
            ["decided_by"] = DecidedBy, ["decided_at"] = DecidedAt?.ToString("O"), ["note"] = Note,
        };
        if (withChanges) node["changes"] = JsonNode.Parse(Changes.ToJsonString());
        return node;
    }
}

/// <summary>The store module's change requests, in the group's state (the <c>serving</c> module's <c>store_change_request</c>): any instance proposes, lists and decides them.</summary>
public sealed class GroupChangeRequests(StateDatabase database)
{
    private const string Module = ServingModule.Name;
    private const string Columns = "id, model, state, proposed_by, proposed_by_name, by_agent, proposed_at, description, changes, decided_by, decided_at, note";

    public GroupChangeRequest Propose(string model, StoreCaller by, string? description, JsonArray changes)
    {
        var request = new GroupChangeRequest(Guid.NewGuid().ToString("N"), model, GroupChangeRequest.Pending, by.UserId, by.Name, by.IsAgent, database.Time.GetUtcNow(), description, changes);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, """
                INSERT INTO {table:store_change_request} (id, model, state, proposed_by, proposed_by_name, by_agent, proposed_at, description, changes)
                VALUES ($id, $model, $state, $by, $name, $agent, $at, $description, $changes);
                """);
            cmd.Bind(database, "id", request.Id).Bind(database, "model", model).Bind(database, "state", request.State).Bind(database, "by", by.UserId)
                .Bind(database, "name", by.Name, System.Data.DbType.String).Bind(database, "agent", by.IsAgent).Bind(database, "at", request.ProposedAt)
                .Bind(database, "description", description, System.Data.DbType.String).Bind(database, "changes", changes.ToJsonString());
            cmd.ExecuteNonQuery();
        });
        return request;
    }

    public IReadOnlyList<GroupChangeRequest> List(string model, string? state = null) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, state is null
            ? $"SELECT {Columns} FROM {{table:store_change_request}} WHERE model = $model ORDER BY proposed_at, id;"
            : $"SELECT {Columns} FROM {{table:store_change_request}} WHERE model = $model AND state = $state ORDER BY proposed_at, id;");
        cmd.Bind(database, "model", model);
        if (state is not null) cmd.Bind(database, "state", state);
        using var reader = cmd.ExecuteReader();
        var found = new List<GroupChangeRequest>();
        while (reader.Read()) found.Add(Read(reader));
        return (IReadOnlyList<GroupChangeRequest>)found;
    });

    public GroupChangeRequest? Find(string model, string id) => List(model).FirstOrDefault(r => r.Id == id);

    /// <summary>Records the decision on a request still pending; false when another instance decided it first.</summary>
    public bool Decide(GroupChangeRequest request, string state, StoreCaller by, string? note) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "UPDATE {table:store_change_request} SET state = $state, decided_by = $by, decided_at = $at, note = $note WHERE id = $id AND state = $pending;");
        cmd.Bind(database, "state", state).Bind(database, "by", by.UserId).Bind(database, "at", database.Time.GetUtcNow()).Bind(database, "note", note, System.Data.DbType.String).Bind(database, "id", request.Id)
            .Bind(database, "pending", GroupChangeRequest.Pending);
        return cmd.ExecuteNonQuery() == 1;
    });

    private static GroupChangeRequest Read(System.Data.Common.DbDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.Bool(5), r.Time(6),
            r.IsDBNull(7) ? null : r.GetString(7), (JsonArray)JsonNode.Parse(r.GetString(8))!, r.IsDBNull(9) ? null : r.GetString(9), r.NullableTime(10), r.IsDBNull(11) ? null : r.GetString(11));
}

/// <summary>Who called the store module: the identity home's user, whether as an agent (a service account), and the roles the home gives them on this group.</summary>
public sealed record StoreCaller(string UserId, string? Name, bool IsAgent, ServerRole Roles)
{
    public bool Holds(ServerRole role) => (ServerRoles.Close(Roles) & role) == role;
}

/// <summary>
/// The callers of the store module (plan 20 O08, owner decision 2026-11-04): people, apps and agents with a token of the group's identity home, which shares the state. A personal access token (a person's or a service account's,
/// <c>Authorization: Bearer ddk_...</c> or as the password of Basic), or an access token the home issued (<c>Bearer</c>, ES256, checked against the home's signing keys when this instance holds their key). What they may do
/// is what the home gives them on this group's server id: viewer reads, author writes, and the approval of a change request as the model says. A service account is an agent.
/// </summary>
public sealed class StoreCallers(StateDatabase state, string serverId, OpenIdProvider? provider)
{
    private readonly UserStore _users = new(state);

    public StoreCaller? Authenticate(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        var tokens = new AccessTokens(state, _users);
        UserRecord? user = null;
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var token = authorization[7..].Trim();
            if (token.StartsWith("ddk_", StringComparison.Ordinal)) user = tokens.VerifyBearer(token);
            else if (provider?.Verify(token, "at+jwt") is { } claims && claims.GetValueOrDefault("aud") as string == provider.Issuer && claims.GetValueOrDefault("sub") is string sub)
                user = _users.Get(sub) is { Enabled: true } found ? found : null;
        }
        else if (authorization.StartsWith("Basic ", StringComparison.Ordinal))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())); }
            catch (FormatException) { return null; }
            var colon = decoded.IndexOf(':');
            if (colon > 0) user = tokens.Verify(decoded[..colon], decoded[(colon + 1)..]);
        }
        if (user is null) return null;
        var roles = new HomeDirectory(state).RolesOn(user.Id, serverId).Roles;
        return new StoreCaller(user.Id, user.DisplayName, tokens.ServiceAccountOf(user.Id) is not null, roles);
    }
}
