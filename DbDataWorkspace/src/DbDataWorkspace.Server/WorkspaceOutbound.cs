using DbDataKit.Outbound;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Server;

/// <summary>
/// The Workspace server's outbound list: the <c>outbound:</c> setting of <c>server.yaml</c>, which names the package sources and the projects' git remotes. The server registers one
/// <see cref="OutboundGate"/> for the code of its own process; a destination that is not listed is refused before anything connects, and the refusal is written to the management audit.
/// Programs the server starts (<c>dbdatabuild</c>, <c>dbdatasync</c>, <c>dbdatarunner</c>, <c>dotnet</c>, Bun) open their own connections, which the gate does not see.
/// </summary>
public static class WorkspaceOutbound
{
    /// <summary>The service a refusal names.</summary>
    public const string Service = "workspace";

    /// <summary>The audit command of a refusal.</summary>
    public const string AuditCommand = "outbound.refused";

    /// <summary>The list in <paramref name="serverYaml"/>; no file or no line: every destination. A value that is not valid fails naming the file and the entry.</summary>
    public static OutboundList Load(string serverYaml) => OutboundSettings.Load(serverYaml);

    /// <summary>
    /// Writes a refusal to the management audit, as the server itself: the command <c>outbound.refused</c>, the destination (host and port) as the target and the operation in the outcome.
    /// The path, the query and the login of the address are never in it.
    /// </summary>
    public sealed class Audit(IAuditLog log) : IOutboundAudit
    {
        public void Refused(OutboundRefusal refusal) =>
            log.Append(new AuditEntry(refusal.At, "server", "the Workspace server", WorkspaceSettings.Product, null, false, AuditCommand,
                new OutboundDestination(refusal.Host, refusal.Port).ToString(), "refused: " + refusal.Operation, ""));
    }
}
