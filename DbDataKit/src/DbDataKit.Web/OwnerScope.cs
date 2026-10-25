namespace DbDataKit.Web.Trust;

/// <summary>What a call does to an object a server keeps (a data source, a connection).</summary>
public enum ObjectAction
{
    Create,
    Change,
    Delete,
}

/// <summary>The part of an object the owner rules look at.</summary>
/// <param name="ManagedBy">The id of the client that created it (<c>managed_by</c>); null or empty for one made by hand, by an administrator or by the server itself.</param>
/// <param name="IsServerPolicy">True for what governs the whole server and is never a Publisher's to change, such as the Proxy's serving policy (<c>extensions.proxy</c>).</param>
public sealed record ManagedObject(string? ManagedBy, bool IsServerPolicy = false);

/// <summary>
/// The owner rules of a server client that publishes into a server (plan 13, W12, "Ownership-scoped roles"). A client whose role is <see cref="ServerRole.Publisher"/> may create objects, each marked <c>managed_by: &lt;its client id&gt;</c>
/// (<see cref="Stamp"/>), and may change or delete only the objects carrying its own id. It cannot touch an object made by hand or by another client, nor the server's policy. Taking an object over is an administrator's act on the server
/// (<see cref="AuthorizeAdopt"/>, <c>--adopt</c>), never a Publisher's. The check is made by the server on every call, from the token, so a console cannot widen it and the called server needs no trust in the caller's own account.
/// <para>
/// A caller whose effective roles include <see cref="ServerRole.Admin"/> is not limited by owner: that is a person with the server's own authority, and a server client's ceiling never holds it.
/// </para>
/// </summary>
public static class OwnerScope
{
    /// <summary>What to write as <c>managed_by</c> on an object this call creates: the paired client's id.</summary>
    public static string Stamp(ValidatedToken token) => token.Client.Id;

    public static AuthorizationDecision Authorize(ValidatedToken token, ObjectAction action, ManagedObject? target = null, string? project = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var effective = ManagementAuthorization.Effective(token, project);
        if (effective.HasFlag(ServerRole.Admin)) return AuthorizationDecision.Allow(ServerRole.Admin, effective);

        if (!effective.HasFlag(ServerRole.Publisher))
            return AuthorizationDecision.Deny(ServerRole.Publisher, effective, "publishing needs the publisher role (or admin) and the call holds neither");

        if (action == ObjectAction.Create)
            return AuthorizationDecision.Allow(ServerRole.Publisher, effective);

        if (target is null)
            return AuthorizationDecision.Deny(ServerRole.Publisher, effective, "the object to change is not named, so nothing says it is this client's");
        if (target.IsServerPolicy)
            return AuthorizationDecision.Deny(ServerRole.Publisher, effective, "the server's own policy is not something a publisher changes");
        if (string.IsNullOrEmpty(target.ManagedBy))
            return AuthorizationDecision.Deny(ServerRole.Publisher, effective, "the object was not made by a publisher, so it is not this client's to change");
        return string.Equals(target.ManagedBy, token.Client.Id, StringComparison.Ordinal)
            ? AuthorizationDecision.Allow(ServerRole.Publisher, effective)
            : AuthorizationDecision.Deny(ServerRole.Publisher, effective, "the object belongs to another client");
    }

    /// <summary>Taking an object over (<c>--adopt</c>): an administrator's act. A server client never has it, whatever its ceiling.</summary>
    public static AuthorizationDecision AuthorizeAdopt(ValidatedToken token, string? project = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var effective = ManagementAuthorization.Effective(token, project);
        if (token.Client.Kind != ClientKind.Console && token.Client.Kind != ClientKind.Cli)
            return AuthorizationDecision.Deny(ServerRole.Admin, effective, "adopting an object is a person's act, not a server client's");
        return effective.HasFlag(ServerRole.Admin)
            ? AuthorizationDecision.Allow(ServerRole.Admin, effective)
            : AuthorizationDecision.Deny(ServerRole.Admin, effective, "adopting an object needs the admin role");
    }
}
