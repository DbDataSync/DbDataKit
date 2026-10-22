using DbDataKit.Cli;

namespace DbDataKit.Web.Trust;

/// <summary>What a management call is allowed to do, and why not when it is not. <see cref="Reason"/> is for the server's log: the caller is told only that it was refused.</summary>
/// <param name="Allowed">True when the call may go on.</param>
/// <param name="Needed">The role the call needs.</param>
/// <param name="Reason">Why not, for the log; null when allowed.</param>
/// <param name="Effective">The roles the call held: the lower of the user's roles and the console's ceiling, with what they imply.</param>
public sealed record AuthorizationDecision(bool Allowed, ServerRole Needed, ServerRole Effective, string? Reason)
{
    public static AuthorizationDecision Allow(ServerRole needed, ServerRole effective) => new(true, needed, effective, null);
    public static AuthorizationDecision Deny(ServerRole needed, ServerRole effective, string reason) => new(false, needed, effective, reason);
}

/// <summary>
/// Authorization on a product's management API (plan 13, W12 and T05). The role a call needs follows from its command's <see cref="EffectClass"/> (or, for a call that is not a command, from the kind of call);
/// the role the call holds is the lower of the user's role in the token and the console's ceiling. The console hides what a user may not do; this is the check on the server, so a console that is wrong
/// or a client that is not a console gets the same answer.
/// </summary>
public static class ManagementAuthorization
{
    /// <summary>
    /// The role a command of this effect class needs. Reading, offline or against a target, is <see cref="ServerRole.Viewer"/>; changing the files of a project is <see cref="ServerRole.Author"/>; refreshing data is an
    /// operation (<see cref="ServerRole.Operator"/>); changing the tool's own tracking tables is configuration (<see cref="ServerRole.Admin"/>); and anything that changes a target's structure or
    /// data under a plan is applying a plan (<see cref="ServerRole.Approver"/>).
    /// </summary>
    public static ServerRole RoleFor(EffectClass effect) => effect switch
    {
        EffectClass.OfflineOnly => ServerRole.Viewer,
        EffectClass.TargetReadOnly => ServerRole.Viewer,
        EffectClass.RepoFilesOnly => ServerRole.Author,
        EffectClass.TargetDataWrites => ServerRole.Operator,
        EffectClass.TrackingTablesOnly => ServerRole.Admin,
        EffectClass.TargetWrites => ServerRole.Approver,
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "an effect class without a role would be allowed by accident"),
    };

    /// <summary>The roles a call holds in a project (or on the server when <paramref name="project"/> is null): the user's server roles with the project's added, limited by the console's ceiling.</summary>
    public static ServerRole Effective(ValidatedToken token, string? project = null)
    {
        var held = token.Roles;
        if (project is not null && token.Projects.TryGetValue(project, out var inProject)) held |= inProject;
        return ServerRoles.Lower(held, token.Console.Ceiling);
    }

    public static AuthorizationDecision Authorize(ValidatedToken token, ServerRole needed, string? project = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var effective = Effective(token, project);
        if (needed == ServerRole.None) return AuthorizationDecision.Deny(needed, effective, "the call names no role, so nothing allows it");
        return ServerRoles.Allows(effective, needed)
            ? AuthorizationDecision.Allow(needed, effective)
            : AuthorizationDecision.Deny(needed, effective, $"{string.Join('+', ServerRoles.Names(needed))} is needed and the call holds {(effective == ServerRole.None ? "nothing" : string.Join('+', ServerRoles.Names(effective)))}");
    }

    /// <summary>Authorizes a command by its effect class.</summary>
    public static AuthorizationDecision Authorize(ValidatedToken token, CommandSpec command, string? project = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Authorize(token, RoleFor(command.Effect), project);
    }
}
