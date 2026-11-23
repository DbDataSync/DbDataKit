using System.Text.Json.Nodes;
using DbDataKit.Deploy;

namespace DbDataKit.Authoring.Server;

public sealed partial class WorkspacePublishing
{
    /// <summary>
    /// The shape of a data source's catalog from a paired Gateway (plan 33 SC20j, D20): <c>POST /api/v1/data-sources/{name}/schema</c> as the paired publisher client, which the Gateway answers with the tables, columns and keys it reads with its own login. The Workspace
    /// holds no login and reads no row. A data source the client did not make is asked for with <paramref name="adopt"/> (the person's confirmation, which the Gateway records); without it the Gateway's refusal comes back with the data source and who made it, as
    /// for a publish (SC21f). The target must be a Gateway. The call is the shared deploy library's (<see cref="GatewayCalls.ReadSchemaAsync"/>).
    /// </summary>
    public async Task<(JsonObject Schema, PublishTarget Target)> ReadSchemaAsync(string targetName, string dataSource, bool adopt, Action<string> step, CancellationToken ct = default)
    {
        var (target, key, role) = ResolveTarget(targetName, "the catalog of a data source");
        var deployTarget = DeployTargetOf(target);
        WorkspacePublishException.Adapt(() => GatewayCalls.RequireForSchema(deployTarget));
        step("Read the publisher key '" + targetName + "' from the secret store.");
        var identity = await KeyAsync(targetName, key.SecretRef, ct);
        using var link = new PinnedLink(target, identity, "client:" + identity.Id, target.ClientName, role, TimeSpan.FromMinutes(2));
        return (await WorkspacePublishException.AdaptAsync(() => GatewayCalls.ReadSchemaAsync(link, deployTarget, dataSource, adopt, step, ct)), target);
    }
}
