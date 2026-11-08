using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataConsole.Servers.Tests;

/// <summary>The management audit of a Gateway that runs in <paramref name="gatewayDirectory"/>, oldest first: in its state database (the <c>audit</c> module), as the JSON each entry is kept as.</summary>
internal static class GatewayAudit
{
    public static List<JsonNode> Read(string gatewayDirectory)
    {
        var state = StateDatabase.OpenModules(Path.Combine(gatewayDirectory, "state.db"), [AuditModule.Module], new StateScope(Group: "main", Service: "dbdatagateway"));
        var entries = new AuditLog(state, "dbdatagateway").Read(new AuditQuery(ServiceId: "dbdatagateway", VerbPrefix: "management.", Limit: 1000));
        return [.. entries.Reverse().Where(e => e.Entry.Detail is not null).Select(e => JsonNode.Parse(e.Entry.Detail!)!)];
    }
}
