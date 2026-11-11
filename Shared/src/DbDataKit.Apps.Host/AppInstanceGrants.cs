using DbDataKit.Identity;

namespace DbDataKit.Apps;

/// <summary>
/// What the approved manifest of an app instance allows, as the identity home needs it to make the instance's access token (plan 26 AP19; <see cref="IAppGrants"/>): the scope of the manifest's <c>models</c> of the version the instance
/// runs (<see cref="AppTokenBroker.ScopeOf"/>), which the home then narrows to the roles the person holds on the instance. Null when the instance does not exist, runs no version, or runs a trusted app no approval stands for
/// (<see cref="AppApprovalStore.Gate"/>), so the home issues nothing for it, exactly as <see cref="AppDataGateway"/> refuses the host's own tokens for it.
/// </summary>
public sealed class AppInstanceGrants(AppInstanceStore instances, AppApprovalStore? approvals = null) : IAppGrants
{
    public IReadOnlyList<string>? ScopeOf(string instance)
    {
        if (instances.Resolve(instance) is not { } resolved) return null;
        if (resolved.Manifest.Trust == AppTrust.Trusted && approvals?.Gate(resolved) is not { Allowed: true }) return null;
        return AppTokenBroker.ScopeOf(resolved.Manifest);
    }
}
