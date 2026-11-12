using DbDataKit.Identity;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps;

/// <summary>A token the shell hands the sandboxed app, with the seconds it lives: the identity home's access token for one instance (plan 26 AP19).</summary>
public sealed record AppToken(string Token, int ExpiresInSeconds, string Audience);

/// <summary>What a manifest's <c>models</c> grant, as the scope strings tokens carry.</summary>
public static class AppManifestScope
{
    /// <summary>The scope a manifest's <c>models</c> grant: <c>model/entity:read</c> or <c>:write</c> for each entity it lists (<c>:propose</c> for a change request only).</summary>
    public static IReadOnlyList<string> Of(AppManifest manifest) =>
        [.. manifest.Models.SelectMany(m => m.Entities.Where(e => e.Value != DataOperations.None).Select(e =>
            e.Value.HasFlag(DataOperations.ChangeRequest) && !e.Value.HasFlag(DataOperations.Write) ? TokenScope.ProposeEntry(m.Model, e.Key) : TokenScope.Entry(m.Model, e.Key, write: e.Value.HasFlag(DataOperations.Write))))];
}

/// <summary>
/// What the approved manifest of an app instance allows, as the identity home needs it to make the instance's access token (plan 26 AP19; <see cref="IAppGrants"/>): the scope of the manifest's <c>models</c> of the version the instance
/// runs (<see cref="AppManifestScope.Of"/>), which the home then narrows to the roles the person holds on the instance. Null when the instance does not exist, runs no version, or runs a trusted app no approval stands for
/// (<see cref="AppApprovalStore.Gate"/>), so the home issues nothing for it, exactly as <see cref="AppDataGateway"/> refuses a call for it.
/// </summary>
public sealed class AppInstanceGrants(AppInstanceStore instances, AppApprovalStore? approvals = null) : IAppGrants
{
    public IReadOnlyList<string>? ScopeOf(string instance)
    {
        if (instances.Resolve(instance) is not { } resolved) return null;
        if (resolved.Manifest.Trust == AppTrust.Trusted && approvals?.Gate(resolved) is not { Allowed: true }) return null;
        return AppManifestScope.Of(resolved.Manifest);
    }
}
