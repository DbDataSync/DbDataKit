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

    // ---- external groups (plan 13a U18) ----

    /// <summary>
    /// Maps an external group to roles on a server (or, with <paramref name="project"/>, on one Build project of it): everyone whose last sign-in through <paramref name="source"/> (<see cref="ExternalSources"/>) carried the group holds
    /// the roles, in addition to what they hold otherwise. Mapping again replaces what the group was given there; <see cref="ServerRole.None"/> removes the mapping. Applies to people at once (their roles are read each time a token is made).
    /// </summary>
    public void MapExternalGroup(string source, string group, string server, ServerRole roles, string? project = null) =>
        WriteMapping(source, group, project is null ? "server" : "project", server, project, roles);

    /// <summary>Maps an external group to roles in an app.</summary>
    public void MapExternalGroupToApp(string source, string group, string app, ServerRole roles) => WriteMapping(source, group, "app", app, null, roles);

    private void WriteMapping(string source, string group, string scope, string target, string? project, ServerRole roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var key = ExternalSources.Key(source, group);
        var projectKey = scope == "project" ? project ?? "" : "";
        var slot = SlotOf(source, key, scope, target, projectKey);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var delete = database.Command(connection, "DELETE FROM GroupMappings WHERE Slot = $slot;"))
            {
                delete.Bind(database, "slot", slot);
                delete.ExecuteNonQuery();
            }
            if (roles == ServerRole.None) return;
            using var cmd = database.Command(connection, """
                INSERT INTO GroupMappings (Id, Slot, Source, ExternalGroup, ExternalKey, Scope, Target, Project, Roles, CreatedAtUtc)
                VALUES ($id, $slot, $source, $group, $key, $scope, $target, $project, $roles, $at);
                """);
            cmd.Bind(database, "id", Guid.NewGuid().ToString("N"));
            cmd.Bind(database, "slot", slot);
            cmd.Bind(database, "source", source);
            cmd.Bind(database, "group", group.Trim());
            cmd.Bind(database, "key", key);
            cmd.Bind(database, "scope", scope);
            cmd.Bind(database, "target", target);
            cmd.Bind(database, "project", projectKey);
            cmd.Bind(database, "roles", string.Join(',', ServerRoles.Names(roles)));
            cmd.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Removes a mapping by its id; the people it gave roles lose them at once.</summary>
    public void RemoveExternalMapping(string id) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "DELETE FROM GroupMappings WHERE Id = $id;");
        cmd.Bind(database, "id", id);
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<ExternalGroupMapping> ExternalMappings() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT Id, Source, ExternalGroup, Scope, Target, Project, Roles FROM GroupMappings ORDER BY Source, ExternalKey, Scope, Target, Project;");
        using var reader = cmd.ExecuteReader();
        var list = new List<ExternalGroupMapping>();
        while (reader.Read())
        {
            ServerRoles.TryParse(reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries), out var roles);
            list.Add(new ExternalGroupMapping(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5) is { Length: > 0 } p ? p : null, roles));
        }
        return (IReadOnlyList<ExternalGroupMapping>)list;
    });

    /// <summary>
    /// Records the external groups a person's sign-in through <paramref name="source"/> carried, replacing what the last one recorded for that source (so a group they left stops counting). Values that cannot be mapped (blank, or longer
    /// than <see cref="ExternalSources.MaxLength"/>) are ignored.
    /// </summary>
    public void SetExternalGroups(string userId, string source, IEnumerable<string> groups)
    {
        ExternalSources.Require(source);
        var keys = groups.Select(g => ExternalSources.TryKey(g)).Where(k => k is not null).Select(k => k!).Distinct(StringComparer.Ordinal).ToList();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = database.Command(connection, transaction, "DELETE FROM ExternalMemberships WHERE UserId = $u AND Source = $source;"))
            {
                delete.Bind(database, "u", userId);
                delete.Bind(database, "source", source);
                delete.ExecuteNonQuery();
            }
            var at = DateTimeOffset.UtcNow.ToString("O");
            foreach (var key in keys)
            {
                using var insert = database.Command(connection, transaction, "INSERT INTO ExternalMemberships (Slot, UserId, Source, ExternalKey, SeenAtUtc) VALUES ($slot, $u, $source, $key, $at);");
                insert.Bind(database, "slot", SlotOf(userId, source, key));
                insert.Bind(database, "u", userId);
                insert.Bind(database, "source", source);
                insert.Bind(database, "key", key);
                insert.Bind(database, "at", at);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        });
    }

    /// <summary>The external groups (normalised, as compared) the person was last seen in, per source.</summary>
    public IReadOnlyList<(string Source, string Group)> ExternalGroupsOf(string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT Source, ExternalKey FROM ExternalMemberships WHERE UserId = $u ORDER BY Source, ExternalKey;");
        cmd.Bind(database, "u", userId);
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1)));
        return (IReadOnlyList<(string, string)>)list;
    });

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

    /// <summary>What the user holds on <paramref name="server"/>: its own assignments, its groups' and those its external groups map to (<see cref="MapExternalGroup"/>), added together.</summary>
    public ServerRolesOf RolesOn(string userId, string server) => Held(userId, "server", server, "project", server);

    /// <summary>What the user holds in <paramref name="app"/>.</summary>
    public ServerRole RolesIn(string userId, string app) => Held(userId, "app", app, null, null).Roles;

    /// <summary>Every server and app the user holds anything on, with the roles (the claim an id token carries).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllRoles(string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, """
            SELECT Scope, Target, Project, Roles FROM RoleAssignments
            WHERE (SubjectKind = 'user' AND SubjectId = $u) OR (SubjectKind = 'group' AND SubjectId IN (SELECT GroupId FROM GroupMembers WHERE UserId = $u))
            UNION ALL
            SELECT m.Scope, m.Target, m.Project, m.Roles FROM GroupMappings m
            WHERE EXISTS (SELECT 1 FROM ExternalMemberships e WHERE e.UserId = $u AND e.Source = m.Source AND e.ExternalKey = m.ExternalKey);
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
              AND ((SubjectKind = 'user' AND SubjectId = $u) OR (SubjectKind = 'group' AND SubjectId IN (SELECT GroupId FROM GroupMembers WHERE UserId = $u)))
            UNION ALL
            SELECT m.Scope, m.Project, m.Roles FROM GroupMappings m
            WHERE m.Target = $target AND (m.Scope = $scope OR m.Scope = $projectScope)
              AND EXISTS (SELECT 1 FROM ExternalMemberships e WHERE e.UserId = $u AND e.Source = m.Source AND e.ExternalKey = m.ExternalKey);
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

