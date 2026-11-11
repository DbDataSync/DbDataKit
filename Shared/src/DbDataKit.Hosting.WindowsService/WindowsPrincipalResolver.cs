using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DbDataKit.Hosting.WindowsService;

/// <summary>The per-service SID (<c>NT SERVICE\Name</c>), which Windows derives from the name alone.</summary>
public static class ServiceSid
{
    /// <summary>
    /// <c>S-1-5-80-a-b-c-d-e</c>: SHA-1 of the upper-cased service name in UTF-16LE, read as five little-endian
    /// DWORDs. Pure computation: works before the service exists and on any OS (same value as <c>sc showsid</c>).
    /// </summary>
    public static string FromName(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var sb = new StringBuilder("S-1-5-80");
        for (var i = 0; i < 5; i++)
        {
            sb.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)));
        }

        return sb.ToString();
    }
}

/// <summary>
/// Real <see cref="IPrincipalResolver"/>: well-known groups plus the SID of the service account. Virtual
/// accounts are computed (no lookup); other accounts are translated through the account database.
/// </summary>
public sealed class WindowsPrincipalResolver : IPrincipalResolver
{
    private const char BackSlash = (char)92;
    private readonly string _serviceSid;

    public WindowsPrincipalResolver(ServiceAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _serviceSid = account.Kind switch
        {
            ServiceAccountKind.VirtualAccount => ServiceSid.FromName(account.Name[(account.Name.LastIndexOf(BackSlash) + 1)..]),
            ServiceAccountKind.LocalSystem => WellKnownPrincipalResolver.SystemSid,
            ServiceAccountKind.LocalService => "S-1-5-19",
            ServiceAccountKind.NetworkService => "S-1-5-20",
            _ => Translate(account.Name),
        };
    }

    public string ResolveSid(LayoutPrincipal principal) =>
        new WellKnownPrincipalResolver(_serviceSid).ResolveSid(principal);

    private static string Translate(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Account lookup is Windows-only.");
        return TranslateWindows(name);
    }

    [SupportedOSPlatform("windows")]
    private static string TranslateWindows(string name) =>
        ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
}
