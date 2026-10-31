using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using DbDataKit.State;

namespace DbDataKit.Web.Auth;

/// <param name="Sid">The security identifier: the subject a user is keyed on.</param>
/// <param name="Name">The account name, for display (a SAM name, <c>DOMAIN\user</c>, which is never matched on).</param>
/// <param name="Upn">The user principal name (<c>user@domain</c>) the domain issued for the account, or null when it carries none. It is the account's key (plan 13a U16): it is what makes the same person, signing in with a passkey
/// or through the identity home, one user.</param>
/// <param name="Role">Null when this identity is in neither configured group — which is "not a user
/// here", not "a user with no permissions".</param>
public sealed record WindowsIdentityResult(string Sid, string Name, UserRole? Role, string? Upn = null);

/// <summary>
/// Turning a negotiated Windows identity into one of this application's users.
/// <para>
/// **Group membership is evaluated per sign-in and again on every request**, never baked into a
/// session at sign-in: removing somebody from the admin group has to remove their access without
/// waiting for a cookie to expire. The session carries the user id; the role comes from the user row,
/// which the Windows path keeps in step.
/// </para>
/// <para>
/// The **SID** is the subject, not the account name. Names get renamed and reused; a SID does not, and
/// keying on a name is how a new employee inherits a former one's access.
/// </para>
/// </summary>
public static class WindowsSignIn
{
    /// <summary>
    /// What this Windows principal is entitled to, or null when it is not one of ours.
    /// <para>
    /// Returns null off Windows too, rather than throwing: a Linux deployment simply does not have
    /// this method available, and the endpoint that calls it says so.
    /// </para>
    /// </summary>
    public static WindowsIdentityResult? Resolve(ClaimsPrincipal principal, AuthOptions options)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        return ResolveOnWindows(principal, options);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsIdentityResult? ResolveOnWindows(ClaimsPrincipal principal, AuthOptions options)
    {
        if (principal.Identity is not WindowsIdentity { IsAuthenticated: true } identity)
            return null;

        var windowsPrincipal = new WindowsPrincipal(identity);
        var sid = identity.User?.Value;
        if (sid is null)
            return null;

        // Both checked, and admin wins. Somebody in both groups was put in the admin group by
        // somebody who meant it.
        var isAdmin = !string.IsNullOrWhiteSpace(options.AdminGroup) && windowsPrincipal.IsInRole(options.AdminGroup);
        var isViewer = !string.IsNullOrWhiteSpace(options.ViewerGroup) && windowsPrincipal.IsInRole(options.ViewerGroup);

        var role = isAdmin ? UserRole.Admin : isViewer ? UserRole.Viewer : (UserRole?)null;
        // The UPN is the domain's own claim in the Kerberos ticket, not anything the browser sent: that is why it may link accounts. Only an address counts; a SAM name in its place is no key.
        var upn = identity.FindFirst(ClaimTypes.Upn)?.Value;
        return new WindowsIdentityResult(sid, identity.Name ?? sid, role, AccountKey.Normalize(upn) is null ? null : upn);
    }

    /// <summary>
    /// The user this Windows account is, created on first sign-in and kept in step with the directory after (plan 13a U16: through <see cref="AccountLinker"/>, so the account is the one that holds the person's UPN, whichever way it
    /// was made). Throws <see cref="AccountConflictException"/> with a message for the person when the account cannot be matched (no UPN) or its UPN is another account's.
    /// <para>
    /// The role is written back on every sign-in because the group is the authority — a user moved
    /// from the viewer group to the admin group should be an admin the next time they sign in, without
    /// anybody editing them here.
    /// </para>
    /// </summary>
    public static UserRecord Upsert(UserStore users, WindowsIdentityResult identity, UserRole role) =>
        new AccountLinker(users).SignInWindows(identity.Sid, identity.Name, identity.Upn, role);
}