/// <summary>One mapping of an external group to roles on a server, a Build project (<paramref name="Project"/>) or an app.</summary>
/// <param name="Id">The mapping's id (to remove it).</param>
/// <param name="Source">One of <see cref="ExternalSources"/>.</param>
/// <param name="Group">The group value as it was written.</param>
/// <param name="Scope">server, project or app.</param>
/// <param name="Target">The server's instance id, or the app.</param>
/// <param name="Project">The Build project when <paramref name="Scope"/> is project; otherwise null.</param>
/// <param name="Roles">The roles the group gives there.</param>
public sealed record ExternalGroupMapping(string Id, string Source, string Group, string Scope, string Target, string? Project, ServerRole Roles);

/// <summary>Where a person's external groups come from. Each is a way of signing in whose groups the home does not manage.</summary>
public static class ExternalSources
{
    /// <summary>Microsoft Entra ID (or another OpenID Connect provider): the values of the id token's <c>groups</c> claim (Entra sends group object ids).</summary>
    public const string Entra = "entra";

    /// <summary>A Windows (Negotiate) sign-in: group SIDs or <c>DOMAIN\group</c> names. The mapping and the records are here; no host feeds them yet (plan 13a U18, not done).</summary>
    public const string Windows = "windows";

    /// <summary>The longest group value that can be mapped (an index key on every engine).</summary>
    public const int MaxLength = 300;

    public static void Require(string source)
    {
        if (source is not (Entra or Windows)) throw new ArgumentException($"'{source}' is not a source of external groups (use '{Entra}' or '{Windows}').", nameof(source));
    }

    /// <summary>What two spellings of the same group are compared on: trimmed, Unicode form C, invariant lower case (a group id's case and a Windows name's are not significant). Null for a value that cannot be a group.</summary>
    public static string? TryKey(string? group)
    {
        var key = group?.Trim().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        return string.IsNullOrEmpty(key) || key.Length > MaxLength ? null : key;
    }

    public static string Key(string source, string group)
    {
        Require(source);
        return TryKey(group) ?? throw new ArgumentException($"A group is a non-blank value of at most {MaxLength} characters.", nameof(group));
    }

    /// <summary>The credential method that names an account made from an external sign-in (a Windows one is the console's own, so one person stays one account).</summary>
    public static string CredentialMethod(string source) { Require(source); return source == Windows ? CredentialMethods.Windows : "External:" + source; }
}
