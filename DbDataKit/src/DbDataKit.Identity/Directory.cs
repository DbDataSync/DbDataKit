using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

public sealed record GroupRecord(string Id, string Name);

/// <summary>What a user holds on one server: the roles on the server itself, and the roles on each Build project it has.</summary>
public sealed record ServerRolesOf(ServerRole Roles, IReadOnlyDictionary<string, ServerRole> Projects);

/// <summary>Groups and the roles assigned to users and groups. The union of a user's own assignments and those of every group the user is in is what the user holds.</summary>
public sealed class HomeDirectory(StateDatabase database)
{
    private const string Module = IdentityModule.Name;

    public GroupRecord CreateGroup(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var group = new GroupRecord(database.NewId().ToString("N"), name.Trim());
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:user_group} (id, name, created_at) VALUES ($id, $name, $at);");
            cmd.Bind(database, "id", group.Id);
            cmd.Bind(database, "name", group.Name);
            cmd.Bind(database, "at", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
        });
        return group;
    }

    public IReadOnlyList<GroupRecord> Groups() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT id, name FROM {table:user_group} ORDER BY name;");
        using var reader = cmd.ExecuteReader();
        var list = new List<GroupRecord>();
        while (reader.Read()) list.Add(new GroupRecord(reader.GetString(0), reader.GetString(1)));
        return (IReadOnlyList<GroupRecord>)list;
    });

    public void AddMember(string groupId, string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "INSERT INTO {table:group_member} (slot, group_id, account_id) VALUES ($slot, $g, $u);");
        cmd.Bind(database, "slot", SlotOf(groupId, userId));
        cmd.Bind(database, "g", groupId);
        cmd.Bind(database, "u", userId);
        cmd.ExecuteNonQuery();
    });

    public void RemoveMember(string groupId, string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "DELETE FROM {table:group_member} WHERE group_id = $g AND account_id = $u;");
        cmd.Bind(database, "g", groupId);
        cmd.Bind(database, "u", userId);
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<GroupRecord> GroupsOf(string userId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT g.id, g.name FROM {table:user_group} g JOIN {table:group_member} m ON m.group_id = g.id WHERE m.account_id = $u ORDER BY g.name;");
        cmd.Bind(database, "u", userId);
        using var reader = cmd.ExecuteReader();
        var list = new List<GroupRecord>();
        while (reader.Read()) list.Add(new GroupRecord(reader.GetString(0), reader.GetString(1)));
        return (IReadOnlyList<GroupRecord>)list;
    });

    /// <summary>The members of a group, by user id.</summary>
    public IReadOnlyList<string> MembersOf(string groupId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT account_id FROM {table:group_member} WHERE group_id = $g ORDER BY account_id;");
        cmd.Bind(database, "g", groupId);
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
        return (IReadOnlyList<string>)list;
    });

    /// <summary>Removes a group, its memberships and what was assigned to it (its members lose those roles at once). False when there is no such group.</summary>
    public bool RemoveGroup(string groupId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM {table:group_member} WHERE group_id = $g;", "DELETE FROM {table:role_assignment} WHERE subject_kind = 'group' AND subject_id = $g;" })
        {
            using var cmd = database.Command(Module, connection, transaction, sql);
            cmd.Bind(database, "g", groupId);
            cmd.ExecuteNonQuery();
        }
        using var group = database.Command(Module, connection, transaction, "DELETE FROM {table:user_group} WHERE id = $g;");
        group.Bind(database, "g", groupId);
        var removed = group.ExecuteNonQuery() == 1;
        transaction.Commit();
        return removed;
    });

    /// <summary>Every role assignment, to a user or a group, on a server, a project or an app.</summary>
    public IReadOnlyList<RoleAssignment> Assignments() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT id, subject_kind, subject_id, scope, target, project, roles FROM {table:role_assignment} ORDER BY scope, target, project, subject_kind, subject_id;");
        using var reader = cmd.ExecuteReader();
        var list = new List<RoleAssignment>();
        while (reader.Read())
        {
            ServerRoles.TryParse(reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries), out var roles);
            list.Add(new RoleAssignment(reader.GetString(0), new AssignmentSubject(reader.GetString(1), reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetString(5) is { Length: > 0 } p ? p : null, roles));
        }
        return (IReadOnlyList<RoleAssignment>)list;
    });

    /// <summary>Removes an assignment by its id; the person (or the group's members) lose the roles at once. False when there is no such assignment.</summary>
    public bool RemoveAssignment(string id) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "DELETE FROM {table:role_assignment} WHERE id = $id;");
        cmd.Bind(database, "id", id);
        return cmd.ExecuteNonQuery() == 1;
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
            using (var delete = database.Command(Module, connection, "DELETE FROM {table:group_mapping} WHERE slot = $slot;"))
            {
                delete.Bind(database, "slot", slot);
                delete.ExecuteNonQuery();
            }
            if (roles == ServerRole.None) return;
            using var cmd = database.Command(Module, connection, """
                INSERT INTO {table:group_mapping} (id, slot, source, external_group, external_key, scope, target, project, roles, created_at)
                VALUES ($id, $slot, $source, $group, $key, $scope, $target, $project, $roles, $at);
                """);
            cmd.Bind(database, "id", database.NewId().ToString("N"));
            cmd.Bind(database, "slot", slot);
            cmd.Bind(database, "source", source);
            cmd.Bind(database, "group", group.Trim());
            cmd.Bind(database, "key", key);
            cmd.Bind(database, "scope", scope);
            cmd.Bind(database, "target", target);
            cmd.Bind(database, "project", projectKey);
            cmd.Bind(database, "roles", string.Join(',', ServerRoles.Names(roles)));
            cmd.Bind(database, "at", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Removes a mapping by its id; the people it gave roles lose them at once. False when there is no such mapping.</summary>
    public bool RemoveExternalMapping(string id) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "DELETE FROM {table:group_mapping} WHERE id = $id;");
        cmd.Bind(database, "id", id);
        return cmd.ExecuteNonQuery() == 1;
    });

    public IReadOnlyList<ExternalGroupMapping> ExternalMappings() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT id, source, external_group, scope, target, project, roles FROM {table:group_mapping} ORDER BY source, external_key, scope, target, project;");
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
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:external_membership} WHERE account_id = $u AND source = $source;"))
            {
                delete.Bind(database, "u", userId);
                delete.Bind(database, "source", source);
                delete.ExecuteNonQuery();
            }
            var at = database.Time.GetUtcNow();
            foreach (var key in keys)
            {
                using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:external_membership} (slot, account_id, source, external_key, seen_at) VALUES ($slot, $u, $source, $key, $at);");
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
        using var cmd = database.Command(Module, connection, "SELECT source, external_key FROM {table:external_membership} WHERE account_id = $u ORDER BY source, external_key;");
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
            using var cmd = database.Command(Module, connection, """
                INSERT INTO {table:role_assignment} (id, slot, subject_kind, subject_id, scope, target, project, roles)
                VALUES ($id, $slot, $kind, $subject, $scope, $target, $project, $roles);
                """);
            cmd.Bind(database, "id", database.NewId().ToString("N"));
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
        using var cmd = database.Command(Module, connection, "DELETE FROM {table:role_assignment} WHERE subject_kind = $kind AND subject_id = $subject AND scope = $scope AND target = $target AND project = $project;");
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
        using var cmd = database.Command(Module, connection, """
            SELECT scope, target, project, roles FROM {table:role_assignment}
            WHERE (subject_kind = 'user' AND subject_id = $u) OR (subject_kind = 'group' AND subject_id IN (SELECT group_id FROM {table:group_member} WHERE account_id = $u))
            UNION ALL
            SELECT m.scope, m.target, m.project, m.roles FROM {table:group_mapping} m
            WHERE EXISTS (SELECT 1 FROM {table:external_membership} e WHERE e.account_id = $u AND e.source = m.source AND e.external_key = m.external_key);
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
        using var cmd = database.Command(Module, connection, """
            SELECT scope, project, roles FROM {table:role_assignment}
            WHERE target = $target AND (scope = $scope OR scope = $projectScope)
              AND ((subject_kind = 'user' AND subject_id = $u) OR (subject_kind = 'group' AND subject_id IN (SELECT group_id FROM {table:group_member} WHERE account_id = $u)))
            UNION ALL
            SELECT m.scope, m.project, m.roles FROM {table:group_mapping} m
            WHERE m.target = $target AND (m.scope = $scope OR m.scope = $projectScope)
              AND EXISTS (SELECT 1 FROM {table:external_membership} e WHERE e.account_id = $u AND e.source = m.source AND e.external_key = m.external_key);
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

/// <summary>One role assignment: to a user or a group, on a server, a Build project of it (<paramref name="Project"/>) or an app.</summary>
/// <param name="Id">The assignment's id (to remove it).</param>
/// <param name="Subject">The user or group that holds the roles.</param>
/// <param name="Scope">server, project or app.</param>
/// <param name="Target">The server's instance id, or the app.</param>
/// <param name="Project">The Build project when <paramref name="Scope"/> is project; otherwise null.</param>
/// <param name="Roles">The roles assigned there.</param>
public sealed record RoleAssignment(string Id, AssignmentSubject Subject, string Scope, string Target, string? Project, ServerRole Roles);

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
