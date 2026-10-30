using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

public sealed record GroupRecord(string Id, string Name);

/// <summary>What a user holds on one server: the roles on the server itself, and the roles on each Build project it has.</summary>
public sealed record ServerRolesOf(ServerRole Roles, IReadOnlyDictionary<string, ServerRole> Projects);

/// <summary>Groups and the roles assigned to users and groups. The union of a user's own assignments and those of every group the user is in is what the user holds.</summary>
public sealed class HomeDirectory(StateDatabase database)
{
    public GroupRecord CreateGroup(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var group = new GroupRecord(Guid.NewGuid().ToString("N"), name.Trim());
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO Groups (Id, Name, CreatedAtUtc) VALUES ($id, $name, $at);");
            cmd.Bind(database, "id", group.Id);
            cmd.Bind(database, "name", group.Name);
            cmd.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
        return group;
    }

    public IReadOnlyList<GroupRecord> Groups() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT Id, Name FROM Groups ORDER BY Name;");
        using var reader = cmd.ExecuteReader();
        var list = new List<GroupRecord>();
        while (reader.Read()) list.Add(new GroupRecord(reader.GetString(0), reader.GetString(1)));
        return (IReadOnlyList<GroupRecord>)list;
    });

    public void AddMember(string groupId, string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "INSERT INTO GroupMembers (Slot, GroupId, UserId) VALUES ($slot, $g, $u);");
        cmd.Bind(database, "slot", SlotOf(groupId, userId));
        cmd.Bind(database, "g", groupId);
        cmd.Bind(database, "u", userId);
        cmd.ExecuteNonQuery();
    });

    public void RemoveMember(string groupId, string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "DELETE FROM GroupMembers WHERE GroupId = $g AND UserId = $u;");
        cmd.Bind(database, "g", groupId);
        cmd.Bind(database, "u", userId);
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<GroupRecord> GroupsOf(string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT g.Id, g.Name FROM Groups g JOIN GroupMembers m ON m.GroupId = g.Id WHERE m.UserId = $u ORDER BY g.Name;");
        cmd.Bind(database, "u", userId);
        using var reader = cmd.ExecuteReader();
        var list = new List<GroupRecord>();
        while (reader.Read()) list.Add(new GroupRecord(reader.GetString(0), reader.GetString(1)));
        return (IReadOnlyList<GroupRecord>)list;
    });

    /// <summary>Gives a user or a group roles on a server (or, with <paramref name="project"/>, on one Build project of it). Assigning again replaces what was assigned there.</summary>
    public void Assign(AssignmentSubject subject, string server, ServerRole roles, string? project = null) => Write(subject, project is null ? "server" : "project", server, project, roles);

    /// <summary>Gives a user or a group roles in an app (an app's users hold app roles only).</summary>
    public void AssignApp(AssignmentSubject subject, string app, ServerRole roles) => Write(subject, "app", app, null, roles);

    private void Write(AssignmentSubject subject, string scope, string target, string? project, ServerRole roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var projectKey = scope == "project" ? project ?? "" : "";
        Unassign(subject, scope, target, projectKey);
        if (roles == ServerRole.None) return;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO RoleAssignments (Id, Slot, SubjectKind, SubjectId, Scope, Target, Project, Roles)
                VALUES ($id, $slot, $kind, $subject, $scope, $target, $project, $roles);
                """);
            cmd.Bind(database, "id", Guid.NewGuid().ToString("N"));
            cmd.Bind(database, "slot", SlotOf(subject.Kind, subject.Id, scope, target, projectKey));
            cmd.Bind(database, "kind", subject.Kind);
            cmd.Bind(database, "subject", subject.Id);
            cmd.Bind(database, "scope", scope);
            cmd.Bind(database, "target", target);
            cmd.Bind(database, "project", projectKey);
            cmd.Bind(database, "roles", string.Join(',', ServerRoles.Names(roles)));
            cmd.ExecuteNonQuery();
        });
    }

    private static string SlotOf(params string[] parts) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', parts)))).ToLowerInvariant();

    private void Unassign(AssignmentSubject subject, string scope, string target, string project) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "DELETE FROM RoleAssignments WHERE SubjectKind = $kind AND SubjectId = $subject AND Scope = $scope AND Target = $target AND Project = $project;");
        cmd.Bind(database, "kind", subject.Kind);
        cmd.Bind(database, "subject", subject.Id);
        cmd.Bind(database, "scope", scope);
        cmd.Bind(database, "target", target);
        cmd.Bind(database, "project", project);
        cmd.ExecuteNonQuery();
    });

    /// <summary>What the user holds on <paramref name="server"/>: its own assignments and its groups', added together.</summary>
    public ServerRolesOf RolesOn(string userId, string server) => Held(userId, "server", server, "project", server);

    /// <summary>What the user holds in <paramref name="app"/>.</summary>
    public ServerRole RolesIn(string userId, string app) => Held(userId, "app", app, null, null).Roles;

    /// <summary>Every server and app the user holds anything on, with the roles (the claim an id token carries).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllRoles(string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, """
            SELECT Scope, Target, Project, Roles FROM RoleAssignments
            WHERE (SubjectKind = 'user' AND SubjectId = $u) OR (SubjectKind = 'group' AND SubjectId IN (SELECT GroupId FROM GroupMembers WHERE UserId = $u));
            """);
        cmd.Bind(database, "u", userId);
        using var reader = cmd.ExecuteReader();
        var held = new SortedDictionary<string, ServerRole>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var key = reader.GetString(0) switch { "app" => "app:" + reader.GetString(1), "project" => $"server:{reader.GetString(1)}/{reader.GetString(2)}", _ => "server:" + reader.GetString(1) };
            ServerRoles.TryParse(reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries), out var roles);
            held[key] = held.GetValueOrDefault(key) | roles;
        }
        return (IReadOnlyDictionary<string, IReadOnlyList<string>>)held.ToDictionary(p => p.Key, p => ServerRoles.Names(ServerRoles.Close(p.Value)));
    });

    private ServerRolesOf Held(string userId, string scope, string target, string? projectScope, string? projectTarget) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, """
            SELECT Scope, Project, Roles FROM RoleAssignments
            WHERE Target = $target AND (Scope = $scope OR Scope = $projectScope)
              AND ((SubjectKind = 'user' AND SubjectId = $u) OR (SubjectKind = 'group' AND SubjectId IN (SELECT GroupId FROM GroupMembers WHERE UserId = $u)));
            """);
        cmd.Bind(database, "u", userId);
        cmd.Bind(database, "target", projectTarget ?? target);
        cmd.Bind(database, "scope", scope);
        cmd.Bind(database, "projectScope", projectScope ?? scope);
        using var reader = cmd.ExecuteReader();
        var roles = ServerRole.None;
        var projects = new Dictionary<string, ServerRole>();
        while (reader.Read())
        {
            ServerRoles.TryParse(reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries), out var these);
            if (reader.GetString(0) == "project") projects[reader.GetString(1)] = projects.GetValueOrDefault(reader.GetString(1)) | these;
            else roles |= these;
        }
        return new ServerRolesOf(ServerRoles.Close(roles), projects.ToDictionary(p => p.Key, p => ServerRoles.Close(p.Value)));
    });
}

/// <summary>Who a role is assigned to: a user or a group.</summary>
public readonly record struct AssignmentSubject(string Kind, string Id)
{
    public static AssignmentSubject User(string id) => new("user", id);
    public static AssignmentSubject Group(string id) => new("group", id);
}
