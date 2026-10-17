using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataProxy.Server.Tls;

/// <summary>Who may read the proxy's key material: whoever runs it now, the service identity, SYSTEM and Administrators.</summary>
public static class ProxyAcl
{
    /// <summary>
    /// Identities for key files. The service SID is derived from the name (it exists whether or not the service
    /// does yet), plus the account the installed service runs as when it is something else (a gMSA, say), so an
    /// administrator renewing the certificate never locks the service out.
    /// </summary>
    public static IReadOnlyList<string> KeyFileIdentities(string serviceName)
    {
        var identities = new List<string> { "CurrentUser", "SYSTEM", "Administrators" };
        if (!OperatingSystem.IsWindows()) return identities;

        identities.Add(ServiceSid.FromName(serviceName));
        var installed = InstalledAccount(serviceName);
        if (installed is not null && TryResolve(installed)) identities.Add(installed);
        return identities;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? InstalledAccount(string serviceName)
    {
        try { return new WindowsServiceControlManager().GetConfig(serviceName)?.AccountName; }
        catch (Exception ex) when (ex is ServiceControlException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private static bool TryResolve(string identity)
    {
        try { AclHelper.ResolveIdentity(identity); return true; }
        catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException or ArgumentException) { return false; }
    }
}
