using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>What a host that signs people in through the home (a second console) keeps: the roles the home last said each person holds, so a server token can be made from them while the session lasts.</summary>
public static class FederatedSchema
{
    public const string Roles =
        """
        -- The home's last word on what a person holds, replaced at each sign-in. The roles are the home's: this table is a copy, never an assignment.
        CREATE TABLE FederatedRoles (
            UserId       {{key}} PRIMARY KEY REFERENCES Users(Id),
            Roles        {{text}} NOT NULL,   -- the dbdata_roles claim as JSON
            UpdatedAtUtc {{text}} NOT NULL
        );
        """;
}

/// <summary>
/// People who sign in through the identity home and so have no password or passkey here (plan 13a U14). A sign-in mirrors the person as a user under the home's id (so every audit entry and token names the same user in
/// every console), decides what they are here from their roles on this console's app (<c>app:&lt;appId&gt;</c>: admin or anything else), and keeps the roles they hold on servers.
/// </summary>
public sealed class FederatedUsers(StateDatabase database, UserStore users, string appId)
{
    public string AppKey { get; } = "app:" + appId;

    /// <summary>The user for this sign-in, or null when the home gives the person no role on this console (they may sign in to the home and still not to here).</summary>
    public UserRecord? SignIn(HomeIdentity identity)
    {
        if (!identity.Roles.TryGetValue(AppKey, out var names) || names.Count == 0) return null;
        var role = names.Contains("admin", StringComparer.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.Viewer;
        var user = users.UpsertFederated(identity.UserId, identity.Name ?? identity.UserId, identity.Email, role);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var delete = database.Command(connection, "DELETE FROM FederatedRoles WHERE UserId = $id;"))
            {
                delete.Bind(database, "id", user.Id);
                delete.ExecuteNonQuery();
            }
            using var insert = database.Command(connection, "INSERT INTO FederatedRoles (UserId, Roles, UpdatedAtUtc) VALUES ($id, $roles, $at);");
            insert.Bind(database, "id", user.Id);
            insert.Bind(database, "roles", JsonSerializer.Serialize(identity.Roles));
            insert.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        });
        return user;
    }

    /// <summary>What the home says the user holds on <paramref name="serverId"/> and on each of its Build projects; null when the user does not sign in through the home (their roles are the local ones').</summary>
    public ServerRolesOf? RolesOn(string userId, string serverId)
    {
        var json = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT Roles FROM FederatedRoles WHERE UserId = $id;");
            cmd.Bind(database, "id", userId);
            return cmd.ExecuteScalar() as string;
        });
        if (json is null) return null;
        var held = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? [];
        ServerRole Parse(string key) => held.TryGetValue(key, out var names) && ServerRoles.TryParse(names, out var r) ? ServerRoles.Close(r) : ServerRole.None;
        var prefix = $"server:{serverId}/";
        var projects = held.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(k => k[prefix.Length..], Parse);
        return new ServerRolesOf(Parse($"server:{serverId}"), projects);
    }
}
