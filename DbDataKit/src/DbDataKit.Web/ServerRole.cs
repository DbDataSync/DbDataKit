namespace DbDataKit.Web.Trust;

/// <summary>
/// What a user may do on a server (plan 13, W12). A role is a set, not a ladder: <see cref="Author"/> and <see cref="Approver"/> are about a Build project's files and plans and neither
/// contains the other, nor <see cref="Admin"/>. What a role implies is in <see cref="ServerRoles.Close"/>.
/// </summary>
[Flags]
public enum ServerRole
{
    None = 0,
    /// <summary>Reads: status, configuration, runs, the catalog.</summary>
    Viewer = 1,
    /// <summary>Operations: run now, pause a schedule, cancel a query, restart.</summary>
    Operator = 2,
    /// <summary>Project file writes and commits (Build).</summary>
    Author = 4,
    /// <summary>Approving and applying a Build plan.</summary>
    Approver = 8,
    /// <summary>Configuration changes: connections, data sources, replications, schedules, certificates.</summary>
    Admin = 16,
}

public static class ServerRoles
{
    /// <summary>The role with everything it implies added: Operator and Author and Approver can read; Admin can operate and read.</summary>
    public static ServerRole Close(ServerRole role)
    {
        if (role.HasFlag(ServerRole.Admin)) role |= ServerRole.Operator;
        if (role.HasFlag(ServerRole.Operator) || role.HasFlag(ServerRole.Author) || role.HasFlag(ServerRole.Approver)) role |= ServerRole.Viewer;
        return role;
    }

    /// <summary>The roles in <paramref name="claimed"/> that <paramref name="ceiling"/> does not allow (empty when it allows them all).</summary>
    public static ServerRole Above(ServerRole claimed, ServerRole ceiling) => Close(claimed) & ~Close(ceiling);

    /// <summary>What both allow: the lower of a user's role and a console's ceiling.</summary>
    public static ServerRole Lower(ServerRole role, ServerRole ceiling) => Close(role) & Close(ceiling);

    public static bool Allows(ServerRole held, ServerRole needed) => needed != ServerRole.None && (Close(held) & needed) == needed;

    public static IReadOnlyList<string> Names(ServerRole role) =>
        Enum.GetValues<ServerRole>().Where(r => r != ServerRole.None && role.HasFlag(r)).Select(r => r.ToString().ToLowerInvariant()).ToList();

    /// <summary>Parses role names; a name that is not a role is an error, never ignored.</summary>
    public static bool TryParse(IEnumerable<string> names, out ServerRole role)
    {
        role = ServerRole.None;
        foreach (var name in names)
        {
            if (!Enum.TryParse<ServerRole>(name, ignoreCase: true, out var one) || one == ServerRole.None || !Enum.IsDefined(one) || name.Contains(',')) { role = ServerRole.None; return false; }
            role |= one;
        }
        return true;
    }
}
