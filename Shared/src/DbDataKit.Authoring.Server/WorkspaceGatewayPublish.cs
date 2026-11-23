using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The Workspace's publish of what a Store or a Build project made, over plan 30's shared route (plan 33 SW01), signed with the target's publisher key. The publish itself is the shared deploy library's
/// <see cref="GatewayDefinitionsPublish"/>; this class gives its refusals as a <see cref="WorkspacePublishException"/>.
/// </summary>
public sealed class WorkspaceGatewayPublish(IDefinitionsLink link, ConsoleIdentity identity, Action<string> step, CancellationToken ct = default)
{
    public const string ModelKind = GatewayDefinitionsPublish.ModelKind;
    public const string EntityModelKind = GatewayDefinitionsPublish.EntityModelKind;
    public const string DataSourceKind = GatewayDefinitionsPublish.DataSourceKind;

    private readonly GatewayDefinitionsPublish publish = new(link, identity, step, ct);

    /// <summary>The Store project's models (<paramref name="models"/>: file name and text) to the Gateway as <c>entity-model</c> definitions (<see cref="GatewayDefinitionsPublish.PublishEntityModelsAsync"/>).</summary>
    public Task<JsonArray> PublishEntityModelsAsync(IReadOnlyList<(string File, string Text)> models, string? dataSource, IReadOnlyList<string>? adopt) =>
        WorkspacePublishException.AdaptAsync(() => publish.PublishEntityModelsAsync(models, dataSource, adopt));

    /// <summary>The compiled semantic models of a Build project to the Gateway (<see cref="GatewayDefinitionsPublish.PublishSemanticModelsAsync"/>).</summary>
    public Task<(JsonArray Models, JsonArray DataSources)> PublishSemanticModelsAsync(string folder, IReadOnlyList<string>? adopt) =>
        WorkspacePublishException.AdaptAsync(() => publish.PublishSemanticModelsAsync(folder, adopt));

    /// <summary>The dashboards and reports of a Build project to the Gateway (<see cref="GatewayDefinitionsPublish.PublishViewsAsync"/>).</summary>
    public Task<JsonArray> PublishViewsAsync(string folder) =>
        WorkspacePublishException.AdaptAsync(() => publish.PublishViewsAsync(folder));
}
