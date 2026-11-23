using DbDataKit.Deploy;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// Where a published version came from, in a form the Runner's screens make a link of (plan 33 SC20o, SC22l: the project page and the Runner's screens link straight to each other, so nobody hunts for either): the token
/// <c>[workspace:&lt;instance&gt;/&lt;project&gt;/&lt;workspace&gt;]</c> at the end of the version's note (<see cref="DeployOrigin.Token"/>).
/// </summary>
public static class WorkspaceOrigin
{
    public static string Token(string instanceId, string project, string workspace) => DeployOrigin.Token(instanceId, project, workspace);
}
