namespace DbDataKit.Apps;

/// <summary>A person of the identity home who may approve apps: the home's id and the name the audit shows.</summary>
public sealed record AppApprover(string Id, string Name);

/// <summary>What looking up an approver found: the person, or the reason there is none (naming the user and the role).</summary>
public sealed record AppApproverLookup(AppApprover? Approver, string? Refusal);

/// <summary>
/// Who may approve a trusted app (plan 26 section 2a, AP15): a user of the identity home who holds the Approver role. The commands use this interface; the Proxy implements it on the home's roles and tests use <see cref="InMemoryApprovers"/>.
/// </summary>
public interface IAppApprovers
{
    /// <summary>Finds the user by the id, the email or the name given as <c>--by</c>, and checks the role.</summary>
    AppApproverLookup Find(string who);
}

/// <summary>An approvers list held in memory, for tests and tools without a home: each user has an id, a name and whether they hold the Approver role.</summary>
public sealed class InMemoryApprovers : IAppApprovers
{
    private readonly List<(AppApprover Who, bool IsApprover)> _users = [];

    public InMemoryApprovers Add(string id, string name, bool approver)
    {
        _users.Add((new AppApprover(id, name), approver));
        return this;
    }

    public AppApproverLookup Find(string who)
    {
        var found = _users.Where(u => string.Equals(u.Who.Id, who, StringComparison.OrdinalIgnoreCase) || string.Equals(u.Who.Name, who, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) return new AppApproverLookup(null, $"'{who}' is not a user of the identity home, so cannot approve; the approver is a user who holds the Approver role.");
        if (found.Count > 1) return new AppApproverLookup(null, $"More than one user of the identity home is called '{who}'; name the user by id or email.");
        return found[0].IsApprover ? new AppApproverLookup(found[0].Who, null) : new AppApproverLookup(null, $"'{who}' does not hold the Approver role, so cannot approve an app.");
    }
}
