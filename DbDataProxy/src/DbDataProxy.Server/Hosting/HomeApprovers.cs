using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// The approvers of trusted apps (plan 26 section 2a, AP15) on the identity home this proxy serves: the person is a user of the home and holds the Approver role on this server (<c>server:&lt;id&gt;</c>, the id the group shares), the same
/// roles the Store's callers and the management API check. The user is named by id, email or display name.
/// </summary>
public sealed class HomeApprovers(IdentityUserStore users, HomeDirectory directory, string serverId) : IAppApprovers
{
    public static HomeApprovers Open(StateDatabase state, ProxyOptions options) => new(new IdentityUserStore(state), new HomeDirectory(state), ProxyStoreModule.ServerIdOf(options, state));

    public AppApproverLookup Find(string who)
    {
        var matches = new List<UserRecord>();
        if (users.Get(who) is { } byId) matches.Add(byId);
        else matches.AddRange(users.List().Where(u => string.Equals(u.Email, who, StringComparison.OrdinalIgnoreCase) || string.Equals(u.DisplayName, who, StringComparison.OrdinalIgnoreCase)));
        if (matches.Count == 0) return new AppApproverLookup(null, $"'{who}' is not a user of the identity home, so cannot approve; the approver is a user who holds the Approver role on this server.");
        if (matches.Count > 1) return new AppApproverLookup(null, $"More than one user of the identity home is called '{who}'; name the user by id or email.");
        var user = matches[0];
        if (!user.Enabled) return new AppApproverLookup(null, $"The user '{user.DisplayName}' is disabled and cannot approve an app.");
        return directory.RolesOn(user.Id, serverId).Roles.HasFlag(ServerRole.Approver)
            ? new AppApproverLookup(new AppApprover(user.Id, user.DisplayName), null)
            : new AppApproverLookup(null, $"The user '{user.DisplayName}' does not hold the Approver role on this server, so cannot approve an app. Give it with: dbdataproxy identity (assign the Approver role).");
    }
}
