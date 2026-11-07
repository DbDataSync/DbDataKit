using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataConsole.Servers.Tests;

/// <summary>The management audit of a Proxy that runs in <paramref name="proxyDirectory"/>, oldest first: in its state database (the <c>audit</c> module), as the JSON each entry is kept as.</summary>
internal static class ProxyAudit
{
    public static List<JsonNode> Read(string proxyDirectory)
    {
        var state = StateDatabase.OpenModules(Path.Combine(proxyDirectory, "state.db"), [AuditModule.Module], new StateScope(Group: "main", Service: "dbdataproxy"));
        var entries = new AuditLog(state, "dbdataproxy").Read(new AuditQuery(ServiceId: "dbdataproxy", VerbPrefix: "management.", Limit: 1000));
        return [.. entries.Reverse().Where(e => e.Entry.Detail is not null).Select(e => JsonNode.Parse(e.Entry.Detail!)!)];
    }
}
