namespace DbDataKit.Authoring.Server;

/// <summary>
/// Where a published version came from, in a form the Runner's screens make a link of (plan 33 SC20o, SC22l: the project page and the Runner's screens link straight to each other, so nobody hunts for either): the token
/// <c>[workspace:&lt;instance&gt;/&lt;project&gt;/&lt;workspace&gt;]</c> at the end of the version's note, each part escaped as a URL path segment. The console's address of the workspace is
/// <c>/servers/&lt;instance&gt;/&lt;project&gt;/&lt;workspace&gt;</c>; <c>Shared/web/runner/src/origin.ts</c> reads the token.
/// </summary>
public static class WorkspaceOrigin
{
    public static string Token(string instanceId, string project, string workspace) =>
        $"[workspace:{Uri.EscapeDataString(instanceId)}/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(workspace)}]";
}
