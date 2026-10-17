using System.DirectoryServices;
using System.Runtime.Versioning;

namespace DbDataSync.Certificates;

/// <param name="CaConfig">What <c>--ca</c> and <c>DbDataSync:Certificates:CaConfig</c> take: <c>CASERVER\CA Name</c>.</param>
/// <param name="Templates">The template names this CA publishes — names, not display names.</param>
public sealed record DiscoveredCa(string CaConfig, IReadOnlyList<string> Templates);

/// <param name="Cas">Empty on every failure path — see <paramref name="Reason"/>; also empty, with
/// <see cref="TemplateListReason.Available"/>, when the directory simply publishes no enterprise CA.</param>
public sealed record CaDiscoveryResult(IReadOnlyList<DiscoveredCa> Cas, TemplateListReason Reason, string? Detail);

/// <summary>
/// Finds the enterprise CAs published in Active Directory — what <c>dbdatasync config cert discover</c> reads, so
/// an operator does not have to know the <c>CASERVER\CA Name</c> string <see cref="CertificateTemplateCatalog"/>
/// needs before it can list anything. Each CA is one object under the same Enrollment Services container the
/// template listing already queries, carrying its host name (<c>dNSHostName</c>), its name (<c>cn</c>) and the
/// templates it publishes, so one read answers both questions.
/// <para>
/// Read-only, and best-effort in exactly the way <see cref="CertificateTemplateCatalog"/> is: not domain-joined,
/// LDAP unreachable or no read rights each come back as a <see cref="TemplateListReason"/> and a message, never
/// an exception. Like it, the directory query itself is not tested against a real forest.
/// </para>
/// </summary>
public static class CaDiscovery
{
    /// <summary>Not marked <see cref="SupportedOSPlatformAttribute"/>, for the same reason
    /// <see cref="CertificateTemplateCatalog.List"/> is not: a non-Windows caller gets a real reason back.</summary>
    public static CaDiscoveryResult Discover()
    {
        if (!OperatingSystem.IsWindows())
            return new CaDiscoveryResult([], TemplateListReason.Unknown, "Certificate management is Windows-only.");

        return DiscoverOnWindows();
    }

    [SupportedOSPlatform("windows")]
    private static CaDiscoveryResult DiscoverOnWindows()
    {
        var (cas, reason, detail) = CertificateTemplateCatalog.QueryEnrollmentServices(servicesPath =>
        {
            using var container = new DirectoryEntry($"LDAP://{servicesPath}");

            var found = new List<DiscoveredCa>();
            foreach (DirectoryEntry child in container.Children)
            {
                using (child)
                {
                    var name = child.Properties["cn"].Value as string;
                    var host = child.Properties["dNSHostName"].Value as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
                        continue;

                    found.Add(new DiscoveredCa($"{host}\\{name}", CertificateTemplateCatalog.TemplatesOf(child)));
                }
            }

            return found;
        });

        return reason == TemplateListReason.Available
            ? new CaDiscoveryResult(cas!, TemplateListReason.Available, null)
            : new CaDiscoveryResult([], reason, detail);
    }
}
